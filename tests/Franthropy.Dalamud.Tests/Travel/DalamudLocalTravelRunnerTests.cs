using Franthropy.Dalamud.Travel;

namespace Franthropy.Dalamud.Tests.Travel;

public sealed class DalamudLocalTravelRunnerTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Long_flyable_route_mounts_takes_off_then_selects_flight()
    {
        var actions = new FakeLocalTravelActions(Observation(flightUnlocked: true, canMount: true));
        var runner = new DalamudLocalTravelRunner(actions);

        var mounting = runner.Advance(100f, StartedAt);
        Assert.Equal(LocalTravelPreparationState.Waiting, mounting.State);
        Assert.Equal("FlightMountRequested", mounting.Code);
        Assert.Equal(1, actions.MountRequests);

        Assert.Equal(LocalTravelPreparationState.Waiting, runner.Advance(100f, StartedAt.AddSeconds(1)).State);
        Assert.Equal(1, actions.MountRequests);

        actions.Current = Observation(flightUnlocked: true, mounted: true, canTakeOff: true);
        var takingOff = runner.Advance(100f, StartedAt.AddSeconds(2));
        Assert.Equal("TakeoffRequested", takingOff.Code);
        Assert.Equal(1, actions.TakeoffRequests);

        Assert.Equal(LocalTravelPreparationState.Waiting, runner.Advance(100f, StartedAt.AddSeconds(3)).State);
        Assert.Equal(2, actions.TakeoffRequests);

        actions.Current = Observation(flightUnlocked: true, mounted: true, inFlight: true);
        var ready = runner.Advance(100f, StartedAt.AddSeconds(4));
        Assert.True(ready.IsReady);
        Assert.Equal(LocalTravelMode.Flight, ready.Mode);
        Assert.Equal(VNavmeshTravelMode.Flight, ready.VNavmeshMode);
    }

    [Fact]
    public void Long_nonflyable_route_uses_ground_mount()
    {
        var actions = new FakeLocalTravelActions(Observation(canMount: true));
        var runner = new DalamudLocalTravelRunner(actions);

        Assert.Equal("MountRequested", runner.Advance(100f, StartedAt).Code);
        actions.Current = Observation(mounted: true);

        var ready = runner.Advance(100f, StartedAt.AddSeconds(1));
        Assert.True(ready.IsReady);
        Assert.Equal(LocalTravelMode.GroundMount, ready.Mode);
        Assert.Equal(VNavmeshTravelMode.Ground, ready.VNavmeshMode);
    }

    [Fact]
    public void Accepted_mount_request_retries_until_mounting_is_observed()
    {
        var actions = new FakeLocalTravelActions(Observation(canMount: true));
        var runner = new DalamudLocalTravelRunner(actions);

        Assert.Equal("MountRequested", runner.Advance(100f, StartedAt).Code);
        Assert.Equal("AwaitingMount", runner.Advance(100f, StartedAt.AddSeconds(1)).Code);
        Assert.Equal("MountRetryRequested", runner.Advance(100f, StartedAt.AddSeconds(2)).Code);
        Assert.Equal(2, actions.MountRequests);
        Assert.Equal(0, actions.AccelerationRequests);

        actions.Current = Observation(mounted: true);
        var ready = runner.Advance(100f, StartedAt.AddSeconds(3));

        Assert.True(ready.IsReady);
        Assert.Equal(LocalTravelMode.GroundMount, ready.Mode);
    }

    [Fact]
    public void Mount_transition_settles_before_the_retry_window_starts()
    {
        var actions = new FakeLocalTravelActions(Observation(
            mountTransition: true,
            canMount: true,
            canAccelerate: true));
        var runner = new DalamudLocalTravelRunner(actions);

        Assert.Equal("AwaitingMount", runner.Advance(100f, StartedAt).Code);
        Assert.Equal("AwaitingMount", runner.Advance(100f, StartedAt.AddSeconds(12)).Code);
        Assert.Equal(0, actions.MountRequests);
        Assert.Equal(0, actions.AccelerationRequests);

        actions.Current = Observation(canMount: true, canAccelerate: true);
        Assert.Equal("AwaitingMount", runner.Advance(100f, StartedAt.AddSeconds(13)).Code);
        Assert.Equal("MountRetryRequested", runner.Advance(100f, StartedAt.AddSeconds(14)).Code);
        Assert.Equal(1, actions.MountRequests);
        Assert.Equal(0, actions.AccelerationRequests);
    }

    [Fact]
    public void Animation_lock_waits_for_mount_instead_of_starting_sprint()
    {
        var actions = new FakeLocalTravelActions(Observation(
            animationLocked: true,
            canMount: true,
            canAccelerate: true));
        var runner = new DalamudLocalTravelRunner(actions);

        var pending = runner.Advance(100f, StartedAt);

        Assert.Equal(LocalTravelPreparationState.Waiting, pending.State);
        Assert.Equal("AwaitingMount", pending.Code);
        Assert.Equal(0, actions.MountRequests);
        Assert.Equal(0, actions.AccelerationRequests);
    }

    [Fact]
    public void Flight_capable_route_with_exhausted_mount_attempts_degrades_to_ground_sprint()
    {
        var actions = new FakeLocalTravelActions(Observation(
            flightUnlocked: true,
            canMount: true,
            canAccelerate: true));
        var runner = new DalamudLocalTravelRunner(actions);

        runner.Advance(100f, StartedAt);
        foreach (var seconds in new[] { 2, 4, 6, 8, 10 })
            Assert.Equal(LocalTravelPreparationState.Waiting, runner.Advance(100f, StartedAt.AddSeconds(seconds)).State);

        var fallback = runner.Advance(100f, StartedAt.AddSeconds(12));

        Assert.Equal(LocalTravelPreparationState.Ready, fallback.State);
        Assert.Equal(LocalTravelMode.Sprint, fallback.Mode);
        Assert.Equal("AccelerationRequested", fallback.Code);
        Assert.Equal(6, actions.MountRequests);
        Assert.Equal(1, actions.AccelerationRequests);

        Assert.Equal(LocalTravelMode.Sprint, runner.Advance(100f, StartedAt.AddSeconds(13)).Mode);
        Assert.Equal(6, actions.MountRequests);
        Assert.Equal(1, actions.AccelerationRequests);
    }

    [Fact]
    public void Mount_prohibited_in_the_current_territory_can_use_sprint()
    {
        var actions = new FakeLocalTravelActions(Observation(mountAllowed: false, canAccelerate: true));
        var runner = new DalamudLocalTravelRunner(actions);

        var ready = runner.Advance(100f, StartedAt);

        Assert.True(ready.IsReady);
        Assert.Equal(LocalTravelMode.Sprint, ready.Mode);
        Assert.Equal(0, actions.MountRequests);
        Assert.Equal(1, actions.AccelerationRequests);
    }

    [Fact]
    public void Mount_allowed_but_temporarily_unavailable_enters_the_retry_window()
    {
        var actions = new FakeLocalTravelActions(Observation(mountAllowed: true, canAccelerate: true));
        var runner = new DalamudLocalTravelRunner(actions);

        var pending = runner.Advance(100f, StartedAt);

        Assert.Equal(LocalTravelPreparationState.Waiting, pending.State);
        Assert.Equal("AwaitingMount", pending.Code);
        Assert.Equal(0, actions.MountRequests);
        Assert.Equal(0, actions.AccelerationRequests);

        actions.Current = Observation(mountAllowed: true, canMount: true, canAccelerate: true);
        Assert.Equal("MountRetryRequested", runner.Advance(100f, StartedAt.AddSeconds(2)).Code);
        Assert.Equal(1, actions.MountRequests);
    }

    [Fact]
    public void Accepted_takeoff_request_retries_until_flight_is_observed()
    {
        var actions = new FakeLocalTravelActions(Observation(
            flightUnlocked: true,
            mounted: true,
            canTakeOff: true));
        var runner = new DalamudLocalTravelRunner(actions);

        Assert.Equal("TakeoffRequested", runner.Advance(100f, StartedAt).Code);
        Assert.Equal("TakeoffRetryRequested", runner.Advance(100f, StartedAt.AddMilliseconds(500)).Code);
        Assert.Equal(2, actions.TakeoffRequests);

        actions.Current = Observation(flightUnlocked: true, mounted: true, inFlight: true);
        var ready = runner.Advance(100f, StartedAt.AddSeconds(1));

        Assert.True(ready.IsReady);
        Assert.Equal(LocalTravelMode.Flight, ready.Mode);
    }

    [Fact]
    public void Temporarily_unavailable_takeoff_enters_the_retry_window()
    {
        var actions = new FakeLocalTravelActions(Observation(
            flightUnlocked: true,
            mounted: true));
        var runner = new DalamudLocalTravelRunner(actions);

        var pending = runner.Advance(100f, StartedAt);

        Assert.Equal(LocalTravelPreparationState.Waiting, pending.State);
        Assert.Equal("AwaitingTakeoff", pending.Code);
        Assert.Equal(0, actions.TakeoffRequests);

        actions.Current = Observation(flightUnlocked: true, mounted: true, canTakeOff: true);
        Assert.Equal("TakeoffRetryRequested", runner.Advance(100f, StartedAt.AddMilliseconds(500)).Code);
        Assert.Equal(1, actions.TakeoffRequests);
    }

    [Fact]
    public void Accepted_takeoff_request_never_silently_downgrades_to_ground()
    {
        var actions = new FakeLocalTravelActions(Observation(
            flightUnlocked: true,
            mounted: true,
            canTakeOff: true));
        var runner = new DalamudLocalTravelRunner(actions);

        runner.Advance(100f, StartedAt);
        var unavailable = runner.Advance(100f, StartedAt.AddSeconds(8));

        Assert.Equal(LocalTravelPreparationState.Unavailable, unavailable.State);
        Assert.Equal("TakeoffTimeout", unavailable.Code);
    }

    [Fact]
    public void Short_route_requests_acceleration_once_and_starts_ground_path()
    {
        var actions = new FakeLocalTravelActions(Observation(canAccelerate: true));
        var runner = new DalamudLocalTravelRunner(actions);

        var ready = runner.Advance(20f, StartedAt);
        Assert.True(ready.IsReady);
        Assert.Equal(LocalTravelMode.Sprint, ready.Mode);
        Assert.Equal(1, actions.AccelerationRequests);

        ready = runner.Advance(20f, StartedAt.AddSeconds(1));
        Assert.Equal(LocalTravelMode.Sprint, ready.Mode);
        Assert.Equal(1, actions.AccelerationRequests);
    }

    [Fact]
    public void Unavailable_acceleration_falls_back_to_truthful_walking()
    {
        var actions = new FakeLocalTravelActions(Observation());
        var runner = new DalamudLocalTravelRunner(actions);

        var ready = runner.Advance(100f, StartedAt);

        Assert.True(ready.IsReady);
        Assert.Equal(LocalTravelMode.Walk, ready.Mode);
        Assert.Equal("WalkingFallback", ready.Code);
        Assert.Equal(0, actions.MountRequests);
        Assert.Equal(0, actions.AccelerationRequests);
    }

    [Fact]
    public void Flight_downgrade_lands_once_then_remounts_for_ground_route()
    {
        var actions = new FakeLocalTravelActions(Observation(
            flightUnlocked: true,
            mounted: true,
            inFlight: true,
            canDismount: true));
        var runner = new DalamudLocalTravelRunner(actions);
        runner.DowngradeFlight();

        Assert.Equal("DismountRequested", runner.Advance(100f, StartedAt).Code);
        Assert.Equal(1, actions.DismountRequests);
        Assert.Equal("AwaitingDismount", runner.Advance(100f, StartedAt.AddSeconds(1)).Code);
        Assert.Equal(1, actions.DismountRequests);

        actions.Current = Observation(flightUnlocked: true, canMount: true);
        Assert.Equal("MountRequested", runner.Advance(100f, StartedAt.AddSeconds(2)).Code);
        actions.Current = Observation(flightUnlocked: true, mounted: true);

        var ready = runner.Advance(100f, StartedAt.AddSeconds(3));
        Assert.True(ready.IsReady);
        Assert.Equal(LocalTravelMode.GroundMount, ready.Mode);
        Assert.Equal(0, actions.TakeoffRequests);
    }

    [Fact]
    public void Flight_downgrade_fails_explicitly_when_landing_never_completes()
    {
        var actions = new FakeLocalTravelActions(Observation(mounted: true, inFlight: true, canDismount: true));
        var runner = new DalamudLocalTravelRunner(actions);
        runner.DowngradeFlight();
        runner.Advance(100f, StartedAt);

        var unavailable = runner.Advance(100f, StartedAt.AddSeconds(5));

        Assert.Equal(LocalTravelPreparationState.Unavailable, unavailable.State);
        Assert.Equal("DismountTimeout", unavailable.Code);
        Assert.Equal(1, actions.DismountRequests);
    }

    private static LocalTravelObservation Observation(
        bool flightUnlocked = false,
        bool mounted = false,
        bool inFlight = false,
        bool mountTransition = false,
        bool casting = false,
        bool animationLocked = false,
        bool accelerationActive = false,
        bool? mountAllowed = null,
        bool canMount = false,
        bool canTakeOff = false,
        bool canDismount = false,
        bool canAccelerate = false) => new(
            flightUnlocked,
            mounted,
            inFlight,
            mountTransition,
            casting,
            animationLocked,
            accelerationActive,
            mountAllowed ?? canMount,
            canMount,
            canTakeOff,
            canDismount,
            canAccelerate);

    private sealed class FakeLocalTravelActions(LocalTravelObservation current) : ILocalTravelActions
    {
        public LocalTravelObservation Current { get; set; } = current;
        public int MountRequests { get; private set; }
        public int TakeoffRequests { get; private set; }
        public int DismountRequests { get; private set; }
        public int AccelerationRequests { get; private set; }

        public LocalTravelObservation Observe() => Current;
        public bool TryMount() { MountRequests++; return true; }
        public bool TryTakeOff() { TakeoffRequests++; return true; }
        public bool TryDismount() { DismountRequests++; return true; }
        public bool TryAccelerate() { AccelerationRequests++; return true; }
    }
}
