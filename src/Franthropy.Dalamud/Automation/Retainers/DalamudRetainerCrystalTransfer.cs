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
            () => BeginDeposit(stack, requestedQuantity),
            requested => SubmitQuantity(stack.ItemId, requested) is { } submitted
                ? new RetainerDepositResult(submitted.Success, submitted.Transferred, submitted.Code, submitted.Message)
                : throw new InvalidOperationException(),
            cancellationToken).ConfigureAwait(false);
        return new(result.Success, result.Transferred, result.Code, result.Message);
    }

    private unsafe RetainerDepositStart BeginDeposit(
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
            return RetainerDepositStart.Fail(NativeCapabilityGuard.FailureCode, exception.Message);
        }

        if (stack.Container != InventoryType.Crystals ||
            requestedQuantity <= 0 ||
            !ElementalCurrencyCatalog.IsElementalCurrency(stack.ItemId))
        {
            return RetainerDepositStart.Fail(
                "InvalidRequest",
                $"Invalid crystal deposit request for item {stack.ItemId}: {requestedQuantity} from {stack.Container}.");
        }

        if (DalamudRetainerInventory.CommandBlocker(gameGui, crystals: true, out var retainerAgent) is { } blocker)
            return RetainerDepositStart.Fail(blocker.Code, blocker.Message);

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return RetainerDepositStart.Fail("InventoryUnavailable", "Inventory manager is unavailable.");

        var playerContainer = inventoryManager->GetInventoryContainer(InventoryType.Crystals);
        if (playerContainer == null || !playerContainer->IsLoaded)
            return RetainerDepositStart.Fail("PlayerCrystalsUnavailable", "Player crystal inventory is unavailable.");

        var slot = playerContainer->GetInventorySlot(stack.SlotIndex);
        if (slot == null || slot->ItemId != stack.ItemId || slot->Quantity != stack.Quantity)
        {
            return RetainerDepositStart.Fail(
                "SourceSlotChanged",
                $"Expected {stack.Quantity}x item {stack.ItemId} was not found in crystal slot {stack.SlotIndex}.");
        }

        var retainerContainer = inventoryManager->GetInventoryContainer(InventoryType.RetainerCrystals);
        if (retainerContainer == null || !retainerContainer->IsLoaded)
            return RetainerDepositStart.Fail("RetainerCrystalsUnavailable", "Retainer crystal inventory is unavailable.");

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
                false,
                "NoCapacity",
                $"Retainer crystal storage is full for item {stack.ItemId}.");
        }

        var selection = RetainerInventoryCommandPolicy.Select(
            RetainerTransferDirection.Entrust, isCrystalContainer: true, stack.Quantity, transfer);
        try
        {
            retainerItemCommand(
                RetainerNativeCommandCapability.Context(retainerAgent),
                (uint)stack.SlotIndex,
                InventoryType.Crystals,
                0,
                selection.Command);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Unable to invoke the retainer crystal deposit command.");
            return RetainerDepositStart.Fail(
                "DepositDispatchIndeterminate",
                $"Dispatch began; do not retry. Retainer crystal deposit command is unavailable. {ex.Message}");
        }

        return new(
            true,
            transfer,
            stack.Quantity,
            retainerBefore,
            selection.NeedsQuantityInput,
            "CommandSubmitted",
            $"Dispatched {selection.Command} for item {stack.ItemId}.");
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
        RetainerInventoryCommand Command);
}
