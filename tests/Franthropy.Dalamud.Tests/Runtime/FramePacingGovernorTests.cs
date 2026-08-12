using Franthropy.Dalamud.Runtime;

namespace Franthropy.Dalamud.Tests.Runtime;

public sealed class FramePacingGovernorTests
{
    [Fact]
    public void Strictest_active_lease_controls_pacing_and_release_recomputes_limit()
    {
        var clock = new TestClock();
        using var governor = clock.CreateGovernor();
        using var baseLease = governor.Acquire("background", 60);
        using var travelLease = governor.Acquire("travel", 30);

        clock.Advance(TimeSpan.FromMilliseconds(10));
        governor.PaceFrame();

        var throttled = governor.Snapshot();
        Assert.True(throttled.IsActive);
        Assert.Equal(30, throttled.EffectiveMaximumFramesPerSecond);
        Assert.Equal(2, throttled.ActiveLeaseCount);
        Assert.Single(clock.Delays);
        Assert.InRange(clock.Delays[0].TotalMilliseconds, 23.3, 23.4);

        travelLease.Dispose();
        clock.Advance(TimeSpan.FromMilliseconds(5));
        governor.PaceFrame();

        var relaxed = governor.Snapshot();
        Assert.Equal(60, relaxed.EffectiveMaximumFramesPerSecond);
        Assert.Equal(1, relaxed.ActiveLeaseCount);
        Assert.Equal(2, clock.Delays.Count);
        Assert.InRange(clock.Delays[1].TotalMilliseconds, 11.6, 11.7);
    }

    [Fact]
    public void First_lease_paces_the_acquisition_frame_and_last_release_stops_immediately()
    {
        var clock = new TestClock();
        using var governor = clock.CreateGovernor();
        var lease = governor.Acquire("market-travel", 30);

        clock.Advance(TimeSpan.FromMilliseconds(2));
        governor.PaceFrame();
        lease.Dispose();
        governor.PaceFrame();

        Assert.Single(clock.Delays);
        Assert.InRange(clock.Delays[0].TotalMilliseconds, 31.3, 31.4);
        Assert.False(governor.Snapshot().IsActive);
        Assert.False(lease.IsActive);
    }

    [Fact]
    public void Lease_release_is_idempotent_and_cannot_release_another_owner()
    {
        var clock = new TestClock();
        using var governor = clock.CreateGovernor();
        var first = governor.Acquire("first", 30);
        using var second = governor.Acquire("second", 45);

        first.Dispose();
        first.Dispose();

        var snapshot = governor.Snapshot();
        Assert.True(snapshot.IsActive);
        Assert.Equal(45, snapshot.EffectiveMaximumFramesPerSecond);
        Assert.Equal(["second"], snapshot.ActiveOwners);
    }

    [Fact]
    public void Governor_disposal_releases_every_lease_and_refuses_new_authority()
    {
        var clock = new TestClock();
        var governor = clock.CreateGovernor();
        var lease = governor.Acquire("travel", 30);

        governor.Dispose();

        Assert.False(lease.IsActive);
        Assert.False(governor.Snapshot().IsActive);
        Assert.Throws<ObjectDisposedException>(() => governor.Acquire("new-travel", 30));
        lease.Dispose();
    }

    [Fact]
    public void Default_delay_rounds_fractional_milliseconds_up_to_preserve_the_maximum()
    {
        var normalized = FramePacingGovernor.NormalizeDefaultDelay(TimeSpan.FromMilliseconds(4.167));

        Assert.Equal(TimeSpan.FromMilliseconds(5), normalized);
    }

    [Fact]
    public void Stricter_lease_acquired_during_delay_extends_the_same_frame_budget()
    {
        const long frequency = 1_000_000;
        long timestamp = 0;
        var delays = new List<TimeSpan>();
        FramePacingGovernor? governor = null;
        IFramePacingLease? strictLease = null;
        governor = new FramePacingGovernor(
            () => timestamp,
            frequency,
            duration =>
            {
                delays.Add(duration);
                timestamp += (long)Math.Round(duration.TotalSeconds * frequency, MidpointRounding.AwayFromZero);
                if (delays.Count == 1)
                    strictLease = governor!.Acquire("strict", 10);
            });
        using (governor)
        using (governor.Acquire("base", 60))
        {
            timestamp += 1_000;

            governor.PaceFrame();

            Assert.Equal(2, delays.Count);
            Assert.InRange(delays[0].TotalMilliseconds, 15.6, 15.7);
            Assert.InRange(delays[1].TotalMilliseconds, 83.3, 83.4);
            Assert.Equal(10, governor.Snapshot().EffectiveMaximumFramesPerSecond);
            Assert.InRange(governor.Snapshot().TotalRequestedDelay.TotalMilliseconds, 98.9, 99.1);
            Assert.InRange(governor.Snapshot().LastRequestedDelay.TotalMilliseconds, 98.9, 99.1);
            strictLease!.Dispose();
        }
    }

    [Fact]
    public async Task Final_lease_release_interrupts_the_default_wait()
    {
        using var governor = new FramePacingGovernor();
        var lease = governor.Acquire("slow", 1);
        var pacing = Task.Run(governor.PaceFrame);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(pacing.IsCompleted);

        lease.Dispose();

        await pacing.WaitAsync(TimeSpan.FromMilliseconds(250));
        var snapshot = governor.Snapshot();
        Assert.False(snapshot.IsActive);
        Assert.Equal(1, snapshot.TotalDelayedFrames);
        Assert.True(snapshot.TotalRequestedDelay >= TimeSpan.FromMilliseconds(900));
        Assert.Equal(snapshot.TotalRequestedDelay, snapshot.LastRequestedDelay);
    }

    private sealed class TestClock
    {
        private const long Frequency = 1_000_000;
        private long timestamp;

        public List<TimeSpan> Delays { get; } = [];

        public FramePacingGovernor CreateGovernor() => new(
            () => timestamp,
            Frequency,
            duration =>
            {
                Delays.Add(duration);
                Advance(duration);
            });

        public void Advance(TimeSpan duration) =>
            timestamp += (long)Math.Round(duration.TotalSeconds * Frequency, MidpointRounding.AwayFromZero);
    }
}
