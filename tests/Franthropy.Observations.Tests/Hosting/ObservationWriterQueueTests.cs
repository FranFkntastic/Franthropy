using System.Collections.Concurrent;
using System.Diagnostics;
using Franthropy.Observations.Hosting;
using Franthropy.Observations.Storage;
using Franthropy.Observations.V1;

namespace Franthropy.Observations.Tests.Hosting;

public sealed class ObservationWriterQueueTests
{
    private static ObservationWriterQueueOptions Options => new()
    {
        StallTimeout = TimeSpan.FromMilliseconds(100),
        WriteTimeout = TimeSpan.FromSeconds(10),
        CheckInterval = TimeSpan.FromMilliseconds(10),
        ShutdownTimeout = TimeSpan.FromMilliseconds(50),
    };

    [Fact]
    public async Task Dispose_drains_accepted_work_from_a_healthy_store_without_cancelling()
    {
        var faults = new ConcurrentQueue<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new ConcurrentQueue<(string Kind, bool Cancelled)>();
        var queue = new ObservationWriterQueue(Options with { ShutdownTimeout = TimeSpan.FromSeconds(5) }, faults.Enqueue, null);
        Assert.True(queue.TryWrite("scope", "snapshot", async token =>
        {
            started.SetResult();
            await Task.Delay(100, CancellationToken.None);
            written.Enqueue(("snapshot", token.IsCancellationRequested));
            return new(ObservationWriteStatus.AcceptedChanged, "Written");
        }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var kind in new[] { "delta", "closing" })
            Assert.True(queue.TryWrite("retainer", kind, token =>
            {
                written.Enqueue((kind, token.IsCancellationRequested));
                return ValueTask.FromResult(new ObservationWriteResult(ObservationWriteStatus.AcceptedChanged, "Written"));
            }));
        queue.Dispose();
        Assert.Equal(new[] { ("snapshot", false), ("delta", false), ("closing", false) }, written.ToArray());
        Assert.Empty(faults);
        Assert.False(queue.TryWrite("scope", "late", _ => throw new Exception("Must not accept after dispose")));
    }

}
