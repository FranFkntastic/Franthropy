using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Inventory;

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
    private const string RetainerItemCommandSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 48 8B 5C 24 ?? 41 8B F0";

    private readonly ISigScanner sigScanner;
    private readonly IGameGui gameGui;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private RetainerItemCommandDelegate? retainerItemCommand;

    public DalamudRetainerCrystalTransfer(
        ISigScanner sigScanner,
        IGameGui gameGui,
        IFramework framework,
        IPluginLog log)
    {
        this.sigScanner = sigScanner;
        this.gameGui = gameGui;
        this.framework = framework;
        this.log = log;
    }

    public async Task<RetainerCrystalTransferResult> DepositAsync(
        DalamudInventoryStack stack,
        int requestedQuantity)
    {
        var pending = await framework.RunOnTick(() => BeginDeposit(stack, requestedQuantity)).ConfigureAwait(false);
        if (!pending.Success || pending.Requested == 0)
            return new(pending.Success, 0, pending.Code, pending.Message);

        for (var attempt = 0; attempt < 30; attempt++)
        {
            var immediate = await framework.RunOnTick(() => VerifyCompleted(
                stack.ItemId,
                pending.Requested,
                pending.PlayerQuantityBefore,
                pending.RetainerQuantityBefore)).ConfigureAwait(false);
            if (immediate.Success)
                return immediate;

            var submitted = await framework.RunOnTick(() => SubmitQuantity(stack.ItemId, pending.Requested)).ConfigureAwait(false);
            if (submitted.Success)
            {
                return await WaitForCompletionAsync(
                    stack.ItemId,
                    submitted.Transferred,
                    pending.PlayerQuantityBefore,
                    pending.RetainerQuantityBefore).ConfigureAwait(false);
            }

            await framework.DelayTicks(1).ConfigureAwait(false);
        }

        return new(false, 0, "DepositNotObserved", $"Deposit neither completed nor opened a numeric quantity popup for item {stack.ItemId}.");
    }

    private async Task<RetainerCrystalTransferResult> WaitForCompletionAsync(
        uint itemId,
        int transferred,
        int playerQuantityBefore,
        int retainerQuantityBefore)
    {
        RetainerCrystalTransferResult last = new(false, 0, "TransferPending", $"Deposit did not complete for item {itemId}.");
        for (var attempt = 0; attempt < 60; attempt++)
        {
            last = await framework.RunOnTick(() => VerifyCompleted(
                itemId,
                transferred,
                playerQuantityBefore,
                retainerQuantityBefore)).ConfigureAwait(false);
            if (last.Success)
                return last;

            await framework.DelayTicks(1).ConfigureAwait(false);
        }

        return last;
    }

    private unsafe PendingRetainerCrystalTransfer BeginDeposit(
        DalamudInventoryStack stack,
        int requestedQuantity)
    {
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

        try
        {
            retainerItemCommand ??= Marshal.GetDelegateForFunctionPointer<RetainerItemCommandDelegate>(
                sigScanner.ScanText(RetainerItemCommandSignature));
            retainerItemCommand(
                (nint)retainerAgent + 40,
                (uint)stack.SlotIndex,
                InventoryType.Crystals,
                0,
                RetainerItemCommand.EntrustToRetainer);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Unable to invoke the retainer crystal deposit command.");
            return PendingRetainerCrystalTransfer.Fail(
                "CommandUnavailable",
                $"Retainer crystal deposit command is unavailable. {ex.Message}");
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
        if (numeric == null || !numeric->IsReady || !numeric->IsVisible || numeric->AtkValuesCount <= 3)
            return new(false, 0, "QuantityInputPending", $"Numeric quantity popup did not open for item {itemId}.");

        var maximum = checked((int)numeric->AtkValues[3].UInt);
        if (maximum <= 0)
            return new(false, 0, "NoCapacity", $"Retainer reported no deposit capacity for item {itemId}.");

        var submitted = Math.Clamp(requested, 1, maximum);
        numeric->FireCallbackInt(submitted);
        return new(true, submitted, "QuantitySubmitted", $"Submitted {submitted}x item {itemId} for deposit.");
    }

    private static RetainerCrystalTransferResult VerifyCompleted(
        uint itemId,
        int expected,
        int playerQuantityBefore,
        int retainerQuantityBefore)
    {
        var playerAfter = DalamudInventoryStackScanner.CountLoadedItem(InventoryType.Crystals, itemId);
        var retainerAfter = DalamudInventoryStackScanner.CountLoadedItem(InventoryType.RetainerCrystals, itemId);
        if (RetainerCrystalTransferObservation.Matches(
                expected,
                playerQuantityBefore,
                playerAfter,
                retainerQuantityBefore,
                retainerAfter))
        {
            return new(
                true,
                expected,
                "TransferVerified",
                $"Deposited {expected}x item {itemId}; player {playerQuantityBefore}->{playerAfter}, retainer {retainerQuantityBefore}->{retainerAfter}.");
        }

        return new(
            false,
            0,
            "TransferPending",
            $"Deposit verification pending for item {itemId}: player {playerQuantityBefore}->{playerAfter}, retainer {retainerQuantityBefore}->{retainerAfter}, expected {expected}.");
    }

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
