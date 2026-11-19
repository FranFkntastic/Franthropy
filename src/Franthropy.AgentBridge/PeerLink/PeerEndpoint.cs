using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;

namespace Franthropy.PeerLink;

public sealed class PeerEndpoint : IAsyncDisposable
{
    internal readonly PeerOptions Options;
    internal readonly byte[] Key;
    private readonly PresenceStore store;
    private readonly CancellationTokenSource stopping = new();
    private readonly object gate = new();
    private readonly HashSet<Task> jobs = [];
    private readonly Dictionary<Guid, PeerConnection> connections = [];
    private readonly Dictionary<(Guid, Guid), Entry> replay = [];
    private Task? disposal;
    private sealed class Entry(string content, Task<PeerResult> result)
    {
        internal string Content => content;
        internal Task<PeerResult> Result => result;
        internal long Completed;
    }

    public Guid InstanceId => store.Self.InstanceId;
    internal CancellationToken Stopping => stopping.Token;
    internal PeerMessage Envelope(string kind, Guid id, Guid correlation, JsonElement payload) =>
        new(PipeProtocol.Schema, 1, id, correlation, InstanceId, kind, DateTimeOffset.UtcNow, payload);

    private PeerEndpoint(PeerOptions options, PresenceStore presence)
    { Options = options; store = presence; Key = store.LoadKey(); }

