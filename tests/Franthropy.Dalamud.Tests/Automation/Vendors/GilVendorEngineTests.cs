using Franthropy.Dalamud.Automation.Vendors;
using System.Numerics;

namespace Franthropy.Dalamud.Tests.Automation.Vendors;

public sealed class GilVendorEngineTests
{
    [Fact]
    public void Catalog_excludes_invalid_offers_and_orders_deterministically()
    {
        var catalog = GilVendorCatalog.Create(
        [
            Offer(itemId: 1, npcId: 30, price: 12),
            Offer(itemId: 1, npcId: 20, price: 8),
            Offer(itemId: 2, npcId: 10, price: 0),
        ]);

        Assert.Equal([20u, 30u], catalog.FindOffers(1).Select(offer => offer.NpcId));
        Assert.Empty(catalog.FindOffers(2));
    }

    [Fact]
    public void Request_freezes_exact_total_and_rejects_zero()
    {
        var offer = Offer(itemId: 7, price: 18);

        var accepted = GilVendorBuyRequest.Create(offer, 30);
        var rejected = GilVendorBuyRequest.Create(offer, 0);

        Assert.True(accepted.IsSuccess);
        Assert.Equal(540UL, accepted.Request!.MaxTotalGil);
        Assert.False(rejected.IsSuccess);
        Assert.Equal("InvalidQuantity", rejected.Code);
    }

    [Fact]
    public void Matcher_requires_one_exact_item_and_price_row()
    {
        var request = GilVendorBuyRequest.Create(Offer(itemId: 7, price: 18), 3).Request!;

        var match = GilVendorShopMatcher.FindMatchingRow(
            request,
            [new(0, 7, 18), new(1, 8, 18)]);
        var wrongPrice = GilVendorShopMatcher.FindMatchingRow(
            request,
            [new(0, 7, 19)]);
        var ambiguous = GilVendorShopMatcher.FindMatchingRow(
            request,
            [new(0, 7, 18), new(1, 7, 18)]);

        Assert.True(match.IsSuccess);
        Assert.Equal(0, match.Row!.RowIndex);
        Assert.Equal("PriceMismatch", wrongPrice.Code);
        Assert.Equal("AmbiguousLiveOffer", ambiguous.Code);
    }

    [Fact]
    public void Receipt_requires_exact_item_and_gil_deltas()
    {
        var request = GilVendorBuyRequest.Create(Offer(itemId: 7, price: 18), 3).Request!;

        var pending = GilVendorPurchaseEvidenceClassifier.Classify(
            request,
            new(10, 1_000),
            new(10, 1_000));
        var verified = GilVendorPurchaseEvidenceClassifier.Classify(
            request,
            new(10, 1_000),
            new(13, 946));
        var mismatch = GilVendorPurchaseEvidenceClassifier.Classify(
            request,
            new(10, 1_000),
            new(13, 945));
        var itemLag = GilVendorPurchaseEvidenceClassifier.Classify(
            request,
            new(10, 1_000),
            new(10, 946));
        var gilLag = GilVendorPurchaseEvidenceClassifier.Classify(
            request,
            new(10, 1_000),
            new(13, 1_000));

        Assert.Equal(GilVendorPurchaseEvidence.Pending, pending.Evidence);
        Assert.Equal(GilVendorPurchaseEvidence.Verified, verified.Evidence);
        Assert.Equal(54UL, verified.Receipt!.SpentGil);
        Assert.Equal(GilVendorPurchaseEvidence.Indeterminate, mismatch.Evidence);
        Assert.Equal(GilVendorPurchaseEvidence.Reconciling, itemLag.Evidence);
        Assert.Equal(GilVendorPurchaseEvidence.Reconciling, gilLag.Evidence);
    }

