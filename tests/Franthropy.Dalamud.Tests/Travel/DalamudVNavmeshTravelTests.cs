using System.Numerics;
using Franthropy.Dalamud.Travel;

namespace Franthropy.Dalamud.Tests.Travel;

public sealed class DalamudVNavmeshTravelTests
{
    [Theory]
    [InlineData(VNavmeshTravelMode.Ground, false)]
    [InlineData(VNavmeshTravelMode.Flight, true)]
    public void Typed_travel_mode_maps_to_exact_vnavmesh_flight_flag(
        VNavmeshTravelMode mode,
        bool expectedFlight)
    {
        bool? submittedFlight = null;
        var travel = new DalamudVNavmeshTravel(
            isAvailable: () => true,
            isReady: () => true,
            isRunning: () => false,
            moveCloseTo: (_, fly, _) =>
            {
                submittedFlight = fly;
                return true;
            },
            stop: () => { },
            setMovementAllowed: _ => { });

        var result = travel.TryMoveCloseTo(new Vector3(1, 2, 3), 3.5f, mode);

        Assert.True(result.Submitted);
        Assert.Equal(expectedFlight, submittedFlight);
    }

    [Fact]
    public void Existing_overload_remains_grounded()
    {
        bool? submittedFlight = null;
        var travel = new DalamudVNavmeshTravel(
            () => true,
            () => true,
            () => false,
            (_, fly, _) => { submittedFlight = fly; return true; },
            () => { },
            _ => { });

        var result = travel.TryMoveCloseTo(Vector3.Zero, 3.5f);

        Assert.True(result.Submitted);
        Assert.False(submittedFlight);
    }
}
