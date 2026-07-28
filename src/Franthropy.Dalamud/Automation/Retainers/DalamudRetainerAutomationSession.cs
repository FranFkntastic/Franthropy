using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Diagnostics;
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
    private const string ApprovedGameVersion = "2026.06.18.0000.0000";
    private const string PatchContractId = "franthropy.retainer-ui-callbacks";
    private static readonly IReadOnlyList<InventoryType> PlayerOrdinaryItemContainers =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly DalamudSummoningBellInteractor bell;
    private readonly DalamudRetainerCrystalTransfer crystals;
    private readonly DalamudRetainerItemTransfer items;
    private readonly DalamudRetainerItemRetrieval retrievals;
    private readonly string? currentGameVersion;
    private RetainerAutomationTarget? active;

    public DalamudRetainerAutomationSession(
        IFramework framework,
        IGameGui gameGui,
        IDataManager dataManager,
        IPluginLog log,
        IObjectTable objects,
        ITargetManager targets,
        ISigScanner sigScanner)
        : this(framework, gameGui, dataManager, log, objects, targets, sigScanner, null)
    {
    }

    internal DalamudRetainerAutomationSession(
        IFramework framework,
        IGameGui gameGui,
        IDataManager dataManager,
        IPluginLog log,
        IObjectTable objects,
        ITargetManager targets,
        ISigScanner sigScanner,
        string? currentGameVersion)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.dataManager = dataManager;
        bell = new(objects, targets, dataManager);
        crystals = new(sigScanner, gameGui, framework, log);
        items = new(sigScanner, gameGui, framework, log);
        retrievals = new(sigScanner, gameGui, framework, log);
        this.currentGameVersion = currentGameVersion;
    }

    /// <remarks>Read this property from the Dalamud framework thread.</remarks>
    public bool IsRetainerListReady => IsReady(RetainerList);

    /// <remarks>Call from the Dalamud framework thread.</remarks>
    public unsafe RetainerLocalUiObservation ObserveCurrentRetainerUi()
    {
        var module = AgentModule.Instance();
        var agent = module == null ? null : module->GetAgentByInternalId(AgentId.Retainer);
        var addon = gameGui.GetAddonByName<AtkUnitBase>(SelectString, 1);
        var manager = RetainerManager.Instance();
        var current = manager == null ? null : manager->GetActiveRetainer();
        return new(
            agent != null,
            agent != null && agent->IsAgentActive(),
            addon != null && addon->IsReady,
            addon != null && addon->IsReady && addon->IsVisible,
            agent != null && agent->OpenerEventInterface != null,
            addon == null ? 0u : addon->Id,
            current == null ? 0 : current->RetainerId,
            manager == null ? 0 : manager->RetainerObjectId);
    }

    /// <remarks>
    /// Diagnostic primitive: hides only the live retainer command addon. SelectString is
    /// not owned by the Retainer agent during accepted scene 2, so this deliberately
    /// bypasses agent lifecycle methods and does not invoke the command-menu callback.
    /// Call from the Dalamud framework thread.
    /// </remarks>
    public unsafe RetainerAutomationResult HideCurrentRetainerAddonLocally()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(SelectString, 1);
        if (addon == null || !addon->IsReady)
            return RetainerAutomationResult.Failed("RetainerCommandAddonUnavailable", "The retainer command addon is unavailable.");
        if (!addon->IsVisible)
            return RetainerAutomationResult.Failed("RetainerCommandAddonNotVisible", "The retainer command addon is already hidden.");

        addon->Hide(disableHideTransition: true, callCloseCallback: false, setShowHideFlags: 0);
        return RetainerAutomationResult.Succeeded(
            "RetainerCommandAddonHiddenLocally",
            "Requested a callback-free local hide on the live retainer command addon.");
    }

    /// <remarks>
    /// Diagnostic primitive paired with <see cref="HideCurrentRetainerAddonLocally"/>.
    /// Call from the Dalamud framework thread.
    /// </remarks>
    public unsafe RetainerAutomationResult ShowCurrentRetainerAddonLocally()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(SelectString, 1);
        if (addon == null || !addon->IsReady)
            return RetainerAutomationResult.Failed("RetainerCommandAddonUnavailable", "The retained retainer command addon is unavailable.");
        if (addon->IsVisible)
            return RetainerAutomationResult.Succeeded(
                "RetainerCommandAddonAlreadyVisible",
                "The retained retainer command addon is already visible.");

        addon->Show(disableShowTransition: true, unsetShowHideFlags: 0);
        return RetainerAutomationResult.Succeeded(
            "RetainerCommandAddonShownLocally",
            "Requested a local-only show on the retained retainer command addon.");
    }

    public async Task<RetainerAutomationResult> EnsureRetainerListAsync(CancellationToken cancellationToken = default)
    {
        var compatibility = EvaluatePatchCompatibility();
        if (!compatibility.IsApproved)
            return RetainerAutomationResult.Failed(GamePatchCompatibility.FailureCode, compatibility.Message);

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
        var compatibility = EvaluatePatchCompatibility();
        if (!compatibility.IsApproved)
            return RetainerAutomationResult.Failed(GamePatchCompatibility.FailureCode, compatibility.Message);

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

    public async Task<RetainerAutomationOpenResult> OpenFirstAvailableRetainerAsync(CancellationToken cancellationToken = default)
    {
        var compatibility = EvaluatePatchCompatibility();
        if (!compatibility.IsApproved)
            return RetainerAutomationOpenResult.Failed(GamePatchCompatibility.FailureCode, compatibility.Message);

        active = null;
        var selected = await framework.RunOnTick(SelectFirstAvailableRetainer, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!selected.Result.Success || string.IsNullOrWhiteSpace(selected.RetainerName))
            return RetainerAutomationOpenResult.Failed(selected.Result.Code, selected.Result.Message);
        if (!await WaitUntilAsync(IsCommandMenuReady, cancellationToken).ConfigureAwait(false))
            return RetainerAutomationOpenResult.Failed("RetainerMenuTimeout", "Timed out waiting for the first available retainer's command menu.");

        var retainerId = await framework.RunOnTick(ReadActiveRetainerId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (retainerId == 0)
            return RetainerAutomationOpenResult.Failed("RetainerIdentityUnavailable", "The opened retainer did not expose a stable retainer ID.");

        var target = new RetainerAutomationTarget(retainerId, selected.RetainerName);
        active = target;
        return RetainerAutomationOpenResult.Succeeded(target, "RetainerOpened", $"Opened and verified {target.RetainerName}.");
    }

    public async Task<RetainerAutomationResult> WaitForCurrentRetainerMenuAsync(CancellationToken cancellationToken = default) =>
        await WaitUntilAsync(IsCommandMenuReady, cancellationToken).ConfigureAwait(false)
            ? RetainerAutomationResult.Succeeded("RetainerMenuReady", "Current retainer command menu is ready.")
            : RetainerAutomationResult.Failed("RetainerMenuTimeout", "Timed out waiting for the current retainer command menu.");

    public async Task<RetainerAutomationResult> OpenInventoryAsync(CancellationToken cancellationToken = default)
    {
        var compatibility = EvaluatePatchCompatibility();
        if (!compatibility.IsApproved)
            return RetainerAutomationResult.Failed(GamePatchCompatibility.FailureCode, compatibility.Message);

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
        return verified.Success
            ? await retrievals.RetrieveAsync(stack, quantity, cancellationToken).ConfigureAwait(false)
            : new(false, 0, "RetainerIdentityMismatch", verified.Message);
    }

    public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerCrystalsAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken = default) =>
        framework.RunOnTick(
            () => DalamudInventoryStackScanner.ScanLoadedStacks([InventoryType.Crystals], itemIds),
            cancellationToken: cancellationToken);

    public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerInventoryAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken = default) =>
        framework.RunOnTick(
            () => DalamudInventoryStackScanner.ScanLoadedStacks(PlayerOrdinaryItemContainers, itemIds),
            cancellationToken: cancellationToken);

    public async Task<RetainerDepositResult> DepositAsync(
        DalamudInventoryStack stack,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        var verified = await framework.RunOnTick(() => VerifyActive(active?.RetainerId ?? 0), cancellationToken: cancellationToken).ConfigureAwait(false);
        return verified.Success
            ? await items.DepositAsync(stack, quantity, cancellationToken).ConfigureAwait(false)
            : new(false, 0, "RetainerIdentityMismatch", verified.Message);
    }

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

    public async Task<RetainerAutomationResult> CloseRetainerListAsync(CancellationToken cancellationToken = default)
    {
        var closed = await framework.RunOnTick(CloseRetainerList, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!closed.Success)
            return closed;

        return await WaitUntilAsync(() => !IsReady(RetainerList), cancellationToken).ConfigureAwait(false)
            ? RetainerAutomationResult.Succeeded("RetainerListClosed", "Retainer list closed.")
            : RetainerAutomationResult.Failed("RetainerListCloseTimeout", "Timed out waiting for the retainer list to close.");
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

    private GamePatchCompatibility EvaluatePatchCompatibility() =>
        GamePatchCompatibilityGate.Evaluate(PatchContractId, ApprovedGameVersion, currentGameVersion);

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

    private unsafe RetainerAutomationResult SelectRetainer(string name)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(RetainerList, 1);
        if (addon is null || !addon->IsReady || !addon->IsVisible)
            return RetainerAutomationResult.Failed("RetainerListUnavailable", "Retainer list is not ready.");

        var entries = ReadRetainerListEntries(addon);
        var selectedIndex = RetainerUiAutomationText.FindRetainerListIndex(entries, name);
        if (selectedIndex is null)
            return RetainerAutomationResult.Failed("RetainerNotVisible", $"Retainer '{name}' was not visible as an active retainer-list row.");

        var values = stackalloc AtkValue[4];
        values[0] = new() { Type = AtkValueType.Int, Int = 2 };
        values[1] = new() { Type = AtkValueType.UInt, UInt = (uint)selectedIndex.Value };
        addon->FireCallback(4, values, true);
        return RetainerAutomationResult.Succeeded("RetainerSelected", $"Selected {name}.");
    }

    private unsafe (RetainerAutomationResult Result, string? RetainerName) SelectFirstAvailableRetainer()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(RetainerList, 1);
        if (addon is null || !addon->IsReady || !addon->IsVisible)
            return (RetainerAutomationResult.Failed("RetainerListUnavailable", "Retainer list is not ready."), null);

        var entries = ReadRetainerListEntries(addon);
        var selectedIndex = RetainerUiAutomationText.FindFirstActiveRetainerListIndex(entries);
        if (selectedIndex is null)
            return (RetainerAutomationResult.Failed("RetainerNotVisible", "No active retainer was visible in the retainer list."), null);

        var values = stackalloc AtkValue[4];
        values[0] = new() { Type = AtkValueType.Int, Int = 2 };
        values[1] = new() { Type = AtkValueType.UInt, UInt = (uint)selectedIndex.Value };
        addon->FireCallback(4, values, true);
        var name = entries[selectedIndex.Value].Name;
        return (RetainerAutomationResult.Succeeded("RetainerSelected", $"Selected {name}."), name);
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

    private static unsafe RetainerAutomationResult VerifyActive(ulong expected)
    {
        var manager = RetainerManager.Instance();
        var current = manager == null ? null : manager->GetActiveRetainer();
        return current != null && expected > 0 && current->RetainerId == expected
            ? RetainerAutomationResult.Succeeded("RetainerIdentityVerified", "Retainer identity verified.")
            : RetainerAutomationResult.Failed("RetainerIdentityMismatch", "Active retainer identity does not match the expected stable ID.");
    }

    private static unsafe ulong ReadActiveRetainerId()
    {
        var manager = RetainerManager.Instance();
        var current = manager == null ? null : manager->GetActiveRetainer();
        return current == null ? 0 : current->RetainerId;
    }

    private static unsafe List<RetainerListEntry> ReadRetainerListEntries(AtkUnitBase* addon)
    {
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

        return entries;
    }

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

    private unsafe RetainerAutomationResult CloseRetainerList()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(RetainerList, 1);
        if (addon is null || !addon->IsReady || !addon->IsVisible)
            return RetainerAutomationResult.Failed("RetainerListUnavailable", "Retainer list is not ready.");

        addon->Close(true);
        return RetainerAutomationResult.Succeeded("RetainerListCloseRequested", "Retainer list close requested.");
    }

    private string ResolveAddonText(uint rowId) => dataManager.GetExcelSheet<Addon>().GetRow(rowId).Text.ExtractText();

}
