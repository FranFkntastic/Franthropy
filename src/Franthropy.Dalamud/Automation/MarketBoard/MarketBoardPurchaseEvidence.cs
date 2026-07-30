namespace Franthropy.Dalamud.Automation.MarketBoard;

public sealed record MarketBoardPurchaseIntent(
    ulong ListingId,
    uint ItemId,
    uint Quantity,
    uint UnitPrice,
    ulong TotalGil);

public sealed record MarketBoardPurchasePacket(
    ulong ListingId,
    uint ItemId,
    uint Quantity,
    uint UnitPrice);

public enum MarketBoardPurchaseEvidence
{
    Unrelated,
    Verified,
    Rejected,
    Indeterminate,
}

public sealed record MarketBoardPurchaseEvidenceResult(
    MarketBoardPurchaseEvidence Evidence,
    long? GilDelta);

public static class MarketBoardPurchaseEvidenceClassifier
{
    public static bool PacketMatches(
        MarketBoardPurchaseIntent intent,
        MarketBoardPurchasePacket packet) =>
        packet.ListingId == intent.ListingId &&
        packet.ItemId == intent.ItemId &&
        packet.Quantity == intent.Quantity &&
        packet.UnitPrice == intent.UnitPrice;

    public static MarketBoardPurchaseEvidenceResult ClassifyResponse(
        MarketBoardPurchaseIntent intent,
        uint responseItemId,
        uint responseQuantity,
        uint? gilBefore,
        uint? gilAfter)
    {
        if (responseItemId != intent.ItemId || responseQuantity != intent.Quantity)
            return new(MarketBoardPurchaseEvidence.Unrelated, null);
        if (gilBefore is not { } before || gilAfter is not { } after)
            return new(MarketBoardPurchaseEvidence.Indeterminate, null);

        var delta = (long)before - after;
        if (delta == (long)intent.TotalGil)
            return new(MarketBoardPurchaseEvidence.Verified, delta);
        if (delta == 0)
            return new(MarketBoardPurchaseEvidence.Rejected, delta);
        return new(MarketBoardPurchaseEvidence.Indeterminate, delta);
    }
}
