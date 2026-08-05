using Franthropy.Dalamud.Automation.Inventory;

namespace Franthropy.Dalamud.Automation.Retainers;

public sealed record RetainerAutomationTarget(ulong RetainerId, string RetainerName);
public sealed record RetainerAutomationRosterResult(
    bool Success,
    IReadOnlyList<RetainerAutomationTarget> Retainers,
    string Code,
    string Message)
{
    public static RetainerAutomationRosterResult Succeeded(IReadOnlyList<RetainerAutomationTarget> retainers) =>
        new(true, retainers, "RetainerRosterScanned", $"Observed {retainers.Count} available retainer(s).");

    public static RetainerAutomationRosterResult Failed(string code, string message) =>
        new(false, [], code, message);
}

public sealed record RetainerAutomationResult(bool Success, string Code, string Message)
{
    public static RetainerAutomationResult Succeeded(string code, string message) => new(true, code, message);
    public static RetainerAutomationResult Failed(string code, string message) => new(false, code, message);
}

public sealed record RetainerLocalUiObservation(
    bool AgentAvailable,
    bool AgentActive,
    bool AddonReady,
    bool AddonVisible,
    bool OpenerAvailable,
    uint AddonId,
    ulong ActiveRetainerId,
    uint RetainerObjectId);

public sealed record RetainerAutomationOpenResult(
    bool Success,
    RetainerAutomationTarget? Target,
    string Code,
    string Message)
{
    public static RetainerAutomationOpenResult Succeeded(RetainerAutomationTarget target, string code, string message) =>
        new(true, target, code, message);

    public static RetainerAutomationOpenResult Failed(string code, string message) =>
        new(false, null, code, message);
}

public sealed record RetainerRetrievalResult(bool Success, int Transferred, string Code, string Message);
public sealed record RetainerDepositResult(bool Success, int Transferred, string Code, string Message);
public sealed record RetainerMarketListingTarget(
    int SlotIndex,
    uint ItemId,
    int Quantity,
    bool IsHq,
    uint? UnitPrice);
public sealed record RetainerMarketListingScanResult(
    bool Success,
    IReadOnlyList<RetainerMarketListingTarget> Listings,
    string Code,
    string Message)
{
    public static RetainerMarketListingScanResult Succeeded(IReadOnlyList<RetainerMarketListingTarget> listings) =>
        new(true, listings, "RetainerMarketListingsScanned", $"Observed {listings.Count} live retainer market listing(s).");

    public static RetainerMarketListingScanResult Failed(string code, string message) =>
        new(false, [], code, message);
}

public enum RetainerMarketListingPostOutcome
{
    FailedBeforeSend,
    Committed,
    Indeterminate,
}

public sealed record RetainerMarketListingPostResult(
    RetainerMarketListingPostOutcome Outcome,
    RetainerMarketListingTarget? Listing,
    string Code,
    string Message)
{
    public bool Success => Outcome == RetainerMarketListingPostOutcome.Committed;
    public bool RequestSent => Outcome != RetainerMarketListingPostOutcome.FailedBeforeSend;

    public static RetainerMarketListingPostResult Succeeded(RetainerMarketListingTarget listing) =>
        new(
            RetainerMarketListingPostOutcome.Committed,
            listing,
            "RetainerMarketListingPosted",
            "The exact source decrement and live market listing were observed.");

    public static RetainerMarketListingPostResult Failed(string code, string message) =>
        new(RetainerMarketListingPostOutcome.FailedBeforeSend, null, code, message);

    public static RetainerMarketListingPostResult Indeterminate(
        RetainerMarketListingTarget? listing,
        string code,
        string message) =>
        new(RetainerMarketListingPostOutcome.Indeterminate, listing, code, message);
}

public sealed class RetainerMarketMutationIndeterminateException : OperationCanceledException
{
    public RetainerMarketMutationIndeterminateException(
        string code,
        string message,
        RetainerMarketListingTarget? listing,
        OperationCanceledException innerException,
        CancellationToken cancellationToken)
        : base(message, innerException, cancellationToken)
    {
        Code = code;
        Listing = listing;
    }

    public string Code { get; }
    public RetainerMarketListingTarget? Listing { get; }
}
public sealed record RetainerSellingUiObservation(
    bool MarketListReady,
    bool SellListReady,
    bool ListingEditorReady,
    bool ConfirmationReady,
    bool InventoryReady,
    bool CommandMenuReady,
    bool RetainerListReady);

