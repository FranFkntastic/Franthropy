using Franthropy.Dalamud.Automation.Vendors;

namespace Franthropy.Dalamud.Automation.Vendors.Coordination;

public interface IGilVendorBuyRunStore
{
    GilVendorBuyRunSnapshot? LoadCurrent();
    void Save(GilVendorBuyRunSnapshot snapshot);
}

public interface IGilVendorBuyRuntime
{
    GilVendorInventorySnapshot CaptureInventory(IReadOnlyCollection<uint> itemIds);
    bool HasCapacity(IReadOnlyDictionary<uint, int> quantities, out string message);
    GilVendorReachResult AdvanceToOpenShop(GilVendorOffer offer);
    void ResetVendorApproach();
    GilVendorShopReadResult ReadShopRows();
    bool TrySubmitPurchase(GilVendorShopRow row, uint quantity, out string error);
    bool TryConfirmPurchasePrompt();
    int ResolveMaximumBatch(uint itemId);
    void CloseShop();
    void BeginAutomation();
    void EndAutomation();

    /// <summary>
    /// Live, zero-wait check that the open shop can accept a purchase callback right now
    /// (no blocking child addon, no confirmation still closing). Runtimes that cannot
    /// observe the UI report ready so submission behaves as before.
    /// </summary>
    GilVendorPurchaseUiReadiness ReadPurchaseUiReadiness() => GilVendorPurchaseUiReadiness.AssumedReady;

    /// <summary>
    /// Confirmation-prompt state for the most recent <see cref="TrySubmitPurchase"/> call.
    /// <see cref="GilVendorPurchasePromptObservation.LifecycleObserved"/> is true only when
    /// addon lifecycle events prove whether the confirmation opened at all.
    /// </summary>
    GilVendorPurchasePromptObservation ObservePurchasePrompt() => GilVendorPurchasePromptObservation.Unobserved;

    /// <summary>
    /// Clicks Yes at most once per submission, and only on a visible confirmation whose
    /// text names the expected gil total (and quantity when above one).
    /// </summary>
    GilVendorPromptConfirmResult ConfirmPurchasePrompt(ulong expectedGil, uint quantity) =>
        TryConfirmPurchasePrompt()
            ? new(GilVendorPromptConfirmState.Confirmed, null)
            : new(GilVendorPromptConfirmState.NotPresent, null);
}

public enum GilVendorPurchaseUiReadinessState
{
    Ready,
    Blocked,
    ShopClosed,
}

public sealed record GilVendorPurchaseUiReadiness(
    GilVendorPurchaseUiReadinessState State,
    bool ShopOpen,
    int ShopBlockingAddons,
    bool SelectYesnoVisible)
{
    public static GilVendorPurchaseUiReadiness AssumedReady { get; } = new(GilVendorPurchaseUiReadinessState.Ready, true, 0, false);

    public string Describe() =>
        $"shopOpen={ShopOpen} shopBlocking={ShopBlockingAddons} yesNoVisible={SelectYesnoVisible}";
}

public sealed record GilVendorPurchasePromptObservation
{
    public static GilVendorPurchasePromptObservation Unobserved { get; } = new();

    /// <summary>Increments for every submission; ties an observation to one callback.</summary>
    public long AttemptSequence { get; init; }
    public bool LifecycleObserved { get; init; }
    public bool FireCallbackReturned { get; init; } = true;
    public int PromptOpenCount { get; init; }
    public bool PromptOpened => PromptOpenCount > 0;
    public bool PromptClosed { get; init; }
    public bool PromptConfirmedByUs { get; init; }
    public string? PromptText { get; init; }
    public bool ShopOpen { get; init; } = true;
    public int ShopBlockingAddons { get; init; }
    public bool SelectYesnoVisible { get; init; }
}

public enum GilVendorPromptConfirmState
{
    NotPresent,
    Waiting,
    Confirmed,
    AlreadyConfirmed,
    UnexpectedPrompt,
}

public sealed record GilVendorPromptConfirmResult(GilVendorPromptConfirmState State, string? PromptText);
