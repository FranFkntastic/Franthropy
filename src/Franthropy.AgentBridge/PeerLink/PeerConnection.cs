using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Authentication;
using System.Security.Principal;
using System.Text.Json;

namespace Franthropy.PeerLink;

/// <summary>A logical peer session. Each exchange has its own authenticated local pipe;
/// reconnection never resubmits application messages.</summary>
public sealed class PeerConnection
{
    private readonly PeerEndpoint owner;
    private readonly PeerPresence peer;
    private readonly object gate = new();
    private CancellationTokenSource generation = new();
    private bool retired;
    private PeerState? state;
    public PeerState? State { get { lock (gate) return state; } }
    public event Action<PeerState>? StateChanged;

    internal PeerConnection(PeerEndpoint owner, PeerPresence peer)
    {
        this.owner = owner;
        this.peer = peer;
        _ = owner.Run(Monitor);
    }

    public Task<PeerResult> RequestAsync(Guid requestId, JsonElement payload, string threshold, TimeSpan limit,
        CancellationToken cancellation = default) => Send("request", requestId, payload, threshold, limit, cancellation);

    public Task<PeerResult> EventAsync(JsonElement payload, string threshold, TimeSpan limit,
        CancellationToken cancellation = default) => Send("event", Guid.NewGuid(), payload, threshold, limit, cancellation);

