using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Inventory;
using Lumina.Excel.Sheets;

namespace Franthropy.Dalamud.Automation.Retainers;

/// <summary>
/// Executes one bounded retainer UI session and verifies inventory mutations from live game state.
/// It intentionally contains no product plan, persistence, retry, or authorization policy.
/// </summary>
public sealed class DalamudRetainerAutomationSession : IRetainerAutomationSession
{
    private const string RetainerList = "RetainerList";
    private const string SelectString = "SelectString";
    private const string InventoryLarge = "InventoryRetainerLarge";
    private const string InventorySmall = "InventoryRetainer";
    private static readonly IReadOnlyList<InventoryType> PlayerItemContainers =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.Crystals,
    ];

    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly DalamudSummoningBellInteractor bell;
    private readonly DalamudRetainerCrystalTransfer crystals;
    private RetainerAutomationTarget? active;

    public DalamudRetainerAutomationSession(
        IFramework framework,
        IGameGui gameGui,
        IDataManager dataManager,
        IPluginLog log,
        IObjectTable objects,
        ITargetManager targets,
        ISigScanner sigScanner)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.dataManager = dataManager;
        bell = new(objects, targets, dataManager);
        crystals = new(sigScanner, gameGui, framework, log);
    }

    /// <remarks>Read this property from the Dalamud framework thread.</remarks>
    public bool IsRetainerListReady => IsReady(RetainerList);

    public async Task<RetainerAutomationResult> EnsureRetainerListAsync(CancellationToken cancellationToken = default)
    {
        var state = await framework.RunOnTick(
            () => (List: IsReady(RetainerList), Inventory: IsInventoryReady(), Menu: IsCommandMenuReady()),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (state.Inventory || state.Menu)
            return RetainerAutomationResult.Failed("RetainerInteractionAlreadyOpen", "Close the current retainer interaction before starting another session.");
        if (state.List)
            return RetainerAutomationResult.Succeeded("RetainerListReady", "Retainer list is ready.");

        SummoningBellInteractionResult? interaction = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            interaction = await framework.RunOnTick(bell.TryInteract, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (interaction.State == SummoningBellInteractionState.Unavailable)
                return RetainerAutomationResult.Failed("SummoningBellUnavailable", interaction.Message);
            if (interaction.Submitted)
                break;
            await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }

        if (interaction is not { Submitted: true })
            return RetainerAutomationResult.Failed("SummoningBellInteractionFailed", interaction?.Message ?? "No summoning bell was available.");

        return await WaitUntilAsync(() => IsReady(RetainerList), cancellationToken).ConfigureAwait(false)
            ? RetainerAutomationResult.Succeeded("RetainerListReady", "Retainer list opened.")
            : RetainerAutomationResult.Failed("RetainerListTimeout", "Timed out waiting for the retainer list.");
    }

    public async Task<RetainerAutomationResult> OpenRetainerAsync(RetainerAutomationTarget target, CancellationToken cancellationToken = default)
    {
        active = null;
        if (target.RetainerId == 0 || string.IsNullOrWhiteSpace(target.RetainerName))
            return RetainerAutomationResult.Failed("RetainerIdentityRequired", "A stable retainer ID and name are required.");

        var selected = await framework.RunOnTick(() => SelectRetainer(target.RetainerName), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!selected.Success)
            return selected;
        if (!await WaitUntilAsync(IsCommandMenuReady, cancellationToken).ConfigureAwait(false))
            return RetainerAutomationResult.Failed("RetainerMenuTimeout", $"Timed out waiting for {target.RetainerName}'s command menu.");

        var verified = await framework.RunOnTick(() => VerifyActive(target.RetainerId), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!verified.Success)
            return verified;

        active = target;
        return RetainerAutomationResult.Succeeded("RetainerOpened", $"Opened and verified {target.RetainerName}.");
    }

    public async Task<RetainerAutomationResult> WaitForCurrentRetainerMenuAsync(CancellationToken cancellationToken = default) =>
        await WaitUntilAsync(IsCommandMenuReady, cancellationToken).ConfigureAwait(false)
            ? RetainerAutomationResult.Succeeded("RetainerMenuReady", "Current retainer command menu is ready.")
            : RetainerAutomationResult.Failed("RetainerMenuTimeout", "Timed out waiting for the current retainer command menu.");

    public async Task<RetainerAutomationResult> OpenInventoryAsync(CancellationToken cancellationToken = default)
    {
        var selected = await framework.RunOnTick(() => SelectCommand(2378), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!selected.Success)
            return selected;

        return await WaitUntilAsync(IsInventoryReady, cancellationToken).ConfigureAwait(false)
            ? RetainerAutomationResult.Succeeded("RetainerInventoryReady", "Retainer inventory opened.")
            : RetainerAutomationResult.Failed("RetainerInventoryTimeout", "Timed out waiting for retainer inventory.");
    }

    public Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken = default) =>
        framework.RunOnTick(() => DalamudRetainerInventory.ScanLoadedStacks(itemIds), cancellationToken: cancellationToken);

    public async Task<RetainerRetrievalResult> RetrieveAsync(DalamudInventoryStack stack, int quantity, CancellationToken cancellationToken = default)
    {
        var verified = await framework.RunOnTick(() => VerifyActive(active?.RetainerId ?? 0), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!verified.Success)
            return new(false, 0, "RetainerIdentityMismatch", verified.Message);

        var pending = await framework.RunOnTick(() => OpenContext(stack, quantity), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!pending.Success)
            return new(false, 0, "ContextOpenFailed", pending.Message);

        var selection = await PollAsync(() => SelectContextEntry(pending.Label, stack), 30, cancellationToken).ConfigureAwait(false);
        if (!selection.Success)
            return new(false, 0, "ContextSelectionFailed", selection.Message);

        if (pending.NeedsQuantity)
        {
            var submitted = await PollAsync(() => SubmitQuantity(pending.Quantity), 30, cancellationToken).ConfigureAwait(false);
            if (!submitted.Success)
                return new(false, 0, "QuantityFailed", submitted.Message);
        }

        for (var attempt = 0; attempt < 60; attempt++)
        {
            var result = await framework.RunOnTick(
                () => VerifyRetrieval(stack, pending.Quantity, pending.PlayerBefore),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.Success)
                return result;
            await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }

        return new(false, 0, "TransferNotObserved", $"Retrieval was not observed for item {stack.ItemId}.");
    }

    public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken = default) =>
        framework.RunOnTick(
            () => DalamudInventoryStackScanner.ScanLoadedStacks([InventoryType.Crystals], itemIds),
            cancellationToken: cancellationToken);

    public async Task<RetainerCrystalTransferResult> DepositCrystalAsync(
        DalamudInventoryStack stack,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        var verified = await framework.RunOnTick(() => VerifyActive(active?.RetainerId ?? 0), cancellationToken: cancellationToken).ConfigureAwait(false);
        return verified.Success
            ? await crystals.DepositAsync(stack, quantity, cancellationToken).ConfigureAwait(false)
            : new(false, 0, "RetainerIdentityMismatch", verified.Message);
    }

    public async Task<RetainerAutomationResult> CloseInventoryAsync(CancellationToken cancellationToken = default)
    {
        var state = await framework.RunOnTick(
            () => (Inventory: IsInventoryReady(), Menu: IsCommandMenuReady()),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (state.Menu && !state.Inventory)
            return RetainerAutomationResult.Succeeded("RetainerInventoryClosed", "Retainer inventory is already closed.");
        if (!state.Inventory)
            return RetainerAutomationResult.Failed("RetainerInventoryUnavailable", "Retainer inventory is not open.");

        await framework.RunOnTick(CloseInventory, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await WaitUntilAsync(IsCommandMenuReady, cancellationToken).ConfigureAwait(false)
            ? RetainerAutomationResult.Succeeded("RetainerInventoryClosed", "Retainer inventory closed.")
            : RetainerAutomationResult.Failed("RetainerMenuTimeout", "Timed out waiting for the retainer command menu after closing inventory.");
    }

    public async Task<RetainerAutomationResult> CloseRetainerAsync(CancellationToken cancellationToken = default)
    {
        var inventoryReady = await framework.RunOnTick(IsInventoryReady, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (inventoryReady)
        {
            var closed = await CloseInventoryAsync(cancellationToken).ConfigureAwait(false);
            if (!closed.Success)
                return closed;
        }
        else if (!await WaitUntilAsync(IsCommandMenuReady, cancellationToken).ConfigureAwait(false))
        {
            return RetainerAutomationResult.Failed("RetainerMenuTimeout", "Timed out waiting for the retainer command menu before closing the retainer.");
        }

        var quit = await framework.RunOnTick(() => SelectCommand(2383), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!quit.Success)
            return quit;
        if (!await WaitUntilAsync(() => IsReady(RetainerList), cancellationToken).ConfigureAwait(false))
            return RetainerAutomationResult.Failed("RetainerListTimeout", "Timed out waiting for the retainer list after closing the retainer.");

        active = null;
        return RetainerAutomationResult.Succeeded("RetainerClosed", "Retainer closed.");
    }

    public unsafe void CancelActive()
    {
        CloseInventory();
        foreach (var addonName in new[] { "InputNumeric", "ContextMenu", SelectString })
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
            if (addon is not null && addon->IsReady && addon->IsVisible)
                addon->Close(true);
        }

        active = null;
    }

    private async Task<bool> WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 180; attempt++)
        {
            if (await framework.RunOnTick(predicate, cancellationToken: cancellationToken).ConfigureAwait(false))
                return true;
            await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<RetainerAutomationResult> PollAsync(
        Func<RetainerAutomationResult> action,
        int attempts,
        CancellationToken cancellationToken)
    {
        var result = RetainerAutomationResult.Failed("ActionNotReady", "Action did not become ready.");
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            result = await framework.RunOnTick(action, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.Success)
                return result;
            await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private unsafe RetainerAutomationResult SelectRetainer(string name)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(RetainerList, 1);
        if (addon is null || !addon->IsReady || !addon->IsVisible)
            return RetainerAutomationResult.Failed("RetainerListUnavailable", "Retainer list is not ready.");

        const int first = 3;
        const int stride = 10;
        const int activeOffset = 8;
        var entries = new List<RetainerListEntry>();
        for (var index = 0; index < 10; index++)
        {
            var valueIndex = first + index * stride;
            if (valueIndex + activeOffset >= addon->AtkValuesCount)
                break;
            var value = addon->AtkValues + valueIndex;
            var rowName = value->Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.WideString or AtkValueType.ConstString
                ? value->GetValueAsString()
                : string.Empty;
            var activeValue = addon->AtkValues + valueIndex + activeOffset;
            entries.Add(new(rowName, activeValue->Type == AtkValueType.Bool && activeValue->Byte != 0));
        }

        var selectedIndex = RetainerUiAutomationText.FindRetainerListIndex(entries, name);
        if (selectedIndex is null)
            return RetainerAutomationResult.Failed("RetainerNotVisible", $"Retainer '{name}' was not visible as an active retainer-list row.");

        var values = stackalloc AtkValue[4];
        values[0] = new() { Type = AtkValueType.Int, Int = 2 };
        values[1] = new() { Type = AtkValueType.UInt, UInt = (uint)selectedIndex.Value };
        addon->FireCallback(4, values, true);
        return RetainerAutomationResult.Succeeded("RetainerSelected", $"Selected {name}.");
    }

    private unsafe bool IsCommandMenuReady()
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>(SelectString, 1);
        return addon is not null && addon->AtkUnitBase.IsReady && addon->AtkUnitBase.IsVisible && FindEntry(addon, ResolveAddonText(2378)) >= 0;
    }

    private unsafe RetainerAutomationResult SelectCommand(uint addonRow)
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>(SelectString, 1);
        if (addon is null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return RetainerAutomationResult.Failed("RetainerMenuUnavailable", "Retainer command menu is unavailable.");
        var index = FindEntry(addon, ResolveAddonText(addonRow));
        if (index < 0)
            return RetainerAutomationResult.Failed("RetainerCommandUnavailable", $"Retainer command entry {addonRow} is unavailable.");
        addon->AtkUnitBase.FireCallbackInt(index);
        return RetainerAutomationResult.Succeeded("RetainerCommandSelected", "Retainer command selected.");
    }

    private static unsafe int FindEntry(AddonSelectString* addon, string target)
    {
        var popup = addon->PopupMenu.PopupMenu;
        for (var index = 0; index < popup.EntryCount; index++)
            if (RetainerUiAutomationText.IsSelectStringEntryMatch(popup.EntryNames[index].ToString(), target))
                return index;
        return -1;
    }

    private unsafe PendingRetrieval OpenContext(DalamudInventoryStack stack, int requested)
    {
        if (requested <= 0 || active is null)
            return PendingRetrieval.Fail("Invalid retrieval request.");
        var manager = InventoryManager.Instance();
        if (manager == null)
            return PendingRetrieval.Fail("Inventory manager is unavailable.");
        var container = manager->GetInventoryContainer(stack.Container);
        if (container == null || !container->IsLoaded)
            return PendingRetrieval.Fail("Retainer source container is unavailable.");
        var slot = container->GetInventorySlot(stack.SlotIndex);
        if (slot == null || slot->ItemId != stack.ItemId || slot->Quantity != stack.Quantity)
            return PendingRetrieval.Fail("Exact retainer source slot changed before retrieval.");

        var quantity = Math.Min(requested, slot->Quantity);
        var retainerAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
        var context = AgentInventoryContext.Instance();
        if (retainerAgent is null || context is null)
            return PendingRetrieval.Fail("Retainer inventory context agent is unavailable.");
        context->OpenForItemSlot(stack.Container, stack.SlotIndex, 0, retainerAgent->GetAddonId());
        return new(true, quantity, quantity < slot->Quantity, ResolveAddonText(quantity < slot->Quantity ? 773u : 98u), CountPlayer(stack.ItemId), "Context menu requested.");
    }

    private unsafe RetainerAutomationResult SelectContextEntry(string label, DalamudInventoryStack stack)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        var agent = AgentInventoryContext.Instance();
        if (addon is null || !addon->IsReady || !addon->IsVisible || agent is null || agent->TargetInventoryId != stack.Container || agent->TargetInventorySlotId != stack.SlotIndex)
            return RetainerAutomationResult.Failed("ContextNotReady", "Waiting for exact retainer context menu.");

        var labels = new List<string>();
        foreach (var value in agent->EventParams)
            if (value.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.WideString or AtkValueType.ConstString)
                labels.Add(value.GetValueAsString());
        var index = RetainerUiAutomationText.FindContextMenuLabelIndex(labels, label);
        if (index is null)
            return RetainerAutomationResult.Failed("ContextEntryUnavailable", $"Context entry '{label}' is unavailable.");

        var values = stackalloc AtkValue[5];
        values[0] = new() { Type = AtkValueType.Int, Int = 0 };
        values[1] = new() { Type = AtkValueType.Int, Int = index.Value };
        return addon->FireCallback(5, values, true)
            ? RetainerAutomationResult.Succeeded("ContextActionSelected", "Context action selected.")
            : RetainerAutomationResult.Failed("ContextCallbackRejected", "Context action callback was rejected.");
    }

    private unsafe RetainerAutomationResult SubmitQuantity(int quantity)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("InputNumeric", 1);
        if (addon is null || !addon->IsReady || !addon->IsVisible)
            return RetainerAutomationResult.Failed("QuantityInputNotReady", "Waiting for quantity input.");
        addon->FireCallbackInt(quantity);
        return RetainerAutomationResult.Succeeded("QuantitySubmitted", "Quantity submitted.");
    }

    private unsafe RetainerRetrievalResult VerifyRetrieval(DalamudInventoryStack original, int transferred, int playerBefore)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return new(false, 0, "ContainerUnavailable", "Inventory manager became unavailable.");
        var container = manager->GetInventoryContainer(original.Container);
        if (container == null || !container->IsLoaded)
            return new(false, 0, "ContainerUnavailable", "Retainer source container became unavailable.");
        var slot = container->GetInventorySlot(original.SlotIndex);
        if (slot == null)
            return new(false, 0, "SlotUnavailable", "Retainer source slot became unavailable.");

        var playerAfter = CountPlayer(original.ItemId);
        if (RetainerRetrievalObservation.Matches(
                original.ItemId,
                original.Quantity,
                transferred,
                slot->ItemId,
                slot->Quantity,
                playerBefore,
                playerAfter))
        {
            return new(true, transferred, "TransferVerified", $"Verified {transferred}x item {original.ItemId}: player {playerBefore}->{playerAfter}.");
        }

        return new(false, 0, "TransferPending", "Waiting for matching retainer-slot and player-inventory deltas.");
    }

    private static unsafe RetainerAutomationResult VerifyActive(ulong expected)
    {
        var manager = RetainerManager.Instance();
        var current = manager == null ? null : manager->GetActiveRetainer();
        return current != null && expected > 0 && current->RetainerId == expected
            ? RetainerAutomationResult.Succeeded("RetainerIdentityVerified", "Retainer identity verified.")
            : RetainerAutomationResult.Failed("RetainerIdentityMismatch", "Active retainer identity does not match the expected stable ID.");
    }

    private static int CountPlayer(uint itemId) => PlayerItemContainers.Sum(type => DalamudInventoryStackScanner.CountLoadedItem(type, itemId));

    private unsafe bool IsReady(string name)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(name, 1);
        return addon is not null && addon->IsReady && addon->IsVisible;
    }

    private bool IsInventoryReady() => IsReady(InventoryLarge) || IsReady(InventorySmall);

    private unsafe void CloseInventory()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(InventoryLarge, 1);
        if (addon == null)
            addon = gameGui.GetAddonByName<AtkUnitBase>(InventorySmall, 1);
        if (addon != null && addon->IsReady && addon->IsVisible)
            addon->Close(true);
    }

    private string ResolveAddonText(uint rowId) => dataManager.GetExcelSheet<Addon>().GetRow(rowId).Text.ExtractText();

    private sealed record PendingRetrieval(bool Success, int Quantity, bool NeedsQuantity, string Label, int PlayerBefore, string Message)
    {
        public static PendingRetrieval Fail(string message) => new(false, 0, false, string.Empty, 0, message);
    }
}
