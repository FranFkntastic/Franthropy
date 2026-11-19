using Franthropy.PeerLink;
using System.Text.Json;
using Xunit;

namespace Franthropy.PeerLink.Tests;

public sealed class PeerPipeTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("Franthropy.PeerLink.").FullName;
    private static readonly JsonElement Value = JsonSerializer.SerializeToElement("value");
    private Task<PeerEndpoint> Start(Func<PeerMessage,PeerResult>? handler = null,
        Action<string>? warning = null, string? otherRoot = null) =>
        PeerEndpoint.StartAsync(new("fixture",(action,ct) => Task.Run(action,ct),
            handler ?? (_ => new PeerResult.Delivered(Value)), warning ?? (_ => {})),Value,otherRoot ?? root);
    public void Dispose() => Directory.Delete(root,true);

    [Fact]
    public async Task IndependentKeyRejectedBeforeDispatch()
    {
        int calls = 0;
        await using var target = await Start(_ => { Interlocked.Increment(ref calls); return new PeerResult.Delivered(Value); });
        await using var observer = await Start();
        await using var foreign = await Start(otherRoot:Path.Combine(root,"other"));
        var peer = (await observer.DiscoverAsync()).Single(p => p.InstanceId == target.InstanceId);
        var result = await foreign.Connect(peer).RequestAsync(Guid.NewGuid(),Value,"RequestDeadline",TimeSpan.FromSeconds(5));
        Assert.Equal("AuthenticationFailed",Assert.IsType<PeerResult.Refused>(result).Code);
        Assert.Equal(0,calls);
    }

    [Fact]
    public async Task ConcurrentAndCompletedRetriesShareOneResult()
    {
        int calls = 0;
        await using var target = await Start(_ => new PeerResult.Delivered(JsonSerializer.SerializeToElement(Interlocked.Increment(ref calls))));
        await using var sender = await Start();
        var link = sender.Connect(Assert.Single(await sender.DiscoverAsync()));
        var id = Guid.NewGuid();
        Task<PeerResult> Attempt() => link.RequestAsync(id,Value,"RequestDeadline",TimeSpan.FromSeconds(5));
        var results = await Task.WhenAll(Attempt(),Attempt());
        foreach (var result in results.Append(await Attempt()))
            Assert.Equal(1,Assert.IsType<PeerResult.Delivered>(result).Response.GetInt32());
        Assert.Equal(1,calls);
    }

    [Fact]
    public async Task LateReplyCannotReplaceNamedTimeout()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var target = await Start(_ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return new PeerResult.Delivered(Value);
        });
        await using var sender = await Start(warning:s => { if (s.Contains("LateResponse ignored")) late.TrySetResult(s); });
        var link = sender.Connect(Assert.Single(await sender.DiscoverAsync()));
        var id = Guid.NewGuid();
        var limit = TimeSpan.FromSeconds(1);
        var request = link.RequestAsync(id,Value,"ReceiptDeadline",limit);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new PeerResult.TimedOut("ReceiptDeadline",limit), await request);
        }
        finally { release.Set(); }
        var notice = await late.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(id.ToString(),notice);
        Assert.Contains("ReceiptDeadline",notice);
        Assert.IsType<PeerResult.TimedOut>(await request);
    }
}