    private Task<PeerResult> Send(string kind, Guid id, JsonElement payload, string threshold, TimeSpan limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threshold);
        if (limit <= TimeSpan.Zero || limit.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(limit));
        if (id == Guid.Empty) throw new ArgumentException("Message ID is required.",nameof(id));
        var start = Stopwatch.GetTimestamp();
        return owner.Run(async () =>
        {
            PeerResult result = await Exchange(owner.Envelope(kind,id,Guid.NewGuid(),payload),threshold,limit,start,ct);
            if (result is not PeerResult.Delivered) Warn(id,result);
            return result;
        });
    }

    private void Warn(Guid id, PeerResult result) =>
        owner.Options.Warning($"Peer {peer.InstanceId}, message {id}: {result}");

    private async Task Monitor()
    {
        var failures = 0;
        while (!owner.Stopping.IsCancellationRequested)
        {
            if (owner.IsWithdrawn(peer))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), owner.Stopping);
                if (owner.IsWithdrawn(peer))
                {
                    bool notify;
                    lock (gate)
                    {
                        retired = true;
                        notify = state != PeerState.Lost;
                        state = PeerState.Lost;
                        generation.Cancel();
                        generation.Dispose();
                    }
                    owner.Forget(peer.InstanceId);
                    if (notify) await Notify(PeerState.Lost);
                    return;
                }
            }
            var id = Guid.NewGuid();
            var probe = await Exchange(owner.Envelope("heartbeat",id,Guid.NewGuid(),PipeProtocol.Empty),
                "HeartbeatTimeout",PipeProtocol.HeartbeatTimeout,Stopwatch.GetTimestamp(),owner.Stopping);
            if (owner.Stopping.IsCancellationRequested) break;
            bool up = probe is PeerResult.Delivered;
            PeerState? changed = null;
            lock (gate)
            {
                if (up && state is null or PeerState.Lost)
                    changed = state = state == null ? PeerState.Connected : PeerState.Recovered;
                else if (!up)
                {
                    if (state != PeerState.Lost) changed = state = PeerState.Lost;
                    // A refused handshake cannot have an accepted application request.
                    // Preserve each caller's precise refusal rather than racing it with LinkLost.
                    if (probe is not PeerResult.Refused)
                    {
                        generation.Cancel();
                        generation.Dispose();
                        generation = new();
                    }
                }
            }
            if (!up) Warn(id,probe);
            if (changed.HasValue) await Notify(changed.Value);
            failures = up ? 0 : Math.Min(failures + 1,5);
            await Task.Delay(up ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(250 << failures),owner.Stopping);
        }
    }

    internal void Release() => generation.Dispose();

    private async Task Notify(PeerState changed)
    {
        try { await owner.Options.Dispatch(() => StateChanged?.Invoke(changed),owner.Stopping); }
        catch (Exception e) when (!owner.Stopping.IsCancellationRequested)
        { owner.Options.Warning($"Peer state callback: {e.GetType().Name}"); }
    }

    private async Task<PeerResult> Exchange(PeerMessage msg,string threshold,TimeSpan limit,long start,CancellationToken ct)
    {
        var remaining = limit - Stopwatch.GetElapsedTime(start);
        if (remaining <= TimeSpan.Zero) return new PeerResult.TimedOut(threshold,limit);
        using var deadline = new CancellationTokenSource(remaining);
        CancellationTokenSource scope;
        CancellationToken session;
        lock (gate)
        {
            if (retired) return new PeerResult.LinkLost("Peer presence withdrawn.");
            if (peer.ProtocolVersion != 1) return new PeerResult.Refused("VersionMismatch","Peer advertised incompatible protocol.");
            session = generation.Token;
            scope = CancellationTokenSource.CreateLinkedTokenSource(ct,owner.Stopping,session,deadline.Token);
        }
        using var lifetime = scope;
        NamedPipeClientStream? pipe = new(".",peer.PipeName,PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,TokenImpersonationLevel.Identification);
        try
        {
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(scope.Token))
            {
                handshake.CancelAfter(PipeProtocol.HandshakeTimeout);
                try
                {
                    await pipe.ConnectAsync(handshake.Token);
                    await PipeProtocol.Authenticate(pipe,owner.Key,peer.InstanceId,owner.InstanceId,handshake.Token);
                }
                catch (OperationCanceledException) when (!scope.IsCancellationRequested)
                { return new PeerResult.TimedOut("HandshakeTimeout",PipeProtocol.HandshakeTimeout); }
            }
            scope.Token.ThrowIfCancellationRequested();
            await PipeProtocol.Write(pipe,msg,scope.Token);
            var reply = PipeProtocol.Read(pipe,owner.Stopping);
            try
            {
                var got = await reply.WaitAsync(scope.Token);
                if (Stopwatch.GetElapsedTime(start) >= limit || deadline.IsCancellationRequested)
                {
                    Late(msg,threshold);
                    return new PeerResult.TimedOut(threshold,limit);
                }
                scope.Token.ThrowIfCancellationRequested();
                if (got.ProtocolVersion != 1 || got.Schema != PipeProtocol.Schema)
                    return new PeerResult.Refused("VersionMismatch","Incompatible response.");
                if (got.SenderInstanceId != peer.InstanceId || got.CorrelationId != msg.CorrelationId)
                    return new PeerResult.Refused("InvalidResponse","Unexpected sender or correlation.");
                return got.Kind switch
                {
                    "response" => new PeerResult.Delivered(got.Payload),
                    "refused" => got.Payload.Deserialize<PeerResult.Refused>() ?? new PeerResult.Refused("InvalidResponse","Empty refusal."),
                    _ => new PeerResult.Refused("UnknownKind","Unexpected reply kind.")
                };
            }
            catch (OperationCanceledException)
            {
                if (deadline.IsCancellationRequested && !owner.Stopping.IsCancellationRequested)
                {
                    var latePipe = pipe;
                    _ = owner.Run(() => ObserveLate(reply,latePipe,msg,threshold,session));
                    pipe = null;
                }
                else _ = reply.ContinueWith(t => _ = t.Exception,TaskContinuationOptions.OnlyOnFaulted);
                throw;
            }
        }
        catch (AuthenticationException e) { return new PeerResult.Refused("AuthenticationFailed",e.Message); }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) return new PeerResult.Cancelled();
            if (deadline.IsCancellationRequested) return new PeerResult.TimedOut(threshold,limit);
            return new PeerResult.LinkLost("Endpoint stopped or peer heartbeat lost.");
        }
        catch (Exception e) { return new PeerResult.LinkLost(e.GetType().Name); }
        finally { pipe?.Dispose(); }
    }

    private void Late(PeerMessage msg,string threshold) =>
        owner.Options.Warning($"LateResponse ignored: peer {peer.InstanceId}, request {msg.MessageId}, attempt {msg.CorrelationId}, threshold {threshold}.");

    private async Task ObserveLate(Task<PeerMessage> read,NamedPipeClientStream pipe,PeerMessage msg,string threshold,CancellationToken session)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(session,owner.Stopping);
        using (pipe)
        {
            try { await read.WaitAsync(TimeSpan.FromMinutes(5),stop.Token); Late(msg,threshold); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception e) when (!stop.IsCancellationRequested)
            { owner.Options.Warning($"LateResponseWatch ended for {msg.MessageId}: {e.GetType().Name}"); }
            finally { pipe.Dispose(); try { await read; } catch { } }
        }
    }
}
