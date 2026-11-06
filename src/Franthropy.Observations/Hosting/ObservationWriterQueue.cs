using System.Diagnostics;
using System.Threading.Channels;
using Franthropy.Observations.Storage;
using Franthropy.Observations.V1;

namespace Franthropy.Observations.Hosting;

public sealed record ObservationWriterQueueOptions
{
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>A single-reader writer with independent, non-framework liveness supervision.</summary>
public sealed class ObservationWriterQueue : IDisposable
{
    private sealed record Work(string Scope, string Kind, Func<CancellationToken, ValueTask<ObservationWriteResult>> Write);
    private sealed record Flight(Work Work, long Started, ObservationWriteProgress Progress);
    private readonly object gate = new();
    private readonly Channel<Work> queue = Channel.CreateUnbounded<Work>(new() { SingleReader = true });
    private readonly CancellationTokenSource lifetime = new();
    private readonly ObservationWriterQueueOptions options;
    private readonly Action<string> fault;
    private readonly Action<string, Exception?>? diagnostic;
    private readonly Timer timer;
    private readonly Task worker;
    private Flight? flight;
    private long lastDrained = Stopwatch.GetTimestamp();
    private long backlogSince;
    private int depth;
    private int failed;
    private int disposed;

    public ObservationWriterQueue(ObservationWriterQueueOptions options, Action<string> fault,
        Action<string, Exception?>? diagnostic)
    {
        foreach (var duration in new[] { options.StallTimeout, options.WriteTimeout, options.CheckInterval, options.ShutdownTimeout })
            if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(options));
        this.options = options;
        this.fault = fault;
        this.diagnostic = diagnostic;
        worker = Task.Run(ProcessAsync);
        timer = new Timer(Check, null, options.CheckInterval, options.CheckInterval);
    }

    public bool TryWrite(string scope, string kind, Func<CancellationToken, ValueTask<ObservationWriteResult>> write)
    {
        lock (gate)
        {
            if (disposed != 0 || failed != 0) return false;
            if (depth++ == 0) backlogSince = Stopwatch.GetTimestamp();
            if (queue.Writer.TryWrite(new Work(scope, kind, write))) return true;
            depth--;
            return false;
        }
    }

    private void Check(object? _)
    {
        string? message = null;
        lock (gate)
        {
            if (disposed != 0 || failed != 0) return;
            var age = flight is null ? TimeSpan.Zero : Stopwatch.GetElapsedTime(flight.Started);
            if (flight is not null && age >= options.WriteTimeout)
                message = Describe("OBS_WRITE_TIMEOUT", age);
            else if (age >= options.StallTimeout ||
                (depth > 0 && Stopwatch.GetElapsedTime(Math.Max(lastDrained, backlogSince)) >= options.StallTimeout))
                message = Describe("OBS_WRITER_STALLED", age);
        }
        if (message is not null) Fail(message);
    }

    private string Describe(string code, TimeSpan age)
    {
        queue.Reader.TryPeek(out var next);
        return FormattableString.Invariant($"[{code}] Shared observation writer stalled: stage={flight?.Progress.Stage ?? "queued"}; scope={flight?.Work.Scope ?? next?.Scope ?? "none"}; payload={flight?.Work.Kind ?? next?.Kind ?? "none"}; inFlightAge={age.TotalSeconds:F3}s; queueDepth={depth}.");
    }

    private void Fail(string message)
    {
        if (Interlocked.CompareExchange(ref failed, 1, 0) != 0) return;
        queue.Writer.TryComplete();
        // Cancellation callbacks and consumer fault reporting must never pin the timer or Dispose.
        _ = lifetime.CancelAsync();
        _ = Task.Run(() => { try { diagnostic?.Invoke(message, null); } catch { } });
        _ = Task.Run(() => { try { fault(message); } catch { } });
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var work in queue.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                if (lifetime.IsCancellationRequested) return;
                using var progress = new ObservationWriteProgress();
                lock (gate)
                {
                    depth--;
                    flight = new Flight(work, Stopwatch.GetTimestamp(), progress);
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(options.WriteTimeout);
                progress.SetStage("store call");
                // The independent timer also covers synchronous code before a ValueTask is returned.
                var result = await work.Write(timeout.Token).ConfigureAwait(false);
                progress.SetStage("returned");
                if (timeout.IsCancellationRequested)
                {
                    if (!lifetime.IsCancellationRequested) Fail(CurrentFailure("OBS_WRITE_TIMEOUT"));
                    return;
                }
                if (result.Status is ObservationWriteStatus.Busy or ObservationWriteStatus.Unavailable or ObservationWriteStatus.UnsupportedDatabaseVersion)
                {
                    Fail($"Shared observation writer failed: {result.Message}");
                    return;
                }
                if (result.Status == ObservationWriteStatus.Rejected)
                    diagnostic?.Invoke($"Shared observation evidence was rejected: {result.Message}", null);
                lock (gate) { lastDrained = Stopwatch.GetTimestamp(); flight = null; }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) { Fail(CurrentFailure("OBS_WRITE_TIMEOUT")); }
        catch (Exception ex) { Fail($"The shared observation writer stopped unexpectedly: {ex.Message}"); }
    }

    private string CurrentFailure(string code)
    {
        lock (gate) return Describe(code, flight is null ? TimeSpan.Zero : Stopwatch.GetElapsedTime(flight.Started));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        timer.Dispose();
        queue.Writer.TryComplete();
        _ = lifetime.CancelAsync();
        if (!worker.Wait(options.ShutdownTimeout))
        {
            var message = CurrentFailure("OBS_WORKER_ABANDONED");
            _ = Task.Run(() => { try { diagnostic?.Invoke(message, null); } catch { } });
        }
        // The worker owns its active transaction until it eventually returns; never dispose it underneath a write.
        _ = worker.ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default);
    }
}
