using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Diagnostics;

using Franthropy.Dalamud.Automation.Ui;

namespace Franthropy.Dalamud.Automation.Retainers;

/// <summary>
/// Deposits an exact ordinary player-inventory stack into the currently open retainer.
/// The caller owns retainer selection and higher-level routing policy.
/// </summary>
public sealed class DalamudRetainerItemTransfer
{
    private const string InputNumericAddon = "InputNumeric";
    private static readonly IReadOnlySet<InventoryType> PlayerItemContainers = new HashSet<InventoryType>
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    };

    private readonly ISigScanner sigScanner;
    private readonly IGameGui gameGui;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly IGameInventory? gameInventory;
    private RetainerItemCommandDelegate? retainerItemCommand;

    public DalamudRetainerItemTransfer(
        ISigScanner sigScanner,
        IGameGui gameGui,
        IFramework framework,
        IPluginLog log,
        IGameInventory? gameInventory = null)
    {
        this.sigScanner = sigScanner;
        this.gameGui = gameGui;
        this.framework = framework;
        this.log = log;
        this.gameInventory = gameInventory;
    }

    public async Task<RetainerDepositResult> DepositAsync(
        DalamudInventoryStack stack,
        int requestedQuantity,
        CancellationToken cancellationToken = default)
    {
        var result = await RetainerDepositVerifier.DepositAsync(
            stack, framework, gameGui, log, gameInventory,
            () =>
            {
                var pending = BeginDeposit(stack, requestedQuantity);
                return new RetainerDepositStart(pending.Success, pending.Requested,
                    pending.PlayerQuantityBefore, pending.RetainerQuantityBefore, pending.Code, pending.Message);
            },
            requested => SubmitQuantity(stack.ItemId, requested) is { } submitted
                ? new RetainerDepositResult(submitted.Success, submitted.Transferred, submitted.Code, submitted.Message)
                : throw new InvalidOperationException(),
            cancellationToken).ConfigureAwait(false);
        return new(result.Success, result.Transferred, result.Code, result.Message);
    }

    private unsafe PendingRetainerItemTransfer BeginDeposit(
        DalamudInventoryStack stack,
        int requestedQuantity)
    {
        try
        {
            retainerItemCommand ??= Marshal.GetDelegateForFunctionPointer<RetainerItemCommandDelegate>(
                RetainerNativeCommandCapability.Resolve(sigScanner));
        }
        catch (Exception exception)
        {
            return PendingRetainerItemTransfer.Fail(NativeCapabilityGuard.FailureCode, exception.Message);
        }

        if (!PlayerItemContainers.Contains(stack.Container) || requestedQuantity <= 0)
        {
            return PendingRetainerItemTransfer.Fail(
                "InvalidRequest",
                $"Invalid item deposit request for item {stack.ItemId}: {requestedQuantity} from {stack.Container}.");
        }

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return PendingRetainerItemTransfer.Fail("InventoryUnavailable", "Inventory manager is unavailable.");

        var playerContainer = inventoryManager->GetInventoryContainer(stack.Container);
        if (playerContainer == null || !playerContainer->IsLoaded)
            return PendingRetainerItemTransfer.Fail("PlayerInventoryUnavailable", "Player inventory is unavailable.");

        var slot = playerContainer->GetInventorySlot(stack.SlotIndex);
        var slotIsHighQuality = slot != null && slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
        if (slot == null ||
            slot->ItemId != stack.ItemId ||
            slot->Quantity != stack.Quantity ||
            slotIsHighQuality != stack.IsHighQuality)
        {
            return PendingRetainerItemTransfer.Fail(
                "SourceSlotChanged",
                $"Expected {stack.Quantity}x item {stack.ItemId} was not found in {stack.Container} slot {stack.SlotIndex}.");
        }

        var hasLoadedRetainerContainer = false;
        foreach (var type in DalamudRetainerInventory.OrdinaryItemContainers)
        {
            var retainerContainer = inventoryManager->GetInventoryContainer(type);
            if (retainerContainer != null && retainerContainer->IsLoaded)
            {
                hasLoadedRetainerContainer = true;
                break;
            }
        }

        if (!hasLoadedRetainerContainer)
        {
            return PendingRetainerItemTransfer.Fail("RetainerInventoryUnavailable", "Retainer item inventory is unavailable.");
        }

        var playerBefore = CountPlayer(stack.ItemId, stack.IsHighQuality);
        var retainerBefore = CountRetainer(stack.ItemId, stack.IsHighQuality);
        var transfer = Math.Min(requestedQuantity, stack.Quantity);

        var retainerAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
        if (retainerAgent == null || !retainerAgent->IsAgentActive())
            return PendingRetainerItemTransfer.Fail("RetainerAgentUnavailable", "Retainer agent is unavailable.");

        if (!RetainerDepositVerifier.PreconditionsAvailable(stack))
            return PendingRetainerItemTransfer.Fail("InventoryUnavailable", "All verification containers must be loaded before dispatch.");
        if (RetainerDepositVerifier.QuantityPopupVisible(gameGui))
            return PendingRetainerItemTransfer.Fail("QuantityPopupAlreadyOpen", "An unowned quantity popup exists before dispatch.");

        try
        {
            retainerItemCommand(
                RetainerNativeCommandCapability.Context(retainerAgent),
                (uint)stack.SlotIndex,
                stack.Container,
                0,
                RetainerItemCommand.EntrustToRetainer);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Unable to invoke the retainer item deposit command.");
            return PendingRetainerItemTransfer.Fail(
                "DepositDispatchIndeterminate",
                $"Dispatch began; do not retry. Retainer item deposit command is unavailable. {ex.Message}");
        }

        return new(
            true,
            transfer,
            playerBefore,
            retainerBefore,
            "CommandSubmitted",
            $"Opened deposit quantity for item {stack.ItemId}.");
    }

    private unsafe RetainerDepositResult SubmitQuantity(uint itemId, int requested)
    {
        var numeric = gameGui.GetAddonByName<AtkUnitBase>(InputNumericAddon, 1);
        if (!DalamudNumericDialog.TryGetBounds(numeric, out var minimum, out var maximum))
            return new(false, 0, "QuantityInputPending", $"Numeric quantity popup did not open for item {itemId}.");

        if (maximum <= 0)
            return new(false, 0, "NoCapacity", $"Retainer reported no deposit capacity for item {itemId}.");

        var submitted = Math.Clamp(requested, Math.Max(1, minimum), maximum);
        if (DalamudNumericDialog.TrySubmit(numeric, submitted) != NumericDialogSubmission.Submitted)
            return new(false, 0, "QuantityInputPending", $"Numeric quantity popup is not ready for item {itemId}.");
        return new(true, submitted, "QuantitySubmitted", $"Submitted {submitted}x item {itemId} for deposit.");
    }

    private static int CountPlayer(uint itemId, bool isHighQuality) => PlayerItemContainers.Sum(
        type => DalamudInventoryStackScanner.CountLoadedItem(type, itemId, isHighQuality));

    private static int CountRetainer(uint itemId, bool isHighQuality) => DalamudRetainerInventory.OrdinaryItemContainers.Sum(
        type => DalamudInventoryStackScanner.CountLoadedItem(type, itemId, isHighQuality));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RetainerItemCommandDelegate(
        nint AgentRetainerItemCommandModule,
        uint Slot,
        InventoryType InventoryType,
        uint A4,
        RetainerItemCommand Command);

    private enum RetainerItemCommand : long
    {
        EntrustToRetainer = 1,
    }

    private sealed record PendingRetainerItemTransfer(
        bool Success,
        int Requested,
        int PlayerQuantityBefore,
        int RetainerQuantityBefore,
        string Code,
        string Message)
    {
        public static PendingRetainerItemTransfer Fail(string code, string message) =>
            new(false, 0, 0, 0, code, message);
    }
}
