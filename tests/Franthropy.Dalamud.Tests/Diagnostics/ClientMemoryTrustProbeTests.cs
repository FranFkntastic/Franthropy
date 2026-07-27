using Franthropy.Dalamud.Diagnostics;

namespace Franthropy.Dalamud.Tests.Diagnostics;

public sealed class ClientMemoryTrustProbeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private sealed class MemoryCell
    {
        public bool Value;
        public int WriteCount;
        public bool Read() => Value;
        public void Write(bool value)
        {
            Value = value;
            WriteCount++;
        }
    }

    private static (ClientMemoryTrustProbe Probe, MemoryCell Cell) CreateArmedProbe(bool initial = false, bool restore = true)
    {
        var cell = new MemoryCell { Value = initial };
        var probe = new ClientMemoryTrustProbe(cell.Read, cell.Write, restoreOriginalOnHoldEnd: restore);
        probe.Arm();
        return (probe, cell);
    }

    [Fact]
    public void WriteOnce_BlockedUntilArmed()
    {
        var cell = new MemoryCell();
        var probe = new ClientMemoryTrustProbe(cell.Read, cell.Write);

        var result = probe.WriteOnce(true);

        Assert.False(result.Success);
        Assert.False(result.Stuck);
        Assert.Equal(0, cell.WriteCount);
        Assert.False(cell.Value);
    }

    [Fact]
    public void WriteOnce_ReportsStuckWhenValueHolds()
    {
        var (probe, cell) = CreateArmedProbe();

        var result = probe.WriteOnce(true);

        Assert.True(result.Success);
        Assert.True(result.Stuck);
        Assert.False(result.BeforeValue);
        Assert.True(result.AfterValue);
        Assert.True(cell.Value);
    }

    [Fact]
    public void WriteOnce_ReportsOverwriteWhenReadDiffers()
    {
        var cell = new MemoryCell();
        var probe = new ClientMemoryTrustProbe(
            read: () => false,
            write: cell.Write);
        probe.Arm();

        var result = probe.WriteOnce(true);

        Assert.True(result.Success);
        Assert.False(result.Stuck);
        Assert.False(result.AfterValue);
    }

    [Fact]
    public void Test_AllSamplesKept_ClassifiesClientOwned()
    {
        var (probe, _) = CreateArmedProbe();

        probe.BeginTest(true);
        probe.RecordTestSample("next tick", TimeSpan.FromMilliseconds(16));
        probe.RecordTestSample("500ms", TimeSpan.FromMilliseconds(500));
        probe.RecordTestSample("2s", TimeSpan.FromSeconds(2));
        var verdict = probe.CompleteTest();

        Assert.Equal(ClientMemoryTrustClassification.ClientOwned, verdict.Classification);
        Assert.Equal(4, verdict.Samples.Count);
        Assert.All(verdict.Samples, sample => Assert.True(sample.Kept));
    }

    [Fact]
    public void Test_ImmediateMismatch_ClassifiesWriteRejected()
    {
        var cell = new MemoryCell();
        var probe = new ClientMemoryTrustProbe(read: () => false, write: cell.Write);
        probe.Arm();

        probe.BeginTest(true);
        var verdict = probe.CompleteTest();

        Assert.Equal(ClientMemoryTrustClassification.WriteRejected, verdict.Classification);
    }

    [Fact]
    public void Test_TickMismatch_ClassifiesLocallyRewritten()
    {
        var cell = new MemoryCell();
        var readCount = 0;
        var probe = new ClientMemoryTrustProbe(
            read: () =>
            {
                readCount++;
                return readCount <= 2 ? cell.Value : false;
            },
            write: cell.Write);
        probe.Arm();

        probe.BeginTest(true);
        probe.RecordTestSample("next tick", TimeSpan.FromMilliseconds(16));
        var verdict = probe.CompleteTest();

        Assert.Equal(ClientMemoryTrustClassification.LocallyRewritten, verdict.Classification);
    }

    [Fact]
    public void Test_SlowMismatch_ClassifiesExternallyRewritten()
    {
        var cell = new MemoryCell();
        var readCount = 0;
        var probe = new ClientMemoryTrustProbe(
            read: () =>
            {
                readCount++;
                return readCount <= 3 ? cell.Value : false;
            },
            write: cell.Write);
        probe.Arm();

        probe.BeginTest(true);
        probe.RecordTestSample("next tick", TimeSpan.FromMilliseconds(16));
        probe.RecordTestSample("500ms", TimeSpan.FromMilliseconds(500));
        var verdict = probe.CompleteTest();

        Assert.Equal(ClientMemoryTrustClassification.ExternallyRewritten, verdict.Classification);
    }

    [Fact]
    public void Test_RequiresArm()
    {
        var cell = new MemoryCell();
        var probe = new ClientMemoryTrustProbe(cell.Read, cell.Write);

        Assert.Throws<InvalidOperationException>(() => probe.BeginTest(true));
    }

    [Fact]
    public void Hold_ReappliesAndCountsExternalOverwrites()
    {
        var (probe, cell) = CreateArmedProbe();

        probe.StartHold(true, TimeSpan.FromSeconds(10), T0);
        probe.ObserveTick(T0.AddMilliseconds(16));
        cell.Value = false;
        var snapshot = probe.ObserveTick(T0.AddMilliseconds(32));

        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsActive);
        Assert.Equal(1, snapshot.OverwriteCount);
        Assert.Equal(3, snapshot.WriteCount);
        Assert.True(cell.Value);
    }

    [Fact]
    public void Hold_CapsDurationAtMaximum()
    {
        var (probe, _) = CreateArmedProbe();

        var snapshot = probe.StartHold(true, TimeSpan.FromMinutes(5), T0);

        Assert.Equal(TimeSpan.FromSeconds(30), snapshot.Duration);
    }

    [Fact]
    public void Hold_CompletesAtDeadlineAndRestoresOriginal()
    {
        var (probe, cell) = CreateArmedProbe(initial: false);

        probe.StartHold(true, TimeSpan.FromSeconds(1), T0);
        var completed = probe.ObserveTick(T0.AddSeconds(2));

        Assert.NotNull(completed);
        Assert.False(completed.IsActive);
        Assert.Equal("completed", completed.EndReason);
        Assert.False(cell.Value);
    }

    [Fact]
    public void Hold_StopRestoresOriginalUnlessDisabled()
    {
        var (restoringProbe, restoringCell) = CreateArmedProbe(initial: true, restore: true);
        restoringProbe.StartHold(false, TimeSpan.FromSeconds(30), T0);
        restoringProbe.StopHold(T0.AddSeconds(1));
        Assert.True(restoringCell.Value);

        var (keepingProbe, keepingCell) = CreateArmedProbe(initial: true, restore: false);
        keepingProbe.StartHold(false, TimeSpan.FromSeconds(30), T0);
        keepingProbe.StopHold(T0.AddSeconds(1));
        Assert.False(keepingCell.Value);
    }

    [Fact]
    public void Disarm_EndsActiveHold()
    {
        var (probe, cell) = CreateArmedProbe(initial: false);

        probe.StartHold(true, TimeSpan.FromSeconds(30), T0);
        var ended = probe.Disarm(T0.AddSeconds(1));

        Assert.NotNull(ended);
        Assert.Equal("disarmed", ended.EndReason);
        Assert.False(probe.IsArmed);
        Assert.False(cell.Value);
        Assert.Throws<InvalidOperationException>(() => probe.StartHold(true, TimeSpan.FromSeconds(1), T0));
    }

    [Fact]
    public void Hold_RequiresPositiveDuration()
    {
        var (probe, _) = CreateArmedProbe();

        Assert.Throws<ArgumentOutOfRangeException>(() => probe.StartHold(true, TimeSpan.Zero, T0));
    }
}