    public static Task<PeerEndpoint> StartAsync(PeerOptions options, JsonElement identity, string? dataRoot = null) =>
        Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Application);
            ArgumentNullException.ThrowIfNull(options.Dispatch);
            ArgumentNullException.ThrowIfNull(options.Handle);
            ArgumentNullException.ThrowIfNull(options.Warning);
            var root = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Franthropy", "PeerLink");
            var store = new PresenceStore(root, options.Application, identity);
            var ep = new PeerEndpoint(options,store);
            var listener = PipeProtocol.Listen(store.Self.PipeName);
            try { store.Publish(identity); ep.Run(() => ep.Listen(listener)); }
            catch { listener.Dispose(); CryptographicOperations.ZeroMemory(ep.Key); throw; }
            return ep;
        });

    public Task<List<PeerPresence>> DiscoverAsync() => Run(() => Task.FromResult(store.Discover(Options.Warning)));
    public Task UpdateIdentityAsync(JsonElement identity) =>
        Run(() => { lock (store) store.Publish(identity); return Task.CompletedTask; });

    public PeerConnection Connect(PeerPresence peer)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal != null, this);
            if (peer.Application != Options.Application || peer.PipeName != PresenceStore.Pipe(peer.InstanceId)
                || peer.InstanceId == InstanceId) throw new ArgumentException("Invalid target.", nameof(peer));
            if (!connections.TryGetValue(peer.InstanceId, out var link))
                connections.Add(peer.InstanceId, link = new PeerConnection(this, peer));
            return link;
        }
    }

    // Own every background operation until unload, including reads watching for late evidence.
    internal Task<T> Run<T>(Func<Task<T>> work)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal != null || stopping.IsCancellationRequested, this);
            var task = Task.Run(work);
            jobs.Add(task);
            _ = task.ContinueWith(t => { lock (gate) jobs.Remove(t); }, TaskScheduler.Default);
            return task;
        }
    }
    internal Task Run(Func<Task> work) => Run(async () => { await work(); return true; });

    private async Task Listen(NamedPipeServerStream listener)
    {
        try
        {
            while (!Stopping.IsCancellationRequested)
            {
                await listener.WaitForConnectionAsync(Stopping);
                var accepted = listener;
                listener = PipeProtocol.Listen(store.Self.PipeName);
                try { _ = Run(() => Serve(accepted)); }
                catch { accepted.Dispose(); throw; }
            }
        }
        catch (Exception e) when (!Stopping.IsCancellationRequested) { Options.Warning($"Listener stopped: {e.Message}"); }
        finally { listener.Dispose(); }
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        using var owned = pipe;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Stopping);
        try
        {
            timeout.CancelAfter(PipeProtocol.HandshakeTimeout);
            var sender = await PipeProtocol.Accept(pipe, Key, InstanceId, timeout.Token);
            var msg = await PipeProtocol.Read(pipe, timeout.Token);
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            PeerResult result;
            if (msg.SenderInstanceId != sender) result = new PeerResult.Refused("SenderMismatch","Sender differs from handshake.");
            else if (msg.ProtocolVersion != 1 || msg.Schema != PipeProtocol.Schema)
                result = new PeerResult.Refused("VersionMismatch","Unsupported schema or protocol.");
            else result = msg.Kind switch
            {
                "heartbeat" => new PeerResult.Delivered(PipeProtocol.Empty),
                "request" => await ReplayOrHandle(msg),
                "event" => await Handle(msg),
                _ => new PeerResult.Refused("UnknownKind","Unsupported message kind.")
            };
            var reply = result switch
            {
                PeerResult.Delivered d => Envelope("response", Guid.NewGuid(), msg.CorrelationId, d.Response),
                PeerResult.Refused r => Envelope("refused", Guid.NewGuid(), msg.CorrelationId, JsonSerializer.SerializeToElement(r)),
                _ => throw new InvalidOperationException("Invalid handler outcome.")
            };
            if (result is PeerResult.Refused refusal) Options.Warning($"Inbound {sender}/{msg.MessageId}: {refusal.Code}");
            timeout.CancelAfter(PipeProtocol.HandshakeTimeout);
            await PipeProtocol.Write(pipe,reply,timeout.Token);
        }
        catch (Exception e) when (!Stopping.IsCancellationRequested)
        { Options.Warning($"Inbound pipe {InstanceId}: {(e is OperationCanceledException ? "HandshakeOrFrameTimeout" : e.GetType().Name)}: {e.Message}"); }
    }

    private Task<PeerResult> ReplayOrHandle(PeerMessage msg)
    {
        lock (replay)
        {
            foreach (var key in replay.Where(x => x.Value.Completed != 0 &&
                         Stopwatch.GetElapsedTime(x.Value.Completed) >= TimeSpan.FromMinutes(5)).Select(x => x.Key).ToArray())
                replay.Remove(key);
            var k = (msg.SenderInstanceId,msg.MessageId);
            var content = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(msg.Payload)));
            if (replay.TryGetValue(k,out var old))
                return old.Content == content ? old.Result : Task.FromResult<PeerResult>(new PeerResult.Refused("IdConflict","ID reused with different payload."));
            if (replay.Count >= 4096)
                return Task.FromResult<PeerResult>(new PeerResult.Refused("ReplayCacheFull","Retry window is full."));
            var entry = new Entry(content, Task.Run(() => Handle(msg)));
            replay.Add(k,entry);
            _ = entry.Result.ContinueWith(_ => { lock (replay) entry.Completed = Stopwatch.GetTimestamp(); },TaskScheduler.Default);
            return entry.Result;
        }
    }

    private async Task<PeerResult> Handle(PeerMessage msg)
    {
        PeerResult? result = null;
        try
        {
            await Options.Dispatch(() =>
            {
                Stopping.ThrowIfCancellationRequested();
                result = Options.Handle(msg);
            }, Stopping);
            return result is PeerResult.Delivered or PeerResult.Refused ? result :
                new PeerResult.Refused("InvalidResult","Handler must deliver or refuse.");
        }
        catch (Exception e) { return new PeerResult.Refused("HandlerFailed",e.GetType().Name); }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= Task.Run(Shutdown));
    }
    private async Task Shutdown()
    {
        try
        {
            await stopping.CancelAsync();
            Task[] active;
            lock (gate) active = jobs.ToArray();
            try { await Task.WhenAll(active); } catch (Exception) when (Stopping.IsCancellationRequested) { }
        }
        finally
        {
            foreach (var link in connections.Values) link.Release();
            CryptographicOperations.ZeroMemory(Key);
            stopping.Dispose();
            store.Remove();
        }
    }
}
