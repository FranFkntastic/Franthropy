namespace Franthropy.Dalamud.Diagnostics;

public enum ClientMemoryTrustClassification
{
    Undetermined,
    ClientOwned,
    LocallyRewritten,
    ExternallyRewritten,
    WriteRejected,
}

public sealed record TrustProbeSample(string Label, TimeSpan Offset, bool ObservedValue, bool Kept);

public sealed record TrustProbeVerdict(
    ClientMemoryTrustClassification Classification,
    string Reason,
    bool BeforeValue,
    bool DesiredValue,
    IReadOnlyList<TrustProbeSample> Samples);

public sealed record TrustWriteResult(
    bool Success,
    bool BeforeValue,
    bool? AfterValue,
    bool Stuck,
    string Message);

public sealed record TrustHoldSnapshot(
    bool IsActive,
    bool DesiredValue,
    bool? OriginalValue,
    TimeSpan Elapsed,
    TimeSpan Duration,
    int WriteCount,
    int OverwriteCount,
    string? EndReason);

/// <summary>
/// Dependency-free state machine for classifying a single client memory cell as client-owned,
/// locally rewritten, externally (server) rewritten, or write-rejected. Callers supply the raw
/// read and write primitives and drive sampling and hold ticks; this class owns arming, sample
/// bookkeeping, overwrite counting, hold deadlines, and verdict computation. Writes are gated
/// behind a session-only arm that is never persisted. Holds restore the original value when they
/// end unless constructed with <c>restoreOriginalOnHoldEnd: false</c>.
/// </summary>
public sealed class ClientMemoryTrustProbe
{
    private static readonly TimeSpan DefaultMaxHoldDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultLocalRewriteThreshold = TimeSpan.FromMilliseconds(100);

    private readonly Func<bool> read;
    private readonly Action<bool> write;
    private readonly TimeSpan maxHoldDuration;
    private readonly TimeSpan localRewriteThreshold;
    private readonly bool restoreOriginalOnHoldEnd;
    private readonly object gate = new();

    private bool armed;
    private TrustTestState? activeTest;
    private TrustHoldState? activeHold;

