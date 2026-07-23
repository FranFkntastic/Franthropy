using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Inventory;

namespace Franthropy.Dalamud.Automation.Retainers;

/// <summary>
/// Deposits an exact ordinary player-inventory stack into the currently open retainer.
/// The caller owns retainer selection and higher-level routing policy.
/// </summary>
public sealed class DalamudRetainerItemTransfer
{
    private const string InputNumericAddon = "InputNumeric";
    private const string RetainerItemCommandSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 48 8B 5C 24 ?? 41 8B F0";
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
    private RetainerItemCommandDelegate? retainerItemCommand;

    public DalamudRetainerItemTransfer(
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

    public async Task<RetainerDepositResult> DepositAsync(
        DalamudInventoryStack stack,
        int requestedQuantity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pending = await framework.RunOnTick(
            () => BeginDeposit(stack, requestedQuantity),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!pending.Success || pending.Requested == 0)
            return new(pending.Success, 0, pending.Code, pending.Message);

        for (var attempt = 0; attempt < 30; attempt++)
        {
            var immediate = await framework.RunOnTick(
                () => VerifyCompleted(stack, pending),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (immediate.Success)
                return immediate;

            var submitted = await framework.RunOnTick(
                () => SubmitQuantity(stack.ItemId, pending.Requested),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (submitted.Success)
            {
                return await WaitForCompletionAsync(
                    stack,
                    pending with { Requested = submitted.Transferred },
                    cancellationToken).ConfigureAwait(false);
            }

            await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }

        return new(false, 0, "DepositNotObserved", $"Deposit neither completed nor opened a numeric quantity popup for item {stack.ItemId}.");
    }

    private async Task<RetainerDepositResult> WaitForCompletionAsync(
        DalamudInventoryStack stack,
        PendingRetainerItemTransfer pending,
        CancellationToken cancellationToken)
    {
        RetainerDepositResult last = new(false, 0, "TransferPending", $"Deposit did not complete for item {stack.ItemId}.");
        for (var attempt = 0; attempt < 60; attempt++)
        {
            last = await framework.RunOnTick(
                () => VerifyCompleted(stack, pending),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (last.Success)
                return last;

            await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }

        return last;
    }

    private unsafe PendingRetainerItemTransfer BeginDeposit(
        DalamudInventoryStack stack,
        int requestedQuantity)
    {
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

        try
        {
            retainerItemCommand ??= Marshal.GetDelegateForFunctionPointer<RetainerItemCommandDelegate>(
                sigScanner.ScanText(RetainerItemCommandSignature));
            retainerItemCommand(
                (nint)retainerAgent + 40,
                (uint)stack.SlotIndex,
                stack.Container,
                0,
                RetainerItemCommand.EntrustToRetainer);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Unable to invoke the retainer item deposit command.");
            return PendingRetainerItemTransfer.Fail(
                "CommandUnavailable",
                $"Retainer item deposit command is unavailable. {ex.Message}");
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
        if (numeric == null || !numeric->IsReady || !numeric->IsVisible || numeric->AtkValuesCount <= 3)
            return new(false, 0, "QuantityInputPending", $"Numeric quantity popup did not open for item {itemId}.");

        var maximum = checked((int)numeric->AtkValues[3].UInt);
        if (maximum <= 0)
            return new(false, 0, "NoCapacity", $"Retainer reported no deposit capacity for item {itemId}.");

        var submitted = Math.Clamp(requested, 1, maximum);
        numeric->FireCallbackInt(submitted);
        return new(true, submitted, "QuantitySubmitted", $"Submitted {submitted}x item {itemId} for deposit.");
    }

    private static RetainerDepositResult VerifyCompleted(
        DalamudInventoryStack stack,
        PendingRetainerItemTransfer pending)
    {
        var playerAfter = CountPlayer(stack.ItemId, stack.IsHighQuality);
        var retainerAfter = CountRetainer(stack.ItemId, stack.IsHighQuality);
        if (RetainerDepositObservation.Matches(
                pending.Requested,
                pending.PlayerQuantityBefore,
                playerAfter,
                pending.RetainerQuantityBefore,
                retainerAfter))
        {
            return new(
                true,
                pending.Requested,
                "TransferVerified",
                $"Deposited {pending.Requested}x item {stack.ItemId}; player {pending.PlayerQuantityBefore}->{playerAfter}, retainer {pending.RetainerQuantityBefore}->{retainerAfter}.");
        }

        return new(
            false,
            0,
            "TransferPending",
            $"Deposit verification pending for item {stack.ItemId}: player {pending.PlayerQuantityBefore}->{playerAfter}, retainer {pending.RetainerQuantityBefore}->{retainerAfter}, expected {pending.Requested}.");
    }

    private static int CountPlayer(uint itemId, bool isHighQuality) => PlayerItemContainers.Sum(
        type => DalamudInventoryStackScanner.CountLoadedItem(type, itemId, isHighQuality));

    private static int CountRetainer(uint itemId, bool isHighQuality) => DalamudRetainerInventory.OrdinaryItemContainers.Sum(
        type => DalamudInventoryStackScanner.CountLoadedItem(type, itemId, isHighQuality));

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
