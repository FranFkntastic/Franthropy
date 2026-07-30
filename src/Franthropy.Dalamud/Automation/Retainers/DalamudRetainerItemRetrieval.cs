using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Diagnostics;

namespace Franthropy.Dalamud.Automation.Retainers;

internal enum RetainerRetrievalCommand : long
{
    RetrieveFromRetainer = 0,
    RetrieveQuantity = 3,
}

internal sealed record RetainerRetrievalCommandSelection(
    RetainerRetrievalCommand Command,
    bool NeedsQuantityInput);

internal static class RetainerRetrievalCommandPolicy
{
    public static RetainerRetrievalCommandSelection Select(
        bool isCrystalContainer,
        int sourceQuantity,
        int requestedQuantity)
    {
        var transfer = Math.Min(sourceQuantity, requestedQuantity);
        if (isCrystalContainer)
            return new(RetainerRetrievalCommand.RetrieveFromRetainer, true);

        return transfer < sourceQuantity
            ? new(RetainerRetrievalCommand.RetrieveQuantity, true)
            : new(RetainerRetrievalCommand.RetrieveFromRetainer, false);
    }
}

/// <summary>
/// Retrieves an exact quantity from the currently open retainer using the
/// game's typed retainer command, then verifies both inventory deltas.
/// </summary>
public sealed class DalamudRetainerItemRetrieval
{
    private const string InputNumericAddon = "InputNumeric";
    private const string RetainerItemCommandSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 48 8B 5C 24 ?? 41 8B F0";
    private const string ApprovedGameVersion = "2026.07.16.0001.0000";
    private const string PatchContractId = "franthropy.retainer-item-command";
    private static readonly IReadOnlyList<InventoryType> PlayerOrdinaryItemContainers =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private readonly ISigScanner sigScanner;
    private readonly IGameGui gameGui;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private RetainerItemCommandDelegate? retainerItemCommand;

