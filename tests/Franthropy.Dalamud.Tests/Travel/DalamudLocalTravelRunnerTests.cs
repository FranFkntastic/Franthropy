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
        Assert.Equal(1, actions.TakeoffRequests);

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
        bool accelerationActive = false,
        bool canMount = false,
        bool canTakeOff = false,
        bool canDismount = false,
        bool canAccelerate = false) => new(
            flightUnlocked,
            mounted,
            inFlight,
            mountTransition,
            casting,
            accelerationActive,
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
