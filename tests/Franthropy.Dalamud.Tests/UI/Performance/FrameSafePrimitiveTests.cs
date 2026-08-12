using Franthropy.Dalamud.UI.Performance;
using Franthropy.Dalamud.UI.Tables;

namespace Franthropy.Dalamud.Tests.UI.Performance;

public sealed class FrameSafePrimitiveTests
{
    [Fact]
    public void VirtualizedRangeSubmitsOnlyTheVisibleRows()
    {
        var rows = Enumerable.Range(0, 500).ToArray();
        var submitted = new List<int>();

        var count = DalamudVirtualizedRows.DrawRange(rows, 120, 135, (row, _) => submitted.Add(row));

        Assert.Equal(15, count);
        Assert.Equal(Enumerable.Range(120, 15), submitted);
    }

    [Fact]
    public void RevisionCacheBuildsOnceUntilTheRevisionChanges()
    {
        var cache = new RevisionCache<string, object>();

        var first = cache.GetOrCreate("r1", _ => new object());
        var repeated = cache.GetOrCreate("r1", _ => new object());
        var changed = cache.GetOrCreate("r2", _ => new object());

        Assert.Same(first, repeated);
        Assert.NotSame(first, changed);
        Assert.Equal(2, cache.BuildCount);
    }

    [Fact]
    public void CadencedProbeRetainsTruthBetweenEligibleRefreshes()
    {
        var time = new ManualTimeProvider();
        var probe = new CadencedProbe<int>(TimeSpan.FromSeconds(1), "test availability", time);
        var calls = 0;

        Assert.Equal(1, probe.Read(() => ++calls, _ => -1).Value);
        Assert.False(probe.Read(() => ++calls, _ => -1).Refreshed);
        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(1, probe.Read(() => ++calls, _ => -1).Value);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, probe.Read(() => ++calls, _ => -1).Value);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void CadencedProbeTurnsFailureIntoAStableRecoveryValue()
    {
        var time = new ManualTimeProvider();
        var probe = new CadencedProbe<bool>(TimeSpan.FromSeconds(1), "test availability", time);
        var calls = 0;

        var failed = probe.Read(
            () => { calls++; throw new InvalidOperationException("offline"); },
            _ => false);
        var repeated = probe.Read(() => { calls++; return true; }, _ => false);

        Assert.False(failed.Value);
        Assert.False(repeated.Value);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DeferredCommitCoalescesContinuousChangesAndRetriesAtCadenceAfterFailure()
    {
        var time = new ManualTimeProvider();
        var pending = new DeferredCommit(TimeSpan.FromMilliseconds(250), "test layout", time);
        var commits = 0;

        pending.MarkChanged();
        time.Advance(TimeSpan.FromMilliseconds(200));
        pending.MarkChanged();
        time.Advance(TimeSpan.FromMilliseconds(249));
        Assert.False(pending.TryCommit(() => commits++));
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(pending.TryCommit(() => commits++));
        Assert.Equal(1, commits);

        pending.MarkChanged();
        time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Throws<InvalidOperationException>(() => pending.TryCommit(() => throw new InvalidOperationException("disk")));
        Assert.False(pending.TryCommit(() => commits++));
        time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.True(pending.TryCommit(() => commits++));
        Assert.Equal(2, commits);
    }

    [Fact]
    public void BoundedTtlCacheExpiresReadsAndNeverExceedsItsCeiling()
    {
        var time = new ManualTimeProvider();
        var cache = new BoundedTtlCache<int, string>(3, TimeSpan.FromMinutes(1), time);
        cache.Set(1, "one");
        cache.Set(2, "two");
        cache.Set(3, "three");
        cache.Set(4, "four");

        Assert.Equal(3, cache.Count);
        Assert.False(cache.TryGetValue(1, out _));
        Assert.True(cache.TryGetValue(4, out var newest));
        Assert.Equal("four", newest);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(cache.TryGetValue(4, out _));
        var stale = cache.Get(4);
        Assert.True(stale.Found);
        Assert.False(stale.IsFresh);
        Assert.Equal("four", stale.Value);
        Assert.Equal(3, cache.Count);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
