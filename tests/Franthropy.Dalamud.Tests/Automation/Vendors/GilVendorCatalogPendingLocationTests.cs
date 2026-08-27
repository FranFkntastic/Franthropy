using System.Numerics;
using Franthropy.Dalamud.Automation.Vendors;

namespace Franthropy.Dalamud.Tests.Automation.Vendors;

public class GilVendorCatalogPendingLocationTests
{
    private static GilVendorOffer PendingOffer(
        uint itemId = 5111,
        uint npcId = 1000999,
        string npcName = "Soemrwyb") => new(
        itemId,
        "Iron Ore",
        2101,
        18,
        262173,
        3,
        npcId,
        npcName,
        0,
        default,
        [])
    {
        RequiredQuestIds = [],
    };

    private static GilVendorOffer PlacedOffer(
        uint itemId = 4552,
        uint npcId = 1000239,
        string npcName = "Sylbfohc") => new(
        itemId,
        "Hi-Potion",
        2101,
        146,
        262180,
        0,
        npcId,
        npcName,
        129,
        new Vector3(1f, 2f, 3f),
        [2])
    {
        TravelRoutes = [new GilVendorTravelRoute(2, AetheryteTerritoryId: 129)],
    };

    [Fact]
    public void PendingOffers_AreNotFindable_UntilLocationObserved()
    {
        var catalog = GilVendorCatalog.Create([PlacedOffer()], [PendingOffer()]);

        Assert.Empty(catalog.FindOffers(5111));
        Assert.True(catalog.PendingByNpcId.ContainsKey(1000999));
        Assert.False(catalog.PendingByNpcId.ContainsKey(1000239));
    }

    [Fact]
    public void ObservedLocation_PromotesPendingOffer_IntoExecutableCatalog()
    {
        var catalog = GilVendorCatalog.Create([PlacedOffer()], [PendingOffer()]);

        var promoted = catalog.WithObservedLocation(
            1000999,
            128,
            new Vector3(-149.2f, 18.2f, 20.5f),
            [new GilVendorTravelRoute(2, AetheryteTerritoryId: 128)],
            [2]);

        var offers = promoted.FindOffers(5111);
        var offer = Assert.Single(offers);
        Assert.Equal(1000999u, offer.NpcId);
        Assert.Equal(128u, offer.TerritoryId);
        Assert.True(offer.IsExecutableOrdinaryGilOffer);
        Assert.Empty(promoted.PendingByNpcId);
    }

    [Fact]
    public void ObservedLocation_ForUnknownNpc_ReturnsSameCatalog()
    {
        var catalog = GilVendorCatalog.Create([PlacedOffer()], []);

        var same = catalog.WithObservedLocation(
            999999,
            128,
            new Vector3(0f, 0f, 0f),
            [],
            []);

        Assert.Same(catalog, same);
    }

    [Fact]
    public void Promotion_PreservesOtherPendingOffers()
    {
        var pending = new List<GilVendorOffer> { PendingOffer(), PendingOffer(npcId: 1008837, npcName: "material supplier") };
        var catalog = GilVendorCatalog.Create([PlacedOffer()], pending);

        var promoted = catalog.WithObservedLocation(
            1000999,
            128,
            new Vector3(1f, 1f, 1f),
            [],
            []);

        var remaining = promoted.PendingByNpcId;
        Assert.True(remaining.ContainsKey(1008837));
        Assert.False(remaining.ContainsKey(1000999));
        Assert.Single(promoted.FindOffers(5111));
    }
}