    public ClientMemoryTrustProbe(
        Func<bool> read,
        Action<bool> write,
        TimeSpan? maxHoldDuration = null,
        TimeSpan? localRewriteThreshold = null,
        bool restoreOriginalOnHoldEnd = true)
    {
        this.read = read ?? throw new ArgumentNullException(nameof(read));
        this.write = write ?? throw new ArgumentNullException(nameof(write));
        this.maxHoldDuration = maxHoldDuration ?? DefaultMaxHoldDuration;
        this.localRewriteThreshold = localRewriteThreshold ?? DefaultLocalRewriteThreshold;
        this.restoreOriginalOnHoldEnd = restoreOriginalOnHoldEnd;
        if (this.maxHoldDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxHoldDuration));
        if (this.localRewriteThreshold < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(localRewriteThreshold));
    }

    public bool IsArmed
    {
        get
        {
            lock (gate)
                return armed;
        }
    }

    public void Arm()
    {
        lock (gate)
            armed = true;
    }

    public TrustHoldSnapshot? Disarm(DateTimeOffset now)
    {
        lock (gate)
        {
            armed = false;
            if (activeHold == null)
                return null;
            return CompleteHoldLocked(activeHold, now, "disarmed");
        }
    }

    public TrustWriteResult WriteOnce(bool desired)
    {
        lock (gate)
        {
            if (!armed)
                return new TrustWriteResult(false, false, null, false, "Writes are not armed for this session.");

            var before = read();
            write(desired);
            var after = read();
            var stuck = after == desired;
            var message = stuck
                ? $"Write held: before={before}, requested={desired}, after={after}."
                : $"Write did not stick: before={before}, requested={desired}, after={after}.";
            return new TrustWriteResult(true, before, after, stuck, message);
        }
    }

    public TrustProbeSample BeginTest(bool desired)
    {
        lock (gate)
        {
            ThrowIfDisarmedLocked();
            if (activeTest != null)
                throw new InvalidOperationException("A trust probe test is already active.");

            var before = read();
            write(desired);
            var immediate = read();
            activeTest = new TrustTestState(before, desired);
            return AddSampleLocked(activeTest, "immediate", TimeSpan.Zero, immediate);
        }
    }

    public TrustProbeSample RecordTestSample(string label, TimeSpan offset)
    {
        lock (gate)
        {
            if (activeTest == null)
                throw new InvalidOperationException("No trust probe test is active.");
            return AddSampleLocked(activeTest, label, offset, read());
        }
    }

    public TrustProbeVerdict CompleteTest()
    {
        lock (gate)
        {
            if (activeTest == null)
                throw new InvalidOperationException("No trust probe test is active.");
            var verdict = Classify(activeTest, localRewriteThreshold);
            activeTest = null;
            return verdict;
        }
    }

    public void CancelTest()
    {
        lock (gate)
            activeTest = null;
    }

    public TrustHoldSnapshot StartHold(bool desired, TimeSpan requestedDuration, DateTimeOffset now)
    {
        lock (gate)
        {
            ThrowIfDisarmedLocked();
            if (requestedDuration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(requestedDuration));

            if (activeHold != null)
                CompleteHoldLocked(activeHold, now, "replaced");

            var duration = requestedDuration > maxHoldDuration ? maxHoldDuration : requestedDuration;
            var original = read();
            var hold = new TrustHoldState(desired, original, duration, now);
            activeHold = hold;
            write(desired);
            hold.WriteCount++;
            return SnapshotLocked(hold, now, isActive: true, endReason: null);
        }
    }

    public TrustHoldSnapshot? ObserveTick(DateTimeOffset now)
    {
        lock (gate)
        {
            var hold = activeHold;
            if (hold == null)
                return null;

            if (now - hold.StartedAt >= hold.Duration)
                return CompleteHoldLocked(hold, now, "completed");

            if (!armed)
                return CompleteHoldLocked(hold, now, "disarmed");

            var current = read();
            if (current != hold.DesiredValue)
                hold.OverwriteCount++;

            write(hold.DesiredValue);
            hold.WriteCount++;
            return SnapshotLocked(hold, now, isActive: true, endReason: null);
        }
    }

    public TrustHoldSnapshot? StopHold(DateTimeOffset now, string reason = "stopped")
    {
        lock (gate)
        {
            var hold = activeHold;
            if (hold == null)
                return null;
            return CompleteHoldLocked(hold, now, reason);
        }
    }

    public TrustHoldSnapshot GetHoldSnapshot(DateTimeOffset now)
    {
        lock (gate)
        {
            return activeHold == null
                ? new TrustHoldSnapshot(false, false, null, TimeSpan.Zero, TimeSpan.Zero, 0, 0, null)
                : SnapshotLocked(activeHold, now, isActive: true, endReason: null);
        }
    }

    public static TrustProbeVerdict Classify(TrustTestState test, TimeSpan localRewriteThreshold)
    {
        var samples = test.Samples;
        if (samples.Count == 0)
        {
            return new TrustProbeVerdict(
                ClientMemoryTrustClassification.Undetermined,
                "No samples were recorded.",
                test.BeforeValue,
                test.DesiredValue,
                samples);
        }

        var firstFailure = samples.FirstOrDefault(sample => !sample.Kept);
        if (firstFailure == null)
        {
            var span = samples[^1].Offset;
            return new TrustProbeVerdict(
                ClientMemoryTrustClassification.ClientOwned,
                $"Value held across {samples.Count} sample(s) spanning {span.TotalSeconds:F1}s. Client-owned only means the write persists locally; server authority is unaffected.",
                test.BeforeValue,
                test.DesiredValue,
                samples);
        }

        if (firstFailure.Offset <= TimeSpan.Zero)
        {
            return new TrustProbeVerdict(
                ClientMemoryTrustClassification.WriteRejected,
                $"Read-after-write at '{firstFailure.Label}' never observed the requested value.",
                test.BeforeValue,
                test.DesiredValue,
                samples);
        }

        if (firstFailure.Offset <= localRewriteThreshold)
        {
            return new TrustProbeVerdict(
                ClientMemoryTrustClassification.LocallyRewritten,
                $"Value was rewritten by '{firstFailure.Label}' ({firstFailure.Offset.TotalMilliseconds:F0}ms), inside frame-cadence. The client recomputes this cell locally.",
                test.BeforeValue,
                test.DesiredValue,
                samples);
        }

        return new TrustProbeVerdict(
            ClientMemoryTrustClassification.ExternallyRewritten,
            $"Value survived local sampling but was rewritten by '{firstFailure.Label}' ({firstFailure.Offset.TotalSeconds:F1}s), consistent with a server-fed update.",
            test.BeforeValue,
            test.DesiredValue,
            samples);
    }

    private TrustProbeSample AddSampleLocked(TrustTestState test, string label, TimeSpan offset, bool observed)
    {
        var sample = new TrustProbeSample(label, offset, observed, observed == test.DesiredValue);
        test.Samples.Add(sample);
        return sample;
    }

    private TrustHoldSnapshot CompleteHoldLocked(TrustHoldState hold, DateTimeOffset now, string reason)
    {
        activeHold = null;
        if (restoreOriginalOnHoldEnd)
            write(hold.OriginalValue);
        return SnapshotLocked(hold, now, isActive: false, endReason: reason);
    }

    private TrustHoldSnapshot SnapshotLocked(TrustHoldState hold, DateTimeOffset now, bool isActive, string? endReason)
    {
        return new TrustHoldSnapshot(
            isActive,
            hold.DesiredValue,
            hold.OriginalValue,
            now - hold.StartedAt,
            hold.Duration,
            hold.WriteCount,
            hold.OverwriteCount,
            endReason);
    }

    private void ThrowIfDisarmedLocked()
    {
        if (!armed)
            throw new InvalidOperationException("Client memory writes are not armed for this session.");
    }

    public sealed class TrustTestState
    {
        public TrustTestState(bool beforeValue, bool desiredValue)
        {
            BeforeValue = beforeValue;
            DesiredValue = desiredValue;
        }

        public bool BeforeValue { get; }
        public bool DesiredValue { get; }
        public List<TrustProbeSample> Samples { get; } = [];
    }

    private sealed class TrustHoldState
    {
        public TrustHoldState(bool desiredValue, bool originalValue, TimeSpan duration, DateTimeOffset startedAt)
        {
            DesiredValue = desiredValue;
            OriginalValue = originalValue;
            Duration = duration;
            StartedAt = startedAt;
        }

        public bool DesiredValue { get; }
        public bool OriginalValue { get; }
        public TimeSpan Duration { get; }
        public DateTimeOffset StartedAt { get; }
        public int WriteCount { get; set; }
        public int OverwriteCount { get; set; }
    }
}
