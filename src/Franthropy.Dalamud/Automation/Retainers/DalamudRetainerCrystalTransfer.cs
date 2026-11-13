using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Diagnostics;

using Franthropy.Dalamud.Automation.Ui;

namespace Franthropy.Dalamud.Automation.Retainers;

public sealed record RetainerCrystalTransferResult(
    bool Success,
    int Transferred,
    string Code,
    string Message);

public static class RetainerCrystalTransferObservation
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

/// <summary>
/// Deposits an exact shard, crystal, or cluster stack into the currently open retainer.
/// The caller owns retainer selection and higher-level routing policy.
/// </summary>
public sealed class DalamudRetainerCrystalTransfer
{
    private const string InputNumericAddon = "InputNumeric";

    private readonly ISigScanner sigScanner;
    private readonly IGameGui gameGui;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly IGameInventory? gameInventory;
    private RetainerItemCommandDelegate? retainerItemCommand;

    public DalamudRetainerCrystalTransfer(
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

    public async Task<RetainerCrystalTransferResult> DepositAsync(
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

    private unsafe PendingRetainerCrystalTransfer BeginDeposit(
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
            return PendingRetainerCrystalTransfer.Fail(NativeCapabilityGuard.FailureCode, exception.Message);
        }

        if (stack.Container != InventoryType.Crystals ||
            requestedQuantity <= 0 ||
            !ElementalCurrencyCatalog.IsElementalCurrency(stack.ItemId))
        {
            return PendingRetainerCrystalTransfer.Fail(
                "InvalidRequest",
                $"Invalid crystal deposit request for item {stack.ItemId}: {requestedQuantity} from {stack.Container}.");
        }

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return PendingRetainerCrystalTransfer.Fail("InventoryUnavailable", "Inventory manager is unavailable.");

        var playerContainer = inventoryManager->GetInventoryContainer(InventoryType.Crystals);
        if (playerContainer == null || !playerContainer->IsLoaded)
            return PendingRetainerCrystalTransfer.Fail("PlayerCrystalsUnavailable", "Player crystal inventory is unavailable.");

        var slot = playerContainer->GetInventorySlot(stack.SlotIndex);
        if (slot == null || slot->ItemId != stack.ItemId || slot->Quantity != stack.Quantity)
        {
            return PendingRetainerCrystalTransfer.Fail(
                "SourceSlotChanged",
                $"Expected {stack.Quantity}x item {stack.ItemId} was not found in crystal slot {stack.SlotIndex}.");
        }

        var retainerContainer = inventoryManager->GetInventoryContainer(InventoryType.RetainerCrystals);
        if (retainerContainer == null || !retainerContainer->IsLoaded)
            return PendingRetainerCrystalTransfer.Fail("RetainerCrystalsUnavailable", "Retainer crystal inventory is unavailable.");

        var retainerBefore = DalamudInventoryStackScanner.CountLoadedItem(InventoryType.RetainerCrystals, stack.ItemId);
        var capacity = Math.Max(0, ElementalCurrencyCatalog.PerItemCapacity - retainerBefore);
        var transfer = Math.Min(requestedQuantity, Math.Min(stack.Quantity, capacity));
        if (transfer <= 0)
        {
            return new(
                true,
                0,
                stack.Quantity,
                retainerBefore,
                "NoCapacity",
                $"Retainer crystal storage is full for item {stack.ItemId}.");
        }

        var retainerAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
        if (retainerAgent == null || !retainerAgent->IsAgentActive())
            return PendingRetainerCrystalTransfer.Fail("RetainerAgentUnavailable", "Retainer agent is unavailable.");

        if (!RetainerDepositVerifier.PreconditionsAvailable(stack))
            return PendingRetainerCrystalTransfer.Fail("InventoryUnavailable", "All verification containers must be loaded before dispatch.");
        if (RetainerDepositVerifier.QuantityPopupVisible(gameGui))
            return PendingRetainerCrystalTransfer.Fail("QuantityPopupAlreadyOpen", "An unowned quantity popup exists before dispatch.");

        try
        {
            retainerItemCommand(
                RetainerNativeCommandCapability.Context(retainerAgent),
                (uint)stack.SlotIndex,
                InventoryType.Crystals,
                0,
                RetainerItemCommand.EntrustToRetainer);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Unable to invoke the retainer crystal deposit command.");
            return PendingRetainerCrystalTransfer.Fail(
                "DepositDispatchIndeterminate",
                $"Dispatch began; do not retry. Retainer crystal deposit command is unavailable. {ex.Message}");
        }

        return new(
            true,
            transfer,
            stack.Quantity,
            retainerBefore,
            "CommandSubmitted",
            $"Opened deposit quantity for item {stack.ItemId}.");
    }

    private unsafe RetainerCrystalTransferResult SubmitQuantity(uint itemId, int requested)
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

    private sealed record PendingRetainerCrystalTransfer(
        bool Success,
        int Requested,
        int PlayerQuantityBefore,
        int RetainerQuantityBefore,
        string Code,
        string Message)
    {
        public static PendingRetainerCrystalTransfer Fail(string code, string message) =>
            new(false, 0, 0, 0, code, message);
    }
}
