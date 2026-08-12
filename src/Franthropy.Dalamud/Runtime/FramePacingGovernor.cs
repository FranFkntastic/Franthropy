using System.Diagnostics;

namespace Franthropy.Dalamud.Runtime;

public interface IFramePacingLease : IDisposable
{
    string LeaseId { get; }
    string Owner { get; }
    int MaximumFramesPerSecond { get; }
    bool IsActive { get; }
}

public sealed record FramePacingGovernorSnapshot(
    bool IsActive,
    int? EffectiveMaximumFramesPerSecond,
    int ActiveLeaseCount,
    IReadOnlyList<string> ActiveOwners,
    long TotalDelayedFrames,
    TimeSpan TotalRequestedDelay,
    TimeSpan LastRequestedDelay);

/// <summary>
/// Composes bounded frame-pacing requests and applies the strictest active limit.
/// Consumers own policy and lease lifetime; this governor owns pacing mechanics.
/// </summary>
public sealed class FramePacingGovernor : IDisposable
{
    public const int MinimumFramesPerSecond = 1;
    public const int MaximumFramesPerSecond = 240;

    private readonly object sync = new();
    private readonly Func<long> timestampProvider;
    private readonly long timestampFrequency;
    private readonly Action<TimeSpan> delay;
    private readonly Dictionary<long, LeaseState> leases = [];
    private long nextLeaseId;
    private long leaseRevision;
    private long? lastFrameTimestamp;
    private long totalDelayedFrames;
    private TimeSpan totalRequestedDelay;
    private TimeSpan lastRequestedDelay;
    private bool disposed;

    public FramePacingGovernor()
        : this(Stopwatch.GetTimestamp, Stopwatch.Frequency, duration => Thread.Sleep(NormalizeDefaultDelay(duration)))
    {
    }

    internal FramePacingGovernor(
        Func<long> timestampProvider,
        long timestampFrequency,
        Action<TimeSpan> delay)
    {
        this.timestampProvider = timestampProvider ?? throw new ArgumentNullException(nameof(timestampProvider));
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
        if (timestampFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        this.timestampFrequency = timestampFrequency;
    }

    public IFramePacingLease Acquire(string owner, int maximumFramesPerSecond)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("A frame-pacing owner is required.", nameof(owner));
        if (maximumFramesPerSecond is < MinimumFramesPerSecond or > MaximumFramesPerSecond)
            throw new ArgumentOutOfRangeException(nameof(maximumFramesPerSecond));

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var id = ++nextLeaseId;
            leases.Add(id, new LeaseState(owner.Trim(), maximumFramesPerSecond));
            leaseRevision++;
            lastFrameTimestamp ??= timestampProvider();
            return new Lease(this, id, owner.Trim(), maximumFramesPerSecond);
        }
    }

    public FramePacingGovernorSnapshot Snapshot()
    {
        lock (sync)
            return CreateSnapshot();
    }

    public void PaceFrame()
    {
        while (true)
        {
            long revision;
            long currentTimestamp;
            TimeSpan requestedDelay;

            lock (sync)
            {
                if (disposed || leases.Count == 0)
                {
                    lastFrameTimestamp = null;
                    lastRequestedDelay = TimeSpan.Zero;
                    return;
                }

                currentTimestamp = timestampProvider();
                var effectiveFramesPerSecond = leases.Values.Min(value => value.MaximumFramesPerSecond);
                var targetFrameTicks = Math.Max(1L, (long)Math.Ceiling(timestampFrequency / (double)effectiveFramesPerSecond));
                var previousTimestamp = lastFrameTimestamp ?? currentTimestamp;
                var remainingTicks = targetFrameTicks - Math.Max(0L, currentTimestamp - previousTimestamp);
                if (remainingTicks <= 0)
                {
                    lastFrameTimestamp = currentTimestamp;
                    lastRequestedDelay = TimeSpan.Zero;
                    return;
                }

                revision = leaseRevision;
                requestedDelay = TimeSpan.FromSeconds(remainingTicks / (double)timestampFrequency);
            }

            delay(requestedDelay);
            var completedTimestamp = timestampProvider();

            lock (sync)
            {
                if (disposed || leases.Count == 0)
                {
                    lastFrameTimestamp = null;
                    lastRequestedDelay = TimeSpan.Zero;
                    return;
                }

                if (revision != leaseRevision)
                {
                    lastRequestedDelay = TimeSpan.Zero;
                    continue;
                }

                lastFrameTimestamp = completedTimestamp;
                totalDelayedFrames++;
                totalRequestedDelay += requestedDelay;
                lastRequestedDelay = requestedDelay;
                return;
            }
        }
    }

    internal static TimeSpan NormalizeDefaultDelay(TimeSpan requestedDelay)
    {
        if (requestedDelay <= TimeSpan.Zero)
            return TimeSpan.Zero;

        return TimeSpan.FromMilliseconds(Math.Ceiling(requestedDelay.TotalMilliseconds));
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;

            disposed = true;
            leases.Clear();
            leaseRevision++;
            lastFrameTimestamp = null;
            lastRequestedDelay = TimeSpan.Zero;
        }
    }

    private void Release(long leaseId)
    {
        lock (sync)
        {
            if (disposed || !leases.Remove(leaseId))
                return;

            leaseRevision++;
            if (leases.Count == 0)
            {
                lastFrameTimestamp = null;
                lastRequestedDelay = TimeSpan.Zero;
            }
        }
    }

    private bool IsActive(long leaseId)
    {
        lock (sync)
            return !disposed && leases.ContainsKey(leaseId);
    }

    private FramePacingGovernorSnapshot CreateSnapshot() => new(
        IsActive: !disposed && leases.Count > 0,
        EffectiveMaximumFramesPerSecond: disposed || leases.Count == 0
            ? null
            : leases.Values.Min(value => value.MaximumFramesPerSecond),
        ActiveLeaseCount: disposed ? 0 : leases.Count,
        ActiveOwners: disposed
            ? []
            : leases.Values.Select(value => value.Owner).Order(StringComparer.Ordinal).ToArray(),
        TotalDelayedFrames: totalDelayedFrames,
        TotalRequestedDelay: totalRequestedDelay,
        LastRequestedDelay: lastRequestedDelay);

    private sealed record LeaseState(string Owner, int MaximumFramesPerSecond);

    private sealed class Lease(
        FramePacingGovernor owner,
        long id,
        string leaseOwner,
        int maximumFramesPerSecond) : IFramePacingLease
    {
        private int released;

        public string LeaseId { get; } = id.ToString("X16");
        public string Owner { get; } = leaseOwner;
        public int MaximumFramesPerSecond { get; } = maximumFramesPerSecond;
        public bool IsActive => Volatile.Read(ref released) == 0 && owner.IsActive(id);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0)
                return;
            owner.Release(id);
        }
    }
}
