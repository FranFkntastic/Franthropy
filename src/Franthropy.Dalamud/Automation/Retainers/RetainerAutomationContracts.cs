using Franthropy.Dalamud.Automation.Inventory;

namespace Franthropy.Dalamud.Automation.Retainers;

public sealed record RetainerAutomationTarget(ulong RetainerId, string RetainerName);

public sealed record RetainerAutomationResult(bool Success, string Code, string Message)
{
    public static RetainerAutomationResult Succeeded(string code, string message) => new(true, code, message);
    public static RetainerAutomationResult Failed(string code, string message) => new(false, code, message);
}

public sealed record RetainerRetrievalResult(bool Success, int Transferred, string Code, string Message);

/// <summary>
/// Complete game-facing retainer interaction lifecycle. Product planning, authorization,
/// persistence, and retry policy belong to the consuming plugin.
/// </summary>
public interface IRetainerAutomationSession
{
    bool IsRetainerListReady { get; }
    Task<RetainerAutomationResult> EnsureRetainerListAsync(CancellationToken cancellationToken = default);
    Task<RetainerAutomationResult> OpenRetainerAsync(RetainerAutomationTarget target, CancellationToken cancellationToken = default);
    Task<RetainerAutomationResult> WaitForCurrentRetainerMenuAsync(CancellationToken cancellationToken = default);
    Task<RetainerAutomationResult> OpenInventoryAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken = default);
    Task<RetainerRetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken = default);
    Task<RetainerCrystalTransferResult> DepositCrystalAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken = default);
    Task<RetainerAutomationResult> CloseInventoryAsync(CancellationToken cancellationToken = default);
    Task<RetainerAutomationResult> CloseRetainerAsync(CancellationToken cancellationToken = default);
    void CancelActive();
}

public static class RetainerRetrievalObservation
{
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
}
