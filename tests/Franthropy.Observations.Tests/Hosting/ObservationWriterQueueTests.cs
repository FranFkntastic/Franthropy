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
    public async Task Uncooperative_write_faults_once_and_dispose_returns()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ObservationWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var faulted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new ConcurrentQueue<string>();
        var faults = 0;
        var queue = new ObservationWriterQueue(Options, message =>
        {
            Interlocked.Increment(ref faults);
            faulted.TrySetResult(message);
        }, (message, _) => messages.Enqueue(message));
        try
        {
            Assert.True(queue.TryWrite("scope", "delta", _ =>
            {
                ObservationWriteProgress.Mark("publish/signal");
                entered.SetResult();
                return new ValueTask<ObservationWriteResult>(release.Task);
            }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(queue.TryWrite("queued", "snapshot", _ => throw new Exception("Must not drain after fault")));
            var warning = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("OBS_WRITER_STALLED", warning);
            Assert.Contains("stage=publish/signal; scope=scope; payload=delta", warning);
            Assert.Contains("queueDepth=1", warning);
            await Task.Delay(150);
            Assert.Equal(1, faults);
            Assert.Single(messages, message => message.Contains("OBS_WRITER_STALLED"));
            var elapsed = Stopwatch.StartNew();
            queue.Dispose();
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
            Assert.False(queue.TryWrite("scope", "delta", _ => new ValueTask<ObservationWriteResult>(release.Task)));
        }
        finally
        {
            release.TrySetResult(new(ObservationWriteStatus.AcceptedChanged, "Released"));
            queue.Dispose();
        }
    }

    [Fact]
    public async Task Healthy_queue_and_long_idle_have_no_false_stall()
    {
        var faults = new ConcurrentQueue<string>();
        using var queue = new ObservationWriterQueue(Options with { StallTimeout = TimeSpan.FromSeconds(1) }, faults.Enqueue, null);
        await Task.Delay(1100);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.TryWrite("scope", "snapshot", _ =>
        {
            drained.SetResult();
            return ValueTask.FromResult(new ObservationWriteResult(ObservationWriteStatus.AcceptedChanged, "Written"));
        });
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(1100);
        Assert.Empty(faults);
    }

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

    [Fact]
    public async Task Failure_during_shutdown_drain_is_diagnosed_but_not_reported_as_fault()
    {
        var faults = new ConcurrentQueue<string>();
        var diagnostics = new ConcurrentQueue<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ObservationWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new ObservationWriterQueue(Options with { StallTimeout = TimeSpan.FromSeconds(5), ShutdownTimeout = TimeSpan.FromSeconds(5) },
            faults.Enqueue, (message, _) => diagnostics.Enqueue(message));
        Assert.True(queue.TryWrite("scope", "snapshot", _ =>
        {
            entered.SetResult();
            return new ValueTask<ObservationWriteResult>(release.Task);
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(queue.TryWrite("retainer", "delta", _ =>
            ValueTask.FromResult(new ObservationWriteResult(ObservationWriteStatus.Busy, "Locked"))));
        var disposing = Task.Run(queue.Dispose);
        await Task.Delay(50);
        release.SetResult(new(ObservationWriteStatus.AcceptedChanged, "Written"));
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        var deadline = Stopwatch.StartNew();
        while (diagnostics.IsEmpty && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.Contains(diagnostics, message => message.Contains("Locked"));
        Assert.Empty(faults);
    }

    [Fact]
    public async Task Cooperative_write_timeout_has_code_and_stage()
    {
        var faulted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queue = new ObservationWriterQueue(Options with
        {
            WriteTimeout = TimeSpan.FromMilliseconds(50), StallTimeout = TimeSpan.FromSeconds(5),
        }, message => faulted.TrySetResult(message), null);
        queue.TryWrite("scope", "snapshot", async token =>
        {
            ObservationWriteProgress.Mark("begin");
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(ObservationWriteStatus.AcceptedChanged, "Unreachable");
        });
        var warning = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("OBS_WRITE_TIMEOUT", warning);
        Assert.Contains("stage=begin", warning);
    }
}
