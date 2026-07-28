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

        Assert.Equal(GilVendorPurchaseEvidence.Pending, pending.Evidence);
        Assert.Equal(GilVendorPurchaseEvidence.Verified, verified.Evidence);
        Assert.Equal(54UL, verified.Receipt!.SpentGil);
        Assert.Equal(GilVendorPurchaseEvidence.Indeterminate, mismatch.Evidence);
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
