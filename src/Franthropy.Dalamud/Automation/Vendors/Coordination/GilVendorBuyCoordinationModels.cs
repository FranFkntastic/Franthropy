using System.Numerics;
using Franthropy.Dalamud.Automation.Vendors;

namespace Franthropy.Dalamud.Automation.Vendors.Coordination;

public enum GilVendorBuyPhase
{
    RefreshPreconditions,
    ReachVendor,
    ValidateShop,
    PurchaseLine,
    VerifyReceipt,
    Paused,
    Completed,
    Stopped,
    Failed,
    Indeterminate,
}

public sealed class GilVendorBuyPlan
{
    public ulong MaximumApprovedGil { get; init; }
    public IReadOnlyList<GilVendorBuyLineSnapshot> Lines { get; init; } = [];
    public IReadOnlyList<GilVendorBuyStopSnapshot> Stops { get; init; } = [];
    public GilVendorBuyFallbackReplanner? FallbackReplanner { get; init; }
}

[Serializable]
public sealed class GilVendorBuyRunSnapshot
{
    public string RunId { get; set; } = string.Empty;
    public string ContextSignature { get; set; } = string.Empty;
    public ulong MaximumApprovedGil { get; set; }
    public GilVendorBuyPhase Phase { get; set; }
    public GilVendorBuyPhase ResumePhase { get; set; }
    public bool StopRequested { get; set; }
    public int StopIndex { get; set; }
    public int LineIndex { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<GilVendorBuyLineSnapshot> Lines { get; set; } = [];
    public List<GilVendorBuyStopSnapshot> Stops { get; set; } = [];
    public GilVendorBuyArmedIntentSnapshot? ArmedPurchase { get; set; }
    public List<GilVendorBuyReceiptSnapshot> Receipts { get; set; } = [];
}

[Serializable]
public sealed class GilVendorBuyLineSnapshot
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int ApprovedQuantity { get; set; }
    public int? TargetTotalQuantity { get; set; }
    public int PurchasedQuantity { get; set; }
    public int PurchaseRetryCount { get; set; }
    public uint UnitPriceGil { get; set; }
    public ulong ApprovedGilCeiling { get; set; }
    public bool VendorUnavailable { get; set; }
    public string Status { get; set; } = "Waiting";
    public GilVendorBuyOfferSnapshot? Offer { get; set; }
    public List<GilVendorBuyOfferSnapshot> AlternativeOffers { get; set; } = [];
}

[Serializable]
public sealed class GilVendorBuyStopSnapshot
{
    public uint NpcId { get; set; }
    public uint ShopId { get; set; }
    public uint TerritoryId { get; set; }
    public string NpcName { get; set; } = string.Empty;
    public List<uint> ItemIds { get; set; } = [];
    public Dictionary<uint, int> MatchedShopRows { get; set; } = [];
    public bool ShopValidated { get; set; }
}

[Serializable]
public sealed class GilVendorBuyOfferSnapshot
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public uint IconId { get; set; }
    public uint UnitPriceGil { get; set; }
    public uint ShopId { get; set; }
    public uint ShopRowIndex { get; set; }
    public uint NpcId { get; set; }
    public string NpcName { get; set; } = string.Empty;
    public uint TerritoryId { get; set; }
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float PositionZ { get; set; }
    public List<uint> RouteAetheryteIds { get; set; } = [];
    public List<GilVendorBuyRouteSnapshot> TravelRoutes { get; set; } = [];
    public List<uint> RequiredQuestIds { get; set; } = [];

    public static GilVendorBuyOfferSnapshot From(GilVendorOffer offer) => new()
    {
        ItemId = offer.ItemId,
        ItemName = offer.ItemName,
        IconId = offer.IconId,
        UnitPriceGil = offer.UnitPriceGil,
        ShopId = offer.ShopId,
        ShopRowIndex = offer.ShopRowIndex,
        NpcId = offer.NpcId,
        NpcName = offer.NpcName,
        TerritoryId = offer.TerritoryId,
        PositionX = offer.Position.X,
        PositionY = offer.Position.Y,
        PositionZ = offer.Position.Z,
        RouteAetheryteIds = [.. offer.RouteAetheryteIds],
        TravelRoutes = offer.EffectiveTravelRoutes.Select(GilVendorBuyRouteSnapshot.From).ToList(),
        RequiredQuestIds = [.. offer.RequiredQuestIds],
    };

    public GilVendorOffer ToOffer() => new(
            ItemId,
            ItemName,
            IconId,
            UnitPriceGil,
            ShopId,
            ShopRowIndex,
            NpcId,
            NpcName,
            TerritoryId,
            new Vector3(PositionX, PositionY, PositionZ),
            RouteAetheryteIds)
        {
            TravelRoutes = TravelRoutes.Select(route => route.ToRoute()).ToArray(),
            RequiredQuestIds = [.. RequiredQuestIds],
        };
}

[Serializable]
public sealed class GilVendorBuyRouteSnapshot
{
    public uint AetheryteId { get; set; }
    public uint? AethernetId { get; set; }
    public uint? AetheryteTerritoryId { get; set; }

    public static GilVendorBuyRouteSnapshot From(GilVendorTravelRoute route) => new()
    {
        AetheryteId = route.AetheryteId,
        AethernetId = route.AethernetId,
        AetheryteTerritoryId = route.AetheryteTerritoryId,
    };

    public GilVendorTravelRoute ToRoute() => new(AetheryteId, AethernetId, AetheryteTerritoryId);
}

[Serializable]
public sealed class GilVendorBuyArmedIntentSnapshot
{
    public uint ItemId { get; set; }
    public int Quantity { get; set; }
    public ulong ExpectedGil { get; set; }
    public int ShopRowIndex { get; set; }
    public int BeforeItemCount { get; set; }
    public ulong BeforeGil { get; set; }
    public int RetryCount { get; set; }
    public DateTime ArmedAtUtc { get; set; }
}

[Serializable]
public sealed class GilVendorBuyReceiptSnapshot
{
    public uint ItemId { get; set; }
    public int Quantity { get; set; }
    public ulong SpentGil { get; set; }
    public int BeforeItemCount { get; set; }
    public int AfterItemCount { get; set; }
    public ulong BeforeGil { get; set; }
    public ulong AfterGil { get; set; }
    public DateTime VerifiedAtUtc { get; set; }
}

public sealed record GilVendorInventorySnapshot(
    bool IsComplete,
    ulong? Gil,
    IReadOnlyDictionary<uint, int> ItemCounts,
    string Message);

public enum GilVendorReachState
{
    Waiting,
    ShopOpen,
    Unavailable,
    Failed,
}

public sealed record GilVendorReachResult(GilVendorReachState State, string Message);

public sealed record GilVendorBuyFallbackLine(
    uint ItemId,
    string ItemName,
    int RemainingQuantity,
    GilVendorBuyOfferSnapshot Offer,
    IReadOnlyList<GilVendorBuyOfferSnapshot> AlternativeOffers);

public sealed record GilVendorBuyFallbackRequest(
    GilVendorBuyStopSnapshot UnreachableStop,
    IReadOnlyList<GilVendorBuyFallbackLine> Lines);

public sealed record GilVendorBuyFallbackSelection(uint ItemId, GilVendorBuyOfferSnapshot Offer);

public sealed record GilVendorBuyFallbackPlan(
    IReadOnlyList<GilVendorBuyStopSnapshot> ReplacementStops,
    IReadOnlyList<GilVendorBuyFallbackSelection> Selections,
    string Message);

public delegate GilVendorBuyFallbackPlan GilVendorBuyFallbackReplanner(GilVendorBuyFallbackRequest request);
