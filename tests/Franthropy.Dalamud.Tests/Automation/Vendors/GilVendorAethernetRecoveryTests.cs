using Franthropy.Dalamud.Automation.Vendors.Coordination;
using Franthropy.Dalamud.Travel;

namespace Franthropy.Dalamud.Tests.Automation.Vendors;

public sealed class GilVendorAethernetRecoveryTests
{
    [Fact]
    public void Pending_aethernet_travel_protects_lifestream_selection_ui_only()
    {
        var protectedAddons = DalamudGilVendorBuyRuntime.ProtectedOwnedAddonsForPendingAethernet(35);

        Assert.NotNull(protectedAddons);
        Assert.False(DalamudTravelReadiness.ShouldCloseOwnedAddon("SelectString", protectedAddons));
        Assert.True(DalamudTravelReadiness.ShouldCloseOwnedAddon("Talk", protectedAddons));
        Assert.Null(DalamudGilVendorBuyRuntime.ProtectedOwnedAddonsForPendingAethernet(null));
    }

    [Fact]
    public void Incomplete_aethernet_leg_retries_after_lifestream_timeout_window()
    {
        var submittedAt = DateTimeOffset.Parse("2026-08-15T23:22:45Z");

        Assert.Equal(
            GilVendorAethernetRecoveryState.Continue,
            Decide(submittedAt, submittedAt.AddSeconds(34), submissionCount: 1));
        Assert.Equal(
            GilVendorAethernetRecoveryState.Retry,
            Decide(submittedAt, submittedAt.AddSeconds(35), submissionCount: 1));
    }

    [Fact]
    public void Repeated_incomplete_aethernet_legs_are_bounded()
    {
        var submittedAt = DateTimeOffset.Parse("2026-08-15T23:22:45Z");

        Assert.Equal(
            GilVendorAethernetRecoveryState.Exhausted,
            Decide(submittedAt, submittedAt.AddSeconds(35), submissionCount: 3));
    }

    [Fact]
    public void Recovery_does_not_fire_outside_the_main_aetheryte_territory()
    {
        var submittedAt = DateTimeOffset.Parse("2026-08-15T23:22:45Z");

        Assert.Equal(
            GilVendorAethernetRecoveryState.Continue,
            DalamudGilVendorBuyRuntime.DetermineAethernetRecovery(
                currentTerritoryId: 129,
                targetTerritoryId: 131,
                routeAetheryteTerritoryId: 130,
                routeAethernetId: 35,
                requestedAethernetId: 35,
                submittedAt,
                submissionCount: 1,
                observedAt: submittedAt.AddMinutes(1)));
    }

    private static GilVendorAethernetRecoveryState Decide(
        DateTimeOffset submittedAt,
        DateTimeOffset observedAt,
        int submissionCount) =>
        DalamudGilVendorBuyRuntime.DetermineAethernetRecovery(
            currentTerritoryId: 130,
            targetTerritoryId: 131,
            routeAetheryteTerritoryId: 130,
            routeAethernetId: 35,
            requestedAethernetId: 35,
            submittedAt,
            submissionCount,
            observedAt);
}