    public DalamudRetainerItemRetrieval(
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

    public async Task<RetainerRetrievalResult> RetrieveAsync(
        DalamudInventoryStack stack,
        int requestedQuantity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pending = await framework.RunOnTick(
            () => BeginRetrieval(stack, requestedQuantity),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!pending.Success)
            return new(false, 0, pending.Code, pending.Message);

        for (var attempt = 0; attempt < 30; attempt++)
        {
            var immediate = await framework.RunOnTick(
                () => VerifyCompleted(stack, pending),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (immediate.Success)
                return immediate;

            if (pending.NeedsQuantityInput)
            {
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
            }

            await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }

        return new(false, 0, "RetrievalNotObserved", $"Retrieval did not complete for item {stack.ItemId}.");
    }

    private async Task<RetainerRetrievalResult> WaitForCompletionAsync(
        DalamudInventoryStack stack,
        PendingRetainerRetrieval pending,
        CancellationToken cancellationToken)
    {
        RetainerRetrievalResult last = new(false, 0, "TransferPending", $"Retrieval did not complete for item {stack.ItemId}.");
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

    private unsafe PendingRetainerRetrieval BeginRetrieval(
        DalamudInventoryStack stack,
        int requestedQuantity)
    {
        var compatibility = GamePatchCompatibilityGate.Evaluate(PatchContractId, ApprovedGameVersion);
        if (!compatibility.IsApproved)
            return PendingRetainerRetrieval.Fail(GamePatchCompatibility.FailureCode, compatibility.Message);

        var supported = stack.Container == InventoryType.RetainerCrystals ||
                        DalamudRetainerInventory.OrdinaryItemContainers.Contains(stack.Container);
        if (!supported || requestedQuantity <= 0)
        {
            return PendingRetainerRetrieval.Fail(
                "InvalidRequest",
                $"Invalid retrieval request for item {stack.ItemId}: {requestedQuantity} from {stack.Container}.");
        }

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return PendingRetainerRetrieval.Fail("InventoryUnavailable", "Inventory manager is unavailable.");

        var container = inventoryManager->GetInventoryContainer(stack.Container);
        if (container == null || !container->IsLoaded)
            return PendingRetainerRetrieval.Fail("RetainerInventoryUnavailable", "Retainer source inventory is unavailable.");

        var slot = container->GetInventorySlot(stack.SlotIndex);
        var slotIsHighQuality = slot != null && slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
        if (slot == null ||
            slot->ItemId != stack.ItemId ||
            slot->Quantity != stack.Quantity ||
            slotIsHighQuality != stack.IsHighQuality)
        {
            return PendingRetainerRetrieval.Fail(
                "SourceSlotChanged",
                $"Expected {stack.Quantity}x item {stack.ItemId} was not found in {stack.Container} slot {stack.SlotIndex}.");
        }

        var transfer = Math.Min(requestedQuantity, stack.Quantity);
        var selection = RetainerRetrievalCommandPolicy.Select(
            stack.Container == InventoryType.RetainerCrystals,
            stack.Quantity,
            transfer);
        var playerBefore = CountPlayer(stack);
        var retainerAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
        if (retainerAgent == null || !retainerAgent->IsAgentActive())
            return PendingRetainerRetrieval.Fail("RetainerAgentUnavailable", "Retainer agent is unavailable.");

        try
        {
            retainerItemCommand ??= Marshal.GetDelegateForFunctionPointer<RetainerItemCommandDelegate>(
                sigScanner.ScanText(RetainerItemCommandSignature));
            retainerItemCommand(
                (nint)retainerAgent + 40,
                (uint)stack.SlotIndex,
                stack.Container,
                0,
                selection.Command);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Unable to invoke the retainer retrieval command.");
            return PendingRetainerRetrieval.Fail(
                "CommandUnavailable",
                $"Retainer retrieval command is unavailable. {ex.Message}");
        }

        return new(
            true,
            transfer,
            playerBefore,
            selection.NeedsQuantityInput,
            "CommandSubmitted",
            $"Submitted retrieval command for item {stack.ItemId}.");
    }

    private unsafe RetainerRetrievalResult SubmitQuantity(uint itemId, int requested)
    {
        var numeric = gameGui.GetAddonByName<AtkUnitBase>(InputNumericAddon, 1);
        if (numeric == null || !numeric->IsReady || !numeric->IsVisible || numeric->AtkValuesCount <= 3)
            return new(false, 0, "QuantityInputPending", $"Numeric quantity popup did not open for item {itemId}.");

        var maximum = checked((int)numeric->AtkValues[3].UInt);
        if (maximum <= 0)
            return new(false, 0, "QuantityUnavailable", $"Retainer reported no retrievable quantity for item {itemId}.");

        var submitted = Math.Clamp(requested, 1, maximum);
        numeric->FireCallbackInt(submitted);
        return new(true, submitted, "QuantitySubmitted", $"Submitted {submitted}x item {itemId} for retrieval.");
    }

    private static unsafe RetainerRetrievalResult VerifyCompleted(
        DalamudInventoryStack original,
        PendingRetainerRetrieval pending)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return new(false, 0, "ContainerUnavailable", "Inventory manager became unavailable.");

        var container = inventoryManager->GetInventoryContainer(original.Container);
        if (container == null || !container->IsLoaded)
            return new(false, 0, "ContainerUnavailable", "Retainer source container became unavailable.");

        var slot = container->GetInventorySlot(original.SlotIndex);
        if (slot == null)
            return new(false, 0, "SlotUnavailable", "Retainer source slot became unavailable.");

        var playerAfter = CountPlayer(original);
        if (RetainerRetrievalObservation.Matches(
                original.ItemId,
                original.Quantity,
                pending.Requested,
                slot->ItemId,
                slot->Quantity,
                pending.PlayerQuantityBefore,
                playerAfter))
        {
            return new(
                true,
                pending.Requested,
                "TransferVerified",
                $"Retrieved {pending.Requested}x item {original.ItemId}; player {pending.PlayerQuantityBefore}->{playerAfter}.");
        }

        return new(false, 0, "TransferPending", "Waiting for matching retainer-slot and player-inventory deltas.");
    }

    private static int CountPlayer(DalamudInventoryStack stack) =>
        stack.Container == InventoryType.RetainerCrystals
            ? DalamudInventoryStackScanner.CountLoadedItem(InventoryType.Crystals, stack.ItemId)
            : PlayerOrdinaryItemContainers.Sum(
                type => DalamudInventoryStackScanner.CountLoadedItem(type, stack.ItemId, stack.IsHighQuality));

    private delegate void RetainerItemCommandDelegate(
        nint AgentRetainerItemCommandModule,
        uint Slot,
        InventoryType InventoryType,
        uint A4,
        RetainerRetrievalCommand Command);

    private sealed record PendingRetainerRetrieval(
        bool Success,
        int Requested,
        int PlayerQuantityBefore,
        bool NeedsQuantityInput,
        string Code,
        string Message)
    {
        public static PendingRetainerRetrieval Fail(string code, string message) =>
            new(false, 0, 0, false, code, message);
    }
}