    [Fact]
    public void Dynamic_location_fallback_only_scans_unresolved_npcs()
    {
        var unresolved = DalamudVendorLocationCatalogBuilder.FindUnresolvedNpcIds(
            new HashSet<uint> { 10, 20, 30 },
            [10u, 30u]);

        Assert.Equal([20u], unresolved);
        Assert.True(DalamudVendorLocationCatalogBuilder.TryBuildPlaneventPath(
            "ex1/02_rvr/r1t1/level/r1t1",
            out var path));
        Assert.Equal("bg/ex1/02_rvr/r1t1/level/planevent.lgb", path);
        Assert.False(DalamudVendorLocationCatalogBuilder.TryBuildPlaneventPath(
            "common/invalid",
            out _));
    }

    [Fact]
    public void Travel_routes_prefer_a_direct_aetheryte_in_the_vendor_territory()
    {
        var routes = DalamudGilVendorCatalogBuilder.ResolveTravelRoutes(
            200,
            [
                new(10, 200, 7, true),
                new(11, 200, 7, false),
                new(12, 201, 7, true),
            ]);

        Assert.Equal([new GilVendorTravelRoute(10, AetheryteTerritoryId: 200)], routes);
    }

    [Fact]
    public void Travel_routes_join_a_city_aetheryte_to_a_destination_aethernet_shard()
    {
        var routes = DalamudGilVendorCatalogBuilder.ResolveTravelRoutes(
            200,
            [
                new(10, 201, 7, true),
                new(11, 200, 7, false),
                new(12, 202, 8, true),
            ]);

        Assert.Equal([new GilVendorTravelRoute(10, 11, 201)], routes);
    }

    [Fact]
    public void Travel_routes_do_not_invent_a_route_without_a_shared_aethernet_group()
    {
        var routes = DalamudGilVendorCatalogBuilder.ResolveTravelRoutes(
            200,
            [
                new(10, 201, 7, true),
                new(11, 200, 0, false),
                new(12, 202, 8, true),
            ]);

        Assert.Empty(routes);
    }

    [Fact]
    public void Catalog_binding_merges_shop_and_prehandler_unlock_quests_per_vendor()
    {
        var bindings = new Dictionary<uint, Dictionary<uint, HashSet<uint>>>();

        DalamudGilVendorCatalogBuilder.AddShopNpc(bindings, 500, 700, 67023, 0);
        DalamudGilVendorCatalogBuilder.AddShopNpc(bindings, 500, 700, 67023, 67111);

        Assert.Equal([67023u, 67111u], bindings[500][700].Order());
    }

    [Fact]
    public void Shop_unlock_assessment_excludes_an_incomplete_required_quest()
    {
        var offer = Offer(itemId: 5371) with
        {
            RequiredQuestIds = [67023],
        };

        var assessment = DalamudGilVendorAccessReader.AssessShopUnlocks(
            offer,
            questId => questId == 67023 ? false : null);

        Assert.NotNull(assessment);
        Assert.Equal(GilVendorAccessState.Unavailable, assessment.State);
        Assert.Equal("ShopLocked", assessment.Code);
    }

    [Fact]
    public void Shop_unlock_assessment_fails_closed_when_completion_cannot_be_read()
    {
        var offer = Offer(itemId: 5371) with
        {
            RequiredQuestIds = [67023],
        };

        var assessment = DalamudGilVendorAccessReader.AssessShopUnlocks(offer, _ => null);

        Assert.NotNull(assessment);
        Assert.Equal(GilVendorAccessState.Unknown, assessment.State);
        Assert.Equal("ShopUnlockStateUnavailable", assessment.Code);
    }

    [Fact]
    public void Reviewed_offer_snapshot_preserves_unlock_requirements_for_execution_recheck()
    {
        var offer = Offer(itemId: 5371) with
        {
            RequiredQuestIds = [67023],
        };

        var restored = Franthropy.Dalamud.Automation.Vendors.Coordination.GilVendorBuyOfferSnapshot
            .From(offer)
            .ToOffer();

        Assert.Equal([67023u], restored.RequiredQuestIds);
    }

    private static GilVendorOffer Offer(
        uint itemId,
        uint npcId = 10,
        uint price = 8) =>
        new(
            itemId,
            $"Item {itemId}",
            1,
            price,
            100,
            0,
            npcId,
            $"NPC {npcId}",
            129,
            new Vector3(1, 2, 3),
            [2]);
}
