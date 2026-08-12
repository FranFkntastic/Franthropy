using Franthropy.Dalamud.Automation.Vendors.Coordination;
using Franthropy.Dalamud.Travel;

namespace Franthropy.Dalamud.Tests.Automation.Vendors;

public sealed class GilVendorFlightNavigationTests
{
    private static readonly DateTimeOffset SubmittedAt = new(2026, 8, 11, 21, 29, 37, TimeSpan.FromHours(-4));

    [Fact]
    public void Accepted_flight_route_waits_through_vnavmesh_path_generation()
    {
        Assert.Equal(
            GilVendorFlightNavigationState.AwaitingStart,
            Decide(
                navigationRunning: false,
                navigationObservedRunning: false,
                observedAt: SubmittedAt.AddMilliseconds(329)));
    }

    [Fact]
    public void Flight_route_continues_after_vnavmesh_reports_running()
    {
        Assert.Equal(
            GilVendorFlightNavigationState.Continue,
            Decide(
                navigationRunning: true,
                navigationObservedRunning: false,
                observedAt: SubmittedAt.AddSeconds(1)));
    }

    [Fact]
    public void Flight_route_that_stops_after_running_downgrades_immediately()
    {
        Assert.Equal(
            GilVendorFlightNavigationState.Downgrade,
            Decide(
                navigationRunning: false,
                navigationObservedRunning: true,
                observedAt: SubmittedAt.AddSeconds(2)));
    }

    [Fact]
    public void Flight_route_that_never_starts_downgrades_after_bounded_timeout()
    {
        Assert.Equal(
            GilVendorFlightNavigationState.AwaitingStart,
            Decide(
                navigationRunning: false,
                navigationObservedRunning: false,
                observedAt: SubmittedAt.AddSeconds(9.999)));
        Assert.Equal(
            GilVendorFlightNavigationState.Downgrade,
            Decide(
                navigationRunning: false,
                navigationObservedRunning: false,
                observedAt: SubmittedAt.AddSeconds(10)));
    }

    [Fact]
    public void Ground_route_does_not_acquire_flight_startup_policy()
    {
        Assert.Equal(
            GilVendorFlightNavigationState.Continue,
            DalamudGilVendorBuyRuntime.DetermineFlightNavigationState(
                ownsNavigation: true,
                activeTravelMode: LocalTravelMode.GroundMount,
                navigationRunning: false,
                navigationObservedRunning: false,
                navigationSubmittedAt: SubmittedAt,
                observedAt: SubmittedAt.AddMinutes(1)));
    }

    private static GilVendorFlightNavigationState Decide(
        bool navigationRunning,
        bool navigationObservedRunning,
        DateTimeOffset observedAt) =>
        DalamudGilVendorBuyRuntime.DetermineFlightNavigationState(
            ownsNavigation: true,
            activeTravelMode: LocalTravelMode.Flight,
            navigationRunning,
            navigationObservedRunning,
            navigationSubmittedAt: SubmittedAt,
            observedAt);
}