/// <summary>
/// Complete game-facing retainer interaction lifecycle. Product planning, authorization,
/// persistence, and retry policy belong to the consuming plugin.
/// </summary>
public interface IRetainerAutomationSession
{
    bool IsRetainerListReady { get; }
    Task<RetainerAutomationResult> EnsureRetainerListAsync(CancellationToken cancellationToken = default);
    Task<RetainerAutomationRosterResult> ScanAvailableRetainersAsync(CancellationToken cancellationToken = default);
    Task<RetainerAutomationOpenResult> OpenFirstAvailableRetainerAsync(CancellationToken cancellationToken = default);
    Task<RetainerAutomationResult> OpenRetainerAsync(RetainerAutomationTarget target, CancellationToken cancellationToken = default);
    Task<RetainerAutomationResult> WaitForCurrentRetainerMenuAsync(CancellationToken cancellationToken = default);
    Task<RetainerAutomationResult> OpenInventoryAsync(CancellationToken cancellationToken = default);
    Task<RetainerAutomationResult> OpenSellingListAsync(CancellationToken cancellationToken = default);
    Task<RetainerAutomationResult> OpenSellingListingAsync(
        RetainerMarketListingTarget listing,
        CancellationToken cancellationToken = default);
    Task<RetainerMarketListingScanResult> ScanMarketListingsAsync(CancellationToken cancellationToken = default);
    Task<RetainerAutomationResult> UpdateSellingListingPriceAsync(
        RetainerMarketListingTarget listing,
        uint newUnitPrice,
        CancellationToken cancellationToken = default);
    Task<RetainerMarketListingPostResult> PostMarketListingAsync(
        DalamudInventoryStack source,
        int quantity,
        uint unitPrice,
        CancellationToken cancellationToken = default);
    Task<RetainerSellingUiObservation> ObserveSellingUiAsync(CancellationToken cancellationToken = default);
    Task<RetainerAutomationResult> ReturnToRetainerListAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken = default);
    Task<RetainerRetrievalResult> RetrieveAsync(
        DalamudInventoryStack stack,
        int quantity,
        CancellationToken cancellationToken = default);
    /// <summary>
    /// Retrieves from one source stack using a variant total from the caller's
    /// existing retainer scan. The default keeps older session implementations
    /// compatible; live implementations use the total to avoid a redundant scan.
    /// </summary>
    Task<RetainerRetrievalResult> RetrieveAsync(
        DalamudInventoryStack stack,
        int quantity,
        int retainerVariantQuantityBefore,
        CancellationToken cancellationToken = default) =>
        RetrieveAsync(stack, quantity, cancellationToken);
    Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerInventoryAsync(IReadOnlySet<uint>? itemIds = null, CancellationToken cancellationToken = default);
    Task<RetainerDepositResult> DepositAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken = default);
    Task<RetainerCrystalTransferResult> DepositCrystalAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken = default);
    Task<RetainerAutomationResult> CloseInventoryAsync(CancellationToken cancellationToken = default);
    Task<RetainerAutomationResult> CloseRetainerAsync(CancellationToken cancellationToken = default);
    Task<RetainerAutomationResult> CloseRetainerListAsync(CancellationToken cancellationToken = default);
    void CancelActive();
}

public static class RetainerDepositObservation
{
    public static bool Matches(
        int expected,
        int playerQuantityBefore,
        int playerQuantityAfter,
        int retainerQuantityBefore,
        int retainerQuantityAfter) =>
        expected > 0 &&
        playerQuantityBefore - playerQuantityAfter == expected &&
        retainerQuantityAfter - retainerQuantityBefore == expected;
}

public static class RetainerRetrievalObservation
{
    /// <summary>
    /// Proves the common retrieval path from the exact source slot and the player's
    /// aggregate inventory. This is deliberately cheap enough to poll while the game
    /// is applying the command.
    /// </summary>
    public static bool Matches(
        uint itemId,
        int originalQuantity,
        int transferred,
        uint observedSlotItemId,
        int observedSlotQuantity,
        int playerQuantityBefore,
        int playerQuantityAfter)
    {
        if (transferred <= 0 || transferred > originalQuantity)
            return false;

        var remaining = originalQuantity - transferred;
        var slotMatches = remaining == 0
            ? observedSlotItemId != itemId || observedSlotQuantity == 0
            : observedSlotItemId == itemId && observedSlotQuantity == remaining;

        return slotMatches && playerQuantityAfter - playerQuantityBefore == transferred;
    }

    /// <summary>
    /// Proves a retrieval after the game has reordered or repopulated the original
    /// source slot. Both inventories must report the same exact movement, so an
    /// unrelated player-inventory change cannot be mistaken for success.
    /// </summary>
    public static bool MatchesAggregate(
        int transferred,
        int retainerQuantityBefore,
        int retainerQuantityAfter,
        int playerQuantityBefore,
        int playerQuantityAfter) =>
        transferred > 0 &&
        retainerQuantityBefore - retainerQuantityAfter == transferred &&
        playerQuantityAfter - playerQuantityBefore == transferred;
}

public static class RetainerMarketListingObservation
{
    public static bool Matches(
        RetainerMarketListingTarget expected,
        uint observedItemId,
        int observedQuantity,
        bool observedIsHq,
        ulong observedUnitPrice) =>
        expected.ItemId == observedItemId &&
        expected.Quantity == observedQuantity &&
        expected.IsHq == observedIsHq &&
        expected.UnitPrice is > 0 &&
        expected.UnitPrice == observedUnitPrice;
}

internal static class RetainerMarketPriceCommitObservation
{
    public static bool Matches(
        RetainerMarketListingTarget expected,
        int observedSlotIndex,
        uint observedItemId,
        int observedQuantity,
        bool observedIsHq,
        ulong observedUnitPrice,
        bool listingEditorReady) =>
        !listingEditorReady &&
        expected.SlotIndex == observedSlotIndex &&
        RetainerMarketListingObservation.Matches(
            expected,
            observedItemId,
            observedQuantity,
            observedIsHq,
            observedUnitPrice);
}

public static class RetainerMarketPricePolicy
{
    public const uint MaximumUnitPrice = 999_999_999;

    public static bool IsValidMutation(uint? observedUnitPrice, uint requestedUnitPrice) =>
        requestedUnitPrice is > 0 and <= MaximumUnitPrice &&
        observedUnitPrice is > 0 &&
        observedUnitPrice != requestedUnitPrice;
}

public enum RetainerMarketPriceUpdateAction
{
    Wait,
    RejectUnexpectedConfirmation,
    Complete,
}

public static class RetainerMarketPriceUpdatePolicy
{
    public static RetainerMarketPriceUpdateAction Decide(
        bool committed,
        bool confirmationReady)
    {
        if (confirmationReady)
            return RetainerMarketPriceUpdateAction.RejectUnexpectedConfirmation;
        if (committed)
            return RetainerMarketPriceUpdateAction.Complete;
        return RetainerMarketPriceUpdateAction.Wait;
    }
}
