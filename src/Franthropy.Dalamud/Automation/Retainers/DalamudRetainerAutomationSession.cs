using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation.UIInput;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.AgentBridge;
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
    private const string Talk = "Talk";
    private const string InventoryLarge = "InventoryRetainerLarge";
    private const string InventorySmall = "InventoryRetainer";
    private const string SellingList = "RetainerSellList";
    private const string MarketList = "RetainerMarketList";
    private const string SellingListingEditor = "RetainerSell";
    private const string YesNo = "SelectYesno";
    private const string ApprovedGameVersion = "2026.07.16.0001.0000";
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
    private readonly DalamudRenderedUiTextActionDispatcher renderedUi;
    private readonly string? currentGameVersion;
    private RetainerAutomationTarget? active;

    private enum MarketListingPostDispatchOutcome
    {
        FailedBeforeSend,
        Sent,
        Indeterminate,
    }

    private sealed record MarketListingPostDispatchResult(
        MarketListingPostDispatchOutcome Outcome,
        RetainerMarketListingTarget? Listing,
        int SourceQuantityBefore,
        string Code,
        string Message);

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
        renderedUi = new(gameGui);
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
        var state = await framework.RunOnTick(
            () => (
                List: IsReady(RetainerList),
                Inventory: IsInventoryReady(),
                Menu: IsCommandMenuReady(),
                Talk: IsReady(Talk),
                ActiveRetainerId: ReadActiveRetainerId(),
                Selling: IsReady(SellingList) || IsReady(MarketList) || IsReady(SellingListingEditor) || IsReady(YesNo)),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (state.List)
            return RetainerAutomationResult.Succeeded("RetainerListReady", "Retainer list is ready.");
        if (state.Talk)
        {
            if (state.ActiveRetainerId == 0)
                return RetainerAutomationResult.Failed(
                    "RetainerTalkIdentityUnavailable",
                    "A talk window is open, but no active retainer identity is available.");

            var recovered = await ReachRetainerMenuAsync(
                null,
                cancellationToken,
                allowRetainerListCompletion: true).ConfigureAwait(false);
            return recovered.Code == "RetainerListReady"
                ? recovered
                : recovered.Success
                    ? await ReturnToRetainerListAsync(cancellationToken).ConfigureAwait(false)
                    : recovered;
        }
        if (state.Inventory || state.Menu || state.Selling)
            return await ReturnToRetainerListAsync(cancellationToken).ConfigureAwait(false);

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

    public Task<RetainerAutomationRosterResult> ScanAvailableRetainersAsync(CancellationToken cancellationToken = default) =>
        framework.RunOnTick(ScanAvailableRetainers, cancellationToken: cancellationToken);

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
        var menu = await ReachRetainerMenuAsync(target, cancellationToken).ConfigureAwait(false);
        if (!menu.Success)
            return menu;

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
        var menu = await ReachRetainerMenuAsync(null, cancellationToken).ConfigureAwait(false);
        if (!menu.Success)
            return RetainerAutomationOpenResult.Failed(menu.Code, menu.Message);

        var retainerId = await framework.RunOnTick(ReadActiveRetainerId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (retainerId == 0)
            return RetainerAutomationOpenResult.Failed("RetainerIdentityUnavailable", "The opened retainer did not expose a stable retainer ID.");

        var target = new RetainerAutomationTarget(retainerId, selected.RetainerName);
        active = target;
        return RetainerAutomationOpenResult.Succeeded(target, "RetainerOpened", $"Opened and verified {target.RetainerName}.");
    }

    public Task<RetainerAutomationResult> WaitForCurrentRetainerMenuAsync(CancellationToken cancellationToken = default) =>
        ReachRetainerMenuAsync(null, cancellationToken);

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

    public async Task<RetainerAutomationResult> OpenSellingListAsync(CancellationToken cancellationToken = default)
    {
        var compatibility = EvaluatePatchCompatibility();
        if (!compatibility.IsApproved)
            return RetainerAutomationResult.Failed(GamePatchCompatibility.FailureCode, compatibility.Message);

        var verified = await framework.RunOnTick(
            () => VerifyActive(active?.RetainerId ?? 0),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!verified.Success)
            return verified;

        var alreadyOpen = await framework.RunOnTick(
            () => IsReady(SellingList),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (alreadyOpen)
            return RetainerAutomationResult.Succeeded("RetainerSellingListReady", "Retainer selling list is ready.");

        var selected = await framework.RunOnTick(
            () => SelectCommand(2380),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!selected.Success)
            return selected;

        if (await WaitUntilAsync(() => IsReady(SellingList), cancellationToken).ConfigureAwait(false))
            return RetainerAutomationResult.Succeeded("RetainerSellingListReady", "Retainer selling list opened.");

        var observed = await ObserveSellingUiAsync(cancellationToken).ConfigureAwait(false);
        return RetainerAutomationResult.Failed(
            "RetainerSellingListTimeout",
            $"Timed out waiting for {SellingList}. Observed {FormatSellingUiObservation(observed)}.");
    }

    public async Task<RetainerAutomationResult> OpenSellingListingAsync(
        RetainerMarketListingTarget listing,
        CancellationToken cancellationToken = default)
    {
        var opened = await OpenSellingListingCoreAsync(listing, cancellationToken).ConfigureAwait(false);
        return opened.Result;
    }

    private async Task<(RetainerAutomationResult Result, RetainerMarketListingTarget? Listing)> OpenSellingListingCoreAsync(
        RetainerMarketListingTarget listing,
        CancellationToken cancellationToken)
    {
        if (listing.ItemId == 0 ||
            listing.Quantity <= 0 ||
            listing.UnitPrice is not > 0 or > RetainerMarketPricePolicy.MaximumUnitPrice)
        {
            return (
                RetainerAutomationResult.Failed("InvalidMarketListing", "A complete physical market-listing identity is required."),
                null);
        }

        var opened = await OpenSellingListAsync(cancellationToken).ConfigureAwait(false);
        if (!opened.Success)
            return (opened, null);

        var reconciled = await framework.RunOnTick(
            () => ResolveMarketListing(listing),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!reconciled.Result.Success)
            return (reconciled.Result, null);

        var selected = await framework.RunOnTick(
            () => SelectMarketListing(reconciled.SlotIndex),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!selected.Success)
            return (selected, null);

        if (!await WaitUntilAsync(() => IsReady(SellingListingEditor), cancellationToken).ConfigureAwait(false))
        {
            return (
                RetainerAutomationResult.Failed(
                    "RetainerSellingListingTimeout",
                    "The verified listing was selected, but its editor did not become ready."),
                null);
        }

        return (
            RetainerAutomationResult.Succeeded("RetainerSellingListingReady", "Opened the verified retainer listing."),
            listing with { SlotIndex = reconciled.SlotIndex });
    }

    public Task<RetainerMarketListingScanResult> ScanMarketListingsAsync(CancellationToken cancellationToken = default) =>
        framework.RunOnTick(ScanMarketListings, cancellationToken: cancellationToken);

    public async Task<RetainerAutomationResult> UpdateSellingListingPriceAsync(
        RetainerMarketListingTarget listing,
        uint newUnitPrice,
        CancellationToken cancellationToken = default)
    {
        if (!RetainerMarketPricePolicy.IsValidMutation(listing.UnitPrice, newUnitPrice))
        {
            if (listing.UnitPrice is not > 0 or > RetainerMarketPricePolicy.MaximumUnitPrice)
            {
                return RetainerAutomationResult.Failed(
                    "InvalidObservedMarketUnitPrice",
                    "An exact valid live unit price must be observed before changing a listing.");
            }
            if (newUnitPrice is 0 or > RetainerMarketPricePolicy.MaximumUnitPrice)
            {
                return RetainerAutomationResult.Failed(
                    "InvalidMarketUnitPrice",
                    $"The market unit price must be between 1 and {RetainerMarketPricePolicy.MaximumUnitPrice:N0} gil.");
            }

            return RetainerAutomationResult.Failed(
                "MarketUnitPriceUnchanged",
                "The requested market unit price already matches the observed listing.");
        }

        var opened = await OpenSellingListingCoreAsync(listing, cancellationToken).ConfigureAwait(false);
        if (!opened.Result.Success || opened.Listing is null)
            return opened.Result;

        var updated = await framework.RunOnTick(
            () => SetSellingListingPrice(newUnitPrice),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!updated.Success)
            return updated;

        var preexistingConfirmation = await framework.RunOnTick(
            () => IsReady(YesNo),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (preexistingConfirmation)
        {
            return RetainerAutomationResult.Failed(
                "UnexpectedRetainerMarketConfirmation",
                "A confirmation dialog was already open before the verified listing submitted its price change.");
        }

        var expected = opened.Listing with { UnitPrice = newUnitPrice };
        var mutationMayHaveBeenSent = false;
        try
        {
            var confirmed = await framework.RunOnTick(
                () => ConfirmSellingListingPrice(() => mutationMayHaveBeenSent = true),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!confirmed.Success)
                return confirmed;

            var confirmationSent = false;
            for (var attempt = 0; attempt < 180; attempt++)
            {
                var observed = await framework.RunOnTick(
                    () => ObserveSellingListingPriceCommit(expected),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var action = RetainerMarketPriceUpdatePolicy.Decide(
                    observed.Committed,
                    observed.YesNoReady,
                    confirmationSent);
                if (action == RetainerMarketPriceUpdateAction.Complete)
                {
                    return RetainerAutomationResult.Succeeded(
                        "RetainerMarketPriceUpdated",
                        "The exact live retainer listing committed the requested unit price.");
                }
                if (action == RetainerMarketPriceUpdateAction.ConfirmOnce)
                {
                    confirmationSent = true;
                    var accepted = await framework.RunOnTick(
                        ConfirmYesNo,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (!accepted.Success)
                    {
                        return RetainerAutomationResult.Failed(
                            "RetainerMarketPriceConfirmationIndeterminate",
                            $"The price request was sent, but its owned confirmation could not be accepted: {accepted.Message} Re-scan before retrying.");
                    }
                }

                await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
            }

            return RetainerAutomationResult.Failed(
                "RetainerMarketPriceUpdateIndeterminate",
                "The price request was sent, but its exact live postcondition was not observed. Re-scan before deciding whether to retry.");
        }
        catch (OperationCanceledException exception) when (mutationMayHaveBeenSent)
        {
            throw new RetainerMarketMutationIndeterminateException(
                "RetainerMarketPriceUpdateCancelledIndeterminate",
                "Cancellation occurred after the price request may have been sent. Re-scan before deciding whether to retry.",
                expected,
                exception,
                cancellationToken);
        }
        catch (Exception exception) when (mutationMayHaveBeenSent)
        {
            return RetainerAutomationResult.Failed(
                "RetainerMarketPriceUpdateIndeterminate",
                $"The price request may have been sent before an observation fault: {exception.Message} Re-scan before retrying.");
        }
    }

    public async Task<RetainerMarketListingPostResult> PostMarketListingAsync(
        DalamudInventoryStack source,
        int quantity,
        uint unitPrice,
        CancellationToken cancellationToken = default)
    {
        var compatibility = EvaluatePatchCompatibility();
        if (!compatibility.IsApproved)
            return RetainerMarketListingPostResult.Failed(GamePatchCompatibility.FailureCode, compatibility.Message);

        if (quantity <= 0 || quantity > source.Quantity)
            return RetainerMarketListingPostResult.Failed(
                "InvalidMarketListingQuantity",
                "The listing quantity must be positive and no greater than the exact observed source stack.");
        if (unitPrice is 0 or > RetainerMarketPricePolicy.MaximumUnitPrice)
            return RetainerMarketListingPostResult.Failed(
                "InvalidMarketUnitPrice",
                $"The market unit price must be between 1 and {RetainerMarketPricePolicy.MaximumUnitPrice:N0} gil.");

        var verified = await framework.RunOnTick(
            () => VerifyActive(active?.RetainerId ?? 0),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!verified.Success)
            return RetainerMarketListingPostResult.Failed(verified.Code, verified.Message);

        var requestMayHaveBeenSent = false;
        RetainerMarketListingTarget? expected = null;
        try
        {
            var started = await framework.RunOnTick(
                () => StartMarketListingPost(
                    source,
                    quantity,
                    unitPrice,
                    () => requestMayHaveBeenSent = true),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            expected = started.Listing;
            if (started.Outcome == MarketListingPostDispatchOutcome.FailedBeforeSend)
                return RetainerMarketListingPostResult.Failed(started.Code, started.Message);
            if (started.Outcome == MarketListingPostDispatchOutcome.Indeterminate)
                return RetainerMarketListingPostResult.Indeterminate(started.Listing, started.Code, started.Message);
            if (started.Listing is null)
            {
                return RetainerMarketListingPostResult.Indeterminate(
                    null,
                    "RetainerMarketListingPostDispatchInvalid",
                    "The listing request reported dispatch without an exact expected listing. Re-scan before retrying.");
            }

            for (var attempt = 0; attempt < 180; attempt++)
            {
                var committed = await framework.RunOnTick(
                    () => ObserveMarketListingPost(
                        source,
                        started.SourceQuantityBefore,
                        started.Listing),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (committed)
                    return RetainerMarketListingPostResult.Succeeded(started.Listing);

                await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
            }

            return RetainerMarketListingPostResult.Indeterminate(
                started.Listing,
                "RetainerMarketListingPostIndeterminate",
                "The listing request was sent, but its exact source decrement and live listing were not observed. Re-scan before retrying.");
        }
        catch (OperationCanceledException exception) when (requestMayHaveBeenSent)
        {
            throw new RetainerMarketMutationIndeterminateException(
                "RetainerMarketListingPostCancelledIndeterminate",
                "Cancellation occurred after the listing request may have been sent. Re-scan before deciding whether to retry.",
                expected,
                exception,
                cancellationToken);
        }
        catch (Exception exception) when (requestMayHaveBeenSent)
        {
            return RetainerMarketListingPostResult.Indeterminate(
                expected,
                "RetainerMarketListingPostIndeterminate",
                $"The listing request may have been sent before an observation fault: {exception.Message} Re-scan before retrying.");
        }
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

    public Task<IReadOnlyList<DalamudInventoryStack>> ScanPlayerInventoryAsync(IReadOnlySet<uint>? itemIds = null, CancellationToken cancellationToken = default) =>
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

    public Task<RetainerSellingUiObservation> ObserveSellingUiAsync(CancellationToken cancellationToken = default) =>
        framework.RunOnTick(
            () => new RetainerSellingUiObservation(
                IsReady(MarketList),
                IsReady(SellingList),
                IsReady(SellingListingEditor),
                IsReady(YesNo),
                IsInventoryReady(),
                IsCommandMenuReady(),
                IsReady(RetainerList)),
            cancellationToken: cancellationToken);

    public async Task<RetainerAutomationResult> ReturnToRetainerListAsync(CancellationToken cancellationToken = default)
    {
        var state = await ObserveSellingUiAsync(cancellationToken).ConfigureAwait(false);
        if (state.RetainerListReady)
        {
            active = null;
            return RetainerAutomationResult.Succeeded("RetainerListReady", "Retainer list is ready.");
        }

        await framework.RunOnTick(
            () =>
            {
                CloseSurface(YesNo);
                CloseSurface(SellingListingEditor);
                CloseSurface(SellingList);
                CloseSurface(MarketList);
                CloseInventory();
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!await WaitUntilAsync(IsCommandMenuReady, cancellationToken).ConfigureAwait(false))
        {
            var observed = await ObserveSellingUiAsync(cancellationToken).ConfigureAwait(false);
            return RetainerAutomationResult.Failed(
                "RetainerMenuRecoveryTimeout",
                $"Timed out returning to the retainer command menu. Observed {FormatSellingUiObservation(observed)}.");
        }

        var activeRetainerId = await framework.RunOnTick(ReadActiveRetainerId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (activeRetainerId == 0)
            return RetainerAutomationResult.Failed(
                "RetainerIdentityUnavailable",
                "The active retainer identity became unavailable before returning to the retainer list.");

        var quit = await framework.RunOnTick(() => SelectCommand(2383), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!quit.Success)
            return quit;
        var returned = await ReachRetainerListAfterQuitAsync(activeRetainerId, cancellationToken).ConfigureAwait(false);
        if (!returned.Success)
            return returned;

        active = null;
        return RetainerAutomationResult.Succeeded("RetainerListRecovered", "Returned to the retainer list.");
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

        var activeRetainerId = await framework.RunOnTick(ReadActiveRetainerId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (activeRetainerId == 0)
            return RetainerAutomationResult.Failed(
                "RetainerIdentityUnavailable",
                "The active retainer identity became unavailable before closing the retainer.");

        var quit = await framework.RunOnTick(() => SelectCommand(2383), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!quit.Success)
            return quit;
        var returned = await ReachRetainerListAfterQuitAsync(activeRetainerId, cancellationToken).ConfigureAwait(false);
        if (!returned.Success)
            return returned;

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
        foreach (var addonName in new[] { "InputNumeric", "ContextMenu", YesNo, SellingListingEditor, SellingList, MarketList, SelectString })
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
            if (addon is not null && addon->IsReady && addon->IsVisible)
                addon->Close(true);
        }

        active = null;
    }

    private GamePatchCompatibility EvaluatePatchCompatibility() =>
        GamePatchCompatibilityGate.Evaluate(PatchContractId, ApprovedGameVersion, currentGameVersion);

    private async Task<RetainerAutomationResult> ReachRetainerMenuAsync(
        RetainerAutomationTarget? expected,
        CancellationToken cancellationToken,
        bool allowRetainerListCompletion = false)
    {
        const int maximumAttempts = 180;
        const int talkAdvanceCooldownTicks = 6;
        const int maximumTalkAdvances = 12;
        var nextTalkAdvanceAttempt = 0;
        var talkAdvances = 0;
        RetainerOpeningObservation observed = default;

        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            observed = await framework.RunOnTick(ObserveRetainerOpening, cancellationToken: cancellationToken).ConfigureAwait(false);
            var action = RetainerOpeningPolicy.Decide(
                observed,
                expected?.RetainerId,
                allowRetainerListCompletion);
            if (action == RetainerOpeningAction.Complete)
                return RetainerAutomationResult.Succeeded("RetainerMenuReady", "Retainer command menu is ready.");
            if (action == RetainerOpeningAction.CompleteAtList)
                return RetainerAutomationResult.Succeeded("RetainerListReady", "Retainer list is ready.");
            if (action == RetainerOpeningAction.RejectIdentity)
            {
                return RetainerAutomationResult.Failed(
                    "RetainerIdentityMismatch",
                    $"The talk window belongs to retainer {observed.ActiveRetainerId}, not expected retainer {expected!.RetainerId}.");
            }

            if (action == RetainerOpeningAction.AdvanceTalk && attempt >= nextTalkAdvanceAttempt)
            {
                if (talkAdvances >= maximumTalkAdvances)
                {
                    return RetainerAutomationResult.Failed(
                        "RetainerTalkAdvanceLimit",
                        $"Stopped after {maximumTalkAdvances} bounded talk advances while opening {FormatRetainerTarget(expected)}.");
                }

                var advanced = await framework.RunOnTick(AdvanceTalk, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (advanced.Success)
                    talkAdvances++;
                else if (advanced.Code != "RetainerTalkUnavailable")
                    return advanced;
                nextTalkAdvanceAttempt = attempt + talkAdvanceCooldownTicks;
            }

            await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }

        return RetainerAutomationResult.Failed(
            "RetainerMenuTimeout",
            $"Timed out waiting for {FormatRetainerTarget(expected)} command menu after {talkAdvances} talk advance(s). " +
            $"Observed {FormatRetainerOpeningObservation(observed)}.");
    }

    private async Task<RetainerAutomationResult> ReachRetainerListAfterQuitAsync(
        ulong expectedRetainerId,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 180;
        const int talkAdvanceCooldownTicks = 6;
        const int maximumTalkAdvances = 12;
        var nextTalkAdvanceAttempt = 0;
        var talkAdvances = 0;
        RetainerClosingObservation observed = default;

        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            observed = await framework.RunOnTick(ObserveRetainerClosing, cancellationToken: cancellationToken).ConfigureAwait(false);
            var action = RetainerClosingPolicy.Decide(observed, expectedRetainerId);
            if (action == RetainerClosingAction.Complete)
                return RetainerAutomationResult.Succeeded("RetainerListReady", "Retainer list is ready.");
            if (action == RetainerClosingAction.RejectIdentity)
            {
                return RetainerAutomationResult.Failed(
                    "RetainerIdentityMismatch",
                    $"The closing dialogue belongs to retainer {observed.ActiveRetainerId}, not expected retainer {expectedRetainerId}.");
            }

            if (action == RetainerClosingAction.AdvanceTalk && attempt >= nextTalkAdvanceAttempt)
            {
                if (talkAdvances >= maximumTalkAdvances)
                {
                    return RetainerAutomationResult.Failed(
                        "RetainerTalkAdvanceLimit",
                        $"Stopped after {maximumTalkAdvances} bounded talk advances while closing retainer {expectedRetainerId}.");
                }

                var advanced = await framework.RunOnTick(AdvanceTalk, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (advanced.Success)
                    talkAdvances++;
                else if (advanced.Code != "RetainerTalkUnavailable")
                    return advanced;
                nextTalkAdvanceAttempt = attempt + talkAdvanceCooldownTicks;
            }

            await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }

        return RetainerAutomationResult.Failed(
            "RetainerListTimeout",
            $"Timed out returning to the retainer list after {talkAdvances} farewell advance(s). " +
            $"Observed {FormatRetainerClosingObservation(observed)}.");
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

    private unsafe RetainerAutomationRosterResult ScanAvailableRetainers()
    {
        if (!IsReady(RetainerList))
            return RetainerAutomationRosterResult.Failed(
                "RetainerListUnavailable",
                "The retainer list is not ready for roster reconciliation.");

        var manager = RetainerManager.Instance();
        if (manager == null)
            return RetainerAutomationRosterResult.Failed(
                "RetainerManagerUnavailable",
                "The live retainer manager is unavailable.");

        var retainers = new List<RetainerAutomationTarget>();
        for (var index = 0; index < manager->GetRetainerCount(); index++)
        {
            var retainer = manager->Retainers[index];
            var name = retainer.NameString;
            if (!retainer.Available || retainer.RetainerId == 0 || string.IsNullOrWhiteSpace(name))
                continue;
            retainers.Add(new(retainer.RetainerId, name));
        }

        return retainers.Count > 0
            ? RetainerAutomationRosterResult.Succeeded(retainers)
            : RetainerAutomationRosterResult.Failed(
                "RetainerRosterEmpty",
                "No available retainers were present in the reconciled live roster.");
    }

    private unsafe bool IsCommandMenuReady()
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>(SelectString, 1);
        return addon is not null && addon->AtkUnitBase.IsReady && addon->AtkUnitBase.IsVisible && FindEntry(addon, ResolveAddonText(2378)) >= 0;
    }

    private unsafe RetainerOpeningObservation ObserveRetainerOpening() =>
        new(
            IsCommandMenuReady(),
            IsReady(Talk),
            IsReady(RetainerList),
            ReadActiveRetainerId());

    private unsafe RetainerClosingObservation ObserveRetainerClosing() =>
        new(
            IsReady(RetainerList),
            IsReady(Talk),
            ReadActiveRetainerId());

    private unsafe RetainerAutomationResult AdvanceTalk()
    {
        var addon = gameGui.GetAddonByName<AddonTalk>(Talk, 1);
        if (addon is null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return RetainerAutomationResult.Failed("RetainerTalkUnavailable", "The retainer talk window is unavailable.");

        try
        {
            new AddonMaster.Talk((nint)addon).Click();
            return RetainerAutomationResult.Succeeded("RetainerTalkAdvanced", "Advanced the current retainer greeting.");
        }
        catch (Exception exception)
        {
            return RetainerAutomationResult.Failed(
                "RetainerTalkAdvanceFailed",
                $"The retainer greeting could not be advanced: {exception.Message}");
        }
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

    private static unsafe (RetainerAutomationResult Result, int SlotIndex) ResolveMarketListing(
        RetainerMarketListingTarget expected)
    {
        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer(InventoryType.RetainerMarket);
        if (container == null || !container->IsLoaded)
            return (
                RetainerAutomationResult.Failed("RetainerMarketUnavailable", "The live retainer market inventory is unavailable."),
                -1);

        if (expected.SlotIndex >= 0 &&
            expected.SlotIndex < container->Size &&
            MatchesMarketListing(manager, container, expected.SlotIndex, expected))
            return (
                RetainerAutomationResult.Succeeded("RetainerMarketListingVerified", "The live retainer listing matches the requested listing."),
                expected.SlotIndex);

        return (
            RetainerAutomationResult.Failed(
                "RetainerMarketListingChanged",
                "The requested listing no longer matches its exact observed retainer-market slot."),
            -1);
    }

    private unsafe RetainerMarketListingScanResult ScanMarketListings()
    {
        var verified = VerifyActive(active?.RetainerId ?? 0);
        if (!verified.Success)
            return RetainerMarketListingScanResult.Failed(verified.Code, verified.Message);

        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer(InventoryType.RetainerMarket);
        if (container == null || !container->IsLoaded)
            return RetainerMarketListingScanResult.Failed(
                "RetainerMarketUnavailable",
                "The live retainer market inventory is unavailable.");

        var listings = new List<RetainerMarketListingTarget>();
        for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
        {
            var slot = container->GetInventorySlot(slotIndex);
            if (slot == null || slot->ItemId == 0 || slot->Quantity == 0)
                continue;

            var unitPrice = manager->GetRetainerMarketPrice(checked((short)slotIndex));
            if (unitPrice is 0 or > RetainerMarketPricePolicy.MaximumUnitPrice)
                return RetainerMarketListingScanResult.Failed(
                    "RetainerMarketPriceInvalid",
                    $"The live listing in slot {slotIndex} has an invalid unit price.");

            listings.Add(new(
                slotIndex,
                slot->ItemId,
                slot->Quantity,
                slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality),
                checked((uint)unitPrice)));
        }

        return RetainerMarketListingScanResult.Succeeded(listings);
    }

    private unsafe MarketListingPostDispatchResult StartMarketListingPost(
        DalamudInventoryStack source,
        int quantity,
        uint unitPrice,
        System.Action markDispatchStarted)
    {
        if (!PlayerOrdinaryItemContainers.Contains(source.Container))
        {
            return new(
                MarketListingPostDispatchOutcome.FailedBeforeSend,
                null,
                0,
                "UnsupportedMarketListingSource",
                "Only an exact live ordinary player-inventory stack may be posted through this primitive.");
        }

        var manager = InventoryManager.Instance();
        var sourceContainer = manager == null ? null : manager->GetInventoryContainer(source.Container);
        var marketContainer = manager == null ? null : manager->GetInventoryContainer(InventoryType.RetainerMarket);
        if (manager == null ||
            sourceContainer == null ||
            !sourceContainer->IsLoaded ||
            marketContainer == null ||
            !marketContainer->IsLoaded)
        {
            return new(
                MarketListingPostDispatchOutcome.FailedBeforeSend,
                null,
                0,
                "RetainerMarketInventoryUnavailable",
                "The exact source or live retainer market inventory is unavailable.");
        }

        if (source.SlotIndex < 0 || source.SlotIndex >= sourceContainer->Size)
        {
            return new(
                MarketListingPostDispatchOutcome.FailedBeforeSend,
                null,
                0,
                "MarketListingSourceChanged",
                "The exact source slot is no longer valid.");
        }

        var sourceSlot = sourceContainer->GetInventorySlot(source.SlotIndex);
        if (sourceSlot == null ||
            sourceSlot->ItemId != source.ItemId ||
            sourceSlot->Quantity != source.Quantity ||
            sourceSlot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) != source.IsHighQuality)
        {
            return new(
                MarketListingPostDispatchOutcome.FailedBeforeSend,
                null,
                0,
                "MarketListingSourceChanged",
                "The exact source stack changed before listing.");
        }

        var marketSlotIndex = -1;
        for (var slotIndex = 0; slotIndex < marketContainer->Size; slotIndex++)
        {
            var slot = marketContainer->GetInventorySlot(slotIndex);
            if (slot != null && slot->ItemId == 0)
            {
                marketSlotIndex = slotIndex;
                break;
            }
        }

        if (marketSlotIndex < 0)
        {
            return new(
                MarketListingPostDispatchOutcome.FailedBeforeSend,
                null,
                0,
                "RetainerMarketFull",
                "The active retainer has no empty market-listing slot.");
        }

        var expected = new RetainerMarketListingTarget(
            marketSlotIndex,
            source.ItemId,
            quantity,
            source.IsHighQuality,
            unitPrice);
        try
        {
            markDispatchStarted();
            manager->MoveToRetainerMarket(
                source.Container,
                checked((ushort)source.SlotIndex),
                InventoryType.RetainerMarket,
                checked((ushort)marketSlotIndex),
                checked((uint)quantity),
                unitPrice);
            return new(
                MarketListingPostDispatchOutcome.Sent,
                expected,
                source.Quantity,
                "RetainerMarketListingPostSent",
                "The exact listing request was sent once; awaiting its live postcondition.");
        }
        catch (Exception exception)
        {
            return new(
                MarketListingPostDispatchOutcome.Indeterminate,
                expected,
                source.Quantity,
                "RetainerMarketListingPostDispatchIndeterminate",
                $"The listing call faulted after dispatch began: {exception.Message} Re-scan before retrying.");
        }
    }

    private static unsafe bool ObserveMarketListingPost(
        DalamudInventoryStack source,
        int sourceQuantityBefore,
        RetainerMarketListingTarget expected)
    {
        var manager = InventoryManager.Instance();
        var sourceContainer = manager == null ? null : manager->GetInventoryContainer(source.Container);
        var marketContainer = manager == null ? null : manager->GetInventoryContainer(InventoryType.RetainerMarket);
        if (manager == null ||
            sourceContainer == null ||
            !sourceContainer->IsLoaded ||
            marketContainer == null ||
            !marketContainer->IsLoaded ||
            source.SlotIndex < 0 ||
            source.SlotIndex >= sourceContainer->Size ||
            expected.SlotIndex < 0 ||
            expected.SlotIndex >= marketContainer->Size)
        {
            return false;
        }

        var expectedSourceQuantity = sourceQuantityBefore - expected.Quantity;
        var sourceSlot = sourceContainer->GetInventorySlot(source.SlotIndex);
        var sourceMatches = expectedSourceQuantity == 0
            ? sourceSlot == null || sourceSlot->ItemId != source.ItemId || sourceSlot->Quantity == 0
            : sourceSlot != null &&
              sourceSlot->ItemId == source.ItemId &&
              sourceSlot->Quantity == expectedSourceQuantity &&
              sourceSlot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) == source.IsHighQuality;
        if (!sourceMatches)
            return false;

        return MatchesMarketListing(manager, marketContainer, expected.SlotIndex, expected);
    }

    private unsafe RetainerAutomationResult SetSellingListingPrice(uint newUnitPrice)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(SellingListingEditor, 1);
        if (addon == null || !addon->IsReady || !addon->IsVisible)
            return RetainerAutomationResult.Failed(
                "RetainerSellingListingUnavailable",
                "The verified retainer listing editor is unavailable.");

        var values = stackalloc AtkValue[2];
        values[0] = new() { Type = AtkValueType.Int, Int = 2 };
        values[1] = new() { Type = AtkValueType.UInt, UInt = newUnitPrice };
        addon->FireCallback(2, values, true);
        return RetainerAutomationResult.Succeeded(
            "RetainerMarketPriceEntered",
            "Entered the requested unit price in the verified listing editor.");
    }

    private unsafe RetainerAutomationResult ConfirmSellingListingPrice(System.Action markDispatchStarted)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(SellingListingEditor, 1);
        if (addon == null || !addon->IsReady || !addon->IsVisible)
            return RetainerAutomationResult.Failed(
                "RetainerSellingListingUnavailable",
                "The verified retainer listing editor is unavailable at confirmation.");

        markDispatchStarted();
        var value = new AtkValue { Type = AtkValueType.Int, Int = 0 };
        addon->FireCallback(1, &value, true);
        return RetainerAutomationResult.Succeeded(
            "RetainerMarketPriceConfirmationSent",
            "Submitted the verified listing price exactly once.");
    }

    private unsafe (bool Committed, bool YesNoReady) ObserveSellingListingPriceCommit(
        RetainerMarketListingTarget expected)
    {
        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer(InventoryType.RetainerMarket);
        var committed = false;
        if (manager != null &&
            container != null &&
            container->IsLoaded &&
            expected.SlotIndex >= 0 &&
            expected.SlotIndex < container->Size)
        {
            var slot = container->GetInventorySlot(expected.SlotIndex);
            committed = slot != null &&
                RetainerMarketPriceCommitObservation.Matches(
                    expected,
                    expected.SlotIndex,
                    slot->ItemId,
                    slot->Quantity,
                    slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality),
                    manager->GetRetainerMarketPrice(checked((short)expected.SlotIndex)),
                    IsReady(SellingListingEditor));
        }

        return (
            committed,
            IsReady(YesNo));
    }

    private unsafe RetainerAutomationResult ConfirmYesNo()
    {
        var addon = gameGui.GetAddonByName<AddonSelectYesno>(YesNo, 1);
        if (addon == null ||
            !addon->AtkUnitBase.IsReady ||
            !addon->AtkUnitBase.IsVisible ||
            addon->YesButton == null ||
            !addon->YesButton->IsEnabled)
            return RetainerAutomationResult.Failed(
                "RetainerMarketPriceConfirmationUnavailable",
                "The owned listing-price confirmation is unavailable or cannot be accepted.");

        addon->YesButton->ClickAddonButton(&addon->AtkUnitBase);
        return RetainerAutomationResult.Succeeded(
            "RetainerMarketPriceConfirmationAccepted",
            "Accepted the listing-price confirmation dialog.");
    }

    private static unsafe bool MatchesMarketListing(
        InventoryManager* manager,
        InventoryContainer* container,
        int slotIndex,
        RetainerMarketListingTarget expected)
    {
        var slot = container->GetInventorySlot(slotIndex);
        if (slot == null || slot->ItemId == 0 || slot->Quantity == 0)
            return false;

        return RetainerMarketListingObservation.Matches(
            expected,
            slot->ItemId,
            slot->Quantity,
            slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality),
            manager->GetRetainerMarketPrice(checked((short)slotIndex)));
    }

    private unsafe RetainerAutomationResult SelectMarketListing(int slotIndex)
    {
        var activated = renderedUi.TryActivateListRowIndex(SellingList, slotIndex);
        return activated.Success
            ? RetainerAutomationResult.Succeeded("RetainerMarketListingSelected", "Selected the verified retainer listing.")
            : RetainerAutomationResult.Failed(activated.Code, activated.Message);
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

    private unsafe void CloseSurface(string addonName)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        if (addon != null && addon->IsReady && addon->IsVisible)
            addon->Close(true);
    }

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

    private static string FormatSellingUiObservation(RetainerSellingUiObservation observed)
    {
        var ready = new List<string>();
        if (observed.MarketListReady) ready.Add(MarketList);
        if (observed.SellListReady) ready.Add(SellingList);
        if (observed.ListingEditorReady) ready.Add(SellingListingEditor);
        if (observed.ConfirmationReady) ready.Add(YesNo);
        if (observed.InventoryReady) ready.Add("retainer inventory");
        if (observed.CommandMenuReady) ready.Add("retainer command menu");
        if (observed.RetainerListReady) ready.Add(RetainerList);
        return ready.Count == 0 ? "no known retainer surface" : string.Join(", ", ready);
    }

    private static string FormatRetainerTarget(RetainerAutomationTarget? target) =>
        target is null ? "the current retainer's" : $"{target.RetainerName}'s";

    private static string FormatRetainerOpeningObservation(RetainerOpeningObservation observed)
    {
        var ready = new List<string>();
        if (observed.CommandMenuReady) ready.Add("retainer command menu");
        if (observed.TalkReady) ready.Add(Talk);
        if (observed.RetainerListReady) ready.Add(RetainerList);
        if (observed.ActiveRetainerId != 0) ready.Add($"active retainer {observed.ActiveRetainerId}");
        return ready.Count == 0 ? "no known retainer surface or identity" : string.Join(", ", ready);
    }

    private static string FormatRetainerClosingObservation(RetainerClosingObservation observed)
    {
        var ready = new List<string>();
        if (observed.RetainerListReady) ready.Add(RetainerList);
        if (observed.TalkReady) ready.Add(Talk);
        if (observed.ActiveRetainerId != 0) ready.Add($"active retainer {observed.ActiveRetainerId}");
        return ready.Count == 0 ? "no known retainer surface or identity" : string.Join(", ", ready);
    }

}

internal enum RetainerOpeningAction
{
    Wait,
    AdvanceTalk,
    Complete,
    CompleteAtList,
    RejectIdentity,
}

internal readonly record struct RetainerOpeningObservation(
    bool CommandMenuReady,
    bool TalkReady,
    bool RetainerListReady,
    ulong ActiveRetainerId);

internal static class RetainerOpeningPolicy
{
    public static RetainerOpeningAction Decide(
        RetainerOpeningObservation observed,
        ulong? expectedRetainerId,
        bool allowRetainerListCompletion = false)
    {
        if (observed.CommandMenuReady)
            return RetainerOpeningAction.Complete;
        if (allowRetainerListCompletion && observed.RetainerListReady)
            return RetainerOpeningAction.CompleteAtList;
        if (!observed.TalkReady || observed.ActiveRetainerId == 0)
            return RetainerOpeningAction.Wait;
        if (expectedRetainerId is > 0 && observed.ActiveRetainerId != expectedRetainerId)
            return RetainerOpeningAction.RejectIdentity;
        return RetainerOpeningAction.AdvanceTalk;
    }
}

internal enum RetainerClosingAction
{
    Wait,
    AdvanceTalk,
    Complete,
    RejectIdentity,
}

internal readonly record struct RetainerClosingObservation(
    bool RetainerListReady,
    bool TalkReady,
    ulong ActiveRetainerId);

internal static class RetainerClosingPolicy
{
    public static RetainerClosingAction Decide(
        RetainerClosingObservation observed,
        ulong expectedRetainerId)
    {
        if (observed.RetainerListReady)
            return RetainerClosingAction.Complete;
        if (!observed.TalkReady)
            return RetainerClosingAction.Wait;
        if (observed.ActiveRetainerId != 0 && observed.ActiveRetainerId != expectedRetainerId)
            return RetainerClosingAction.RejectIdentity;
        return RetainerClosingAction.AdvanceTalk;
    }
}
