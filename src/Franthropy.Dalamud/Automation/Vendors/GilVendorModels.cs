using System.Numerics;

namespace Franthropy.Dalamud.Automation.Vendors;

public sealed record GilVendorOffer(
    uint ItemId,
    string ItemName,
    uint IconId,
    uint UnitPriceGil,
    uint ShopId,
    uint ShopRowIndex,
    uint NpcId,
    string NpcName,
    uint TerritoryId,
    Vector3 Position,
    IReadOnlyList<uint> RouteAetheryteIds)
{
    public IReadOnlyList<GilVendorTravelRoute> TravelRoutes { get; init; } = [];

    public IReadOnlyList<uint> RequiredQuestIds { get; init; } = [];

    public IReadOnlyList<GilVendorTravelRoute> EffectiveTravelRoutes =>
        TravelRoutes.Count != 0
            ? TravelRoutes
            : RouteAetheryteIds.Select(id => new GilVendorTravelRoute(id)).ToArray();

    public bool IsExecutableOrdinaryGilOffer =>
        ItemId != 0 &&
        !string.IsNullOrWhiteSpace(ItemName) &&
        UnitPriceGil != 0 &&
        ShopId != 0 &&
        NpcId != 0 &&
        !string.IsNullOrWhiteSpace(NpcName) &&
        TerritoryId != 0 &&
        float.IsFinite(Position.X) &&
        float.IsFinite(Position.Y) &&
        float.IsFinite(Position.Z);
}

public sealed record GilVendorTravelRoute(
    uint AetheryteId,
    uint? AethernetId = null,
    uint? AetheryteTerritoryId = null);

public sealed record GilVendorBuyRequest(
    GilVendorOffer Offer,
    uint Quantity,
    ulong MaxTotalGil)
{
    public static GilVendorBuyRequestCreateResult Create(GilVendorOffer offer, uint quantity)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (!offer.IsExecutableOrdinaryGilOffer)
            return GilVendorBuyRequestCreateResult.Fail("InvalidOffer", "The selected offer is not an executable ordinary-gil offer.");
        if (quantity == 0)
            return GilVendorBuyRequestCreateResult.Fail("InvalidQuantity", "Vendor buy quantity must be greater than zero.");

        var total = checked((ulong)offer.UnitPriceGil * quantity);
        if (total > int.MaxValue)
            return GilVendorBuyRequestCreateResult.Fail("GilTotalOverflow", "The vendor buy total exceeds the supported per-request gil guard.");

        return GilVendorBuyRequestCreateResult.Success(new(offer, quantity, total));
    }
}

public sealed record GilVendorBuyRequestCreateResult(
    bool IsSuccess,
    string Code,
    string Message,
    GilVendorBuyRequest? Request)
{
    public static GilVendorBuyRequestCreateResult Success(GilVendorBuyRequest request) =>
        new(true, "Ready", "The vendor buy request is ready.", request);

    public static GilVendorBuyRequestCreateResult Fail(string code, string message) =>
        new(false, code, message, null);
}

public sealed record GilVendorShopRow(
    int RowIndex,
    uint ItemId,
    uint UnitPriceGil);

public sealed record GilVendorShopReadResult(
    bool IsSuccess,
    string Code,
    string Message,
    IReadOnlyList<GilVendorShopRow> Rows)
{
    public static GilVendorShopReadResult Success(IReadOnlyList<GilVendorShopRow> rows) =>
        new(true, "Ready", "Read the open ordinary-gil shop.", rows);

    public static GilVendorShopReadResult Fail(string code, string message) =>
        new(false, code, message, []);
}

public sealed record GilVendorMenuAdvanceResult(
    bool MenuPresented,
    bool Advanced,
    string Code,
    string Message)
{
    public static GilVendorMenuAdvanceResult NotPresented() =>
        new(false, false, "NoMenu", "No vendor menu is presented.");

    public static GilVendorMenuAdvanceResult Selected(string entry) =>
        new(true, true, "Selected", $"Selected vendor menu entry '{entry}'.");

    public static GilVendorMenuAdvanceResult NoMatchingEntry() =>
        new(true, false, "OfferUnavailable", "The reached vendor did not present the reviewed shop.");
}

public sealed record GilVendorShopMatchResult(
    bool IsSuccess,
    string Code,
    string Message,
    GilVendorShopRow? Row)
{
    public static GilVendorShopMatchResult Success(GilVendorShopRow row) =>
        new(true, "Ready", "Matched the catalog offer in the live ordinary-gil shop.", row);

    public static GilVendorShopMatchResult Fail(string code, string message) =>
        new(false, code, message, null);
}

public enum GilVendorAccessState
{
    Unknown,
    Unavailable,
    Probeable,
    Verified,
}

public sealed record GilVendorAccessAssessment(
    GilVendorAccessState State,
    string Code,
    string Message,
    uint? RouteAetheryteId = null,
    uint? RouteAethernetId = null,
    uint? RouteAetheryteTerritoryId = null)
{
    public bool IsEligible => State is GilVendorAccessState.Probeable or GilVendorAccessState.Verified;
}

public sealed record GilVendorPurchaseObservation(
    int ItemCount,
    ulong Gil);

public enum GilVendorPurchaseEvidence
{
    Pending,
    Verified,
    Indeterminate,
}

public sealed record GilVendorPurchaseReceipt(
    uint ItemId,
    uint Quantity,
    ulong SpentGil,
    int BeforeItemCount,
    int AfterItemCount,
    ulong BeforeGil,
    ulong AfterGil);

public sealed record GilVendorPurchaseEvidenceResult(
    GilVendorPurchaseEvidence Evidence,
    string Code,
    string Message,
    GilVendorPurchaseReceipt? Receipt = null);
