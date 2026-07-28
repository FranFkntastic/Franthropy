namespace Franthropy.Dalamud.Automation.Vendors;

public sealed class GilVendorCatalog
{
    private readonly Dictionary<uint, IReadOnlyList<GilVendorOffer>> offersByItemId;

    private GilVendorCatalog(Dictionary<uint, IReadOnlyList<GilVendorOffer>> offersByItemId)
    {
        this.offersByItemId = offersByItemId;
    }

    public static GilVendorCatalog Create(IEnumerable<GilVendorOffer> offers)
    {
        ArgumentNullException.ThrowIfNull(offers);
        return new(offers
            .Where(offer => offer.IsExecutableOrdinaryGilOffer)
            .Distinct()
            .GroupBy(offer => offer.ItemId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<GilVendorOffer>)group
                    .OrderBy(offer => offer.UnitPriceGil)
                    .ThenBy(offer => offer.NpcName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(offer => offer.NpcId)
                    .ThenBy(offer => offer.ShopId)
                    .ThenBy(offer => offer.TerritoryId)
                    .ToArray()));
    }

    public IReadOnlyList<GilVendorOffer> FindOffers(uint itemId) =>
        offersByItemId.TryGetValue(itemId, out var offers) ? offers : [];

    public GilVendorBuyRequestCreateResult TryCreateRequest(
        uint itemId,
        uint quantity,
        uint? preferredNpcId = null)
    {
        var offers = FindOffers(itemId);
        if (offers.Count == 0)
            return GilVendorBuyRequestCreateResult.Fail("OfferNotCataloged", $"Item {itemId} has no executable ordinary-gil offer.");

        var offer = preferredNpcId is { } npcId
            ? offers.FirstOrDefault(candidate => candidate.NpcId == npcId)
            : offers[0];
        return offer is null
            ? GilVendorBuyRequestCreateResult.Fail("PreferredVendorUnavailable", $"NPC {preferredNpcId} does not provide an executable offer for item {itemId}.")
            : GilVendorBuyRequest.Create(offer, quantity);
    }
}

public static class GilVendorShopMatcher
{
    public static GilVendorShopMatchResult FindMatchingRow(
        GilVendorBuyRequest request,
        IReadOnlyList<GilVendorShopRow> rows)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rows);

        var itemRows = rows.Where(row => row.ItemId == request.Offer.ItemId).ToArray();
        if (itemRows.Length == 0)
            return GilVendorShopMatchResult.Fail("OfferNotInLiveShop", "The open shop does not contain the catalog item.");

        var exact = itemRows
            .Where(row => row.UnitPriceGil == request.Offer.UnitPriceGil)
            .OrderBy(row => row.RowIndex)
            .ToArray();
        if (exact.Length == 0)
            return GilVendorShopMatchResult.Fail("PriceMismatch", "The open shop contains the item at a different unit gil price.");
        if (exact.Length > 1)
            return GilVendorShopMatchResult.Fail("AmbiguousLiveOffer", "The open shop contains duplicate item and price matches.");

        return GilVendorShopMatchResult.Success(exact[0]);
    }
}

public static class GilVendorPurchaseEvidenceClassifier
{
    public static GilVendorPurchaseEvidenceResult Classify(
        GilVendorBuyRequest request,
        GilVendorPurchaseObservation before,
        GilVendorPurchaseObservation after)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var itemDelta = (long)after.ItemCount - before.ItemCount;
        var gilDelta = before.Gil >= after.Gil
            ? before.Gil - after.Gil
            : ulong.MaxValue;
        if (itemDelta == 0 && after.Gil == before.Gil)
            return new(GilVendorPurchaseEvidence.Pending, "Unchanged", "The purchase has not produced an observable inventory or gil change.");

        if (itemDelta == request.Quantity && gilDelta == request.MaxTotalGil)
        {
            return new(
                GilVendorPurchaseEvidence.Verified,
                "Verified",
                "The exact item and gil deltas verified the ordinary-gil purchase.",
                new(
                    request.Offer.ItemId,
                    request.Quantity,
                    gilDelta,
                    before.ItemCount,
                    after.ItemCount,
                    before.Gil,
                    after.Gil));
        }

        return new(
            GilVendorPurchaseEvidence.Indeterminate,
            "DeltaMismatch",
            $"Observed item delta {itemDelta} and gil delta {gilDelta}, expected {request.Quantity} and {request.MaxTotalGil}.");
    }
}
