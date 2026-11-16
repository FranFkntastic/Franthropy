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
    private readonly IPluginLog log;
    private RetainerAutomationTarget? active;
    // Market price freshness proof. InventoryManager's 20-entry retainer market price table is a
    // client cache that is NOT tied to the RetainerMarket container's load: right after a retainer
    // switch it can still hold the previous retainer's (non-zero) prices. The game itself replaces
    // the table wholesale (clear all 20 entries, then write each server price record) when a price
    // batch arrives, and treats 0 as "no price". This session clears the same table on the tick it
    // selects a retainer, before the selection callback, so afterwards a non-zero price on an
    // occupied slot can only come from the game's refresh for that retainer (or from this
    // retainer's own local listing/reprice writes). Stopwatch timestamp of that clear; 0 = none.
    private long marketPriceCacheClearedAt;
    // How long a market scan waits, on framework ticks, for the price refresh to cover every listing.
    private static readonly TimeSpan MarketPriceRefreshLimit = TimeSpan.FromSeconds(5);
    // Pre-dispatch reprice selection: each attempt is one row activation. The budget covers
    // probing every sell-list row twice; only a persistent hard blocker can exhaust it.
    private const int RepriceSelectionAttemptBudget = 40;
    // Frames a not-yet-ready menu/target/label state may take to settle before the attempt fails.
    private const int RepriceReadinessSettleFrames = 12;
    // Consecutive quiet frames (sell list ready, no menu/editor/confirmation) required before activation.
    private const int RepriceStableFrames = 3;
    private const int RepriceSurfaceWaitFrames = 180;
    // Wall-clock bounds. Pre-send recovery (all attempts plus the final cleanup) never exceeds
    // RepriceRecoveryBudget: an attempt starts only if its worst case still fits. Every stage
    // below is capped in both frames and time, so low frame rates cannot stretch it.
    private static readonly TimeSpan RepriceRecoveryBudget = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan RepriceStageLimit = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RepriceReopenLimit = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RepriceSettleLimit = TimeSpan.FromSeconds(1);
    // backoff (<=0.5) + release 2 + reopen 3 + quiet 2 + menu 2 + settle 1 + direct-editor release 2, rounded up.
    private static readonly TimeSpan RepriceAttemptWorstCase = TimeSpan.FromSeconds(13);
    // Post-callback stages: editor appearance, unsent-editor abandonment, and commit observation.
    private static readonly TimeSpan RepriceEditorAppearLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RepriceAbandonLimit = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RepriceCommitObserveLimit = TimeSpan.FromSeconds(6);
    // Spacing between attempts so instantly failing attempts still span several seconds.
    private const int RepriceRetryBackoffFrames = 10;

    private enum AdjustPriceGateKind
    {
        Waiting,
        Failed,
        HardStop,
        EditorOpened,
        Dispatched,
        DispatchUnacknowledged,
    }

    private readonly record struct AdjustPriceGate(
        AdjustPriceGateKind Kind,
        int Slot,
        string Condition,
        RetainerAutomationResult? Result = null)
    {
        public static AdjustPriceGate Wait(string condition) => new(AdjustPriceGateKind.Waiting, -1, condition);

        public static AdjustPriceGate Stop(RetainerAutomationResult result) =>
            new(AdjustPriceGateKind.HardStop, -1, result.Message, result);
    }

    private readonly record struct RepriceSurfaceObservation(bool Quiet, string Description);

    private sealed record RepriceAttemptOutcome(
        int Row,
        string? Condition,
        RetainerAutomationResult? Result,
        RetainerMarketListingTarget? Listing)
    {
        public static RepriceAttemptOutcome Retry(int row, string condition) => new(row, condition, null, null);

        public static RepriceAttemptOutcome Final(RetainerAutomationResult result, RetainerMarketListingTarget? listing = null) =>
            new(-1, null, result, listing);

        public static RepriceAttemptOutcome Activated(int row) => new(row, null, null, null);
    }

    private sealed class RepriceSelectionState(RetainerMarketListingTarget listing)
    {
        private string? marketFingerprint;

        public System.Diagnostics.Stopwatch Clock { get; } = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>The listing exactly as requested (old price); used to prove an abandoned editor sent nothing.</summary>
        public RetainerMarketListingTarget Requested { get; } = listing;

        public RetainerMarketListingTarget Current { get; set; } = listing;

        /// <summary>A row activation by this reprice may have left a menu or editor open.</summary>
        public bool OwnsSurface { get; set; }

        /// <summary>Set immediately before the Adjust Price callback; nothing may be retried after it.</summary>
        public bool AdjustDispatched { get; set; }

        public List<string> Attempts { get; } = [];

        /// <summary>Sell-list rows whose Adjust Price editor showed a different listing (closed unsent).</summary>
        public HashSet<int> WrongRows { get; } = [];

        public Dictionary<int, int> RowFailures { get; } = [];

        public void RecordRowFailure(int row) => RowFailures[row] = RowFailures.GetValueOrDefault(row) + 1;

        public void ObserveMarket(string fingerprint)
        {
            if (marketFingerprint == fingerprint)
                return;
            marketFingerprint = fingerprint;
            WrongRows.Clear();
            RowFailures.Clear();
        }
    }

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

    private sealed record MarketListingRemovalDispatchResult(
        MarketListingPostDispatchOutcome Outcome,
        int PlayerVariantQuantityBefore,
        string Code,
        string Message);

    public DalamudRetainerAutomationSession(
        IFramework framework,
        IGameGui gameGui,
        IDataManager dataManager,
        IPluginLog log,
        IObjectTable objects,
        ITargetManager targets,
        ISigScanner sigScanner,
        IGameInventory? gameInventory = null)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.dataManager = dataManager;
        this.log = log;
        bell = new(objects, targets, dataManager);
        crystals = new(sigScanner, gameGui, framework, log, gameInventory);
        items = new(sigScanner, gameGui, framework, log, gameInventory);
        retrievals = new(sigScanner, gameGui, framework, log, gameInventory);
        renderedUi = new(gameGui);
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
                return RetainerAutomationResult.Failed(interaction.Code, interaction.Message);
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

    public Task<RetainerRosterResult> ScanRetainerRosterAsync(CancellationToken cancellationToken = default) =>
        framework.RunOnTick(ScanRetainerRoster, cancellationToken: cancellationToken);

    public async Task<RetainerAutomationResult> OpenRetainerAsync(RetainerAutomationTarget target, CancellationToken cancellationToken = default)
    {
        var compatibility = EvaluateUiCapability();
        if (!compatibility.IsAvailable)
            return RetainerAutomationResult.Failed(NativeCapabilityGuard.FailureCode, compatibility.Message);

        active = null;
        marketPriceCacheClearedAt = 0;
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
        var compatibility = EvaluateUiCapability();
        if (!compatibility.IsAvailable)
            return RetainerAutomationOpenResult.Failed(NativeCapabilityGuard.FailureCode, compatibility.Message);

        active = null;
        marketPriceCacheClearedAt = 0;
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
        var compatibility = EvaluateUiCapability();
        if (!compatibility.IsAvailable)
            return RetainerAutomationResult.Failed(NativeCapabilityGuard.FailureCode, compatibility.Message);

        var selected = await framework.RunOnTick(() => SelectCommand(2378), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!selected.Success)
            return selected;

        return await WaitUntilAsync(IsInventoryReady, cancellationToken).ConfigureAwait(false)
            ? RetainerAutomationResult.Succeeded("RetainerInventoryReady", "Retainer inventory opened.")
            : RetainerAutomationResult.Failed("RetainerInventoryTimeout", "Timed out waiting for retainer inventory.");
    }

    public Task<RetainerAutomationResult> OpenSellingListAsync(CancellationToken cancellationToken = default) =>
        OpenSellingListAsync(null, cancellationToken);

    private async Task<RetainerAutomationResult> OpenSellingListAsync(TimeSpan? limit, CancellationToken cancellationToken)
    {
        var compatibility = EvaluateUiCapability();
        if (!compatibility.IsAvailable)
            return RetainerAutomationResult.Failed(NativeCapabilityGuard.FailureCode, compatibility.Message);

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

        if (await WaitUntilAsync(() => IsReady(SellingList), cancellationToken, limit).ConfigureAwait(false))
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

    // Nothing reaches the game before the Adjust Price callback fires: row activation only
    // opens a local menu. Every failure before that callback is therefore recovered by
    // closing what this reprice opened, re-observing the live listing and trying again.
    // The budget only bounds a persistent hard blocker; transient UI timing cannot use it up.
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

        var state = new RepriceSelectionState(listing);
        var attempted = 0;
        for (var attempt = 1; attempt <= RepriceSelectionAttemptBudget; attempt++)
        {
            // Never start an attempt (plus the final cleanup) that could overrun the wall-clock budget.
            if (state.Clock.Elapsed + RepriceAttemptWorstCase + RepriceStageLimit > RepriceRecoveryBudget)
                break;
            attempted = attempt;
            RepriceAttemptOutcome outcome;
            try
            {
                if (attempt > 1)
                    await framework.DelayTicks(RepriceRetryBackoffFrames, cancellationToken).ConfigureAwait(false);
                outcome = await RunRepriceSelectionAttemptAsync(state, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (state.AdjustDispatched)
            {
                return (RetainerAutomationResult.Failed("RetainerSellingListingSelectionIndeterminate",
                    $"Adjust Price was dispatched; do not retry automatically. {exception.Message}"), null);
            }
            catch (OperationCanceledException)
            {
                await ReleaseOwnedRepriceSurfacesAsync(state, CancellationToken.None).ConfigureAwait(false);
                return (RetainerAutomationResult.Failed("RetainerSellingListingSelectionFailed",
                    "Reprice selection was cancelled before Adjust Price was dispatched; nothing was sent."), null);
            }
            catch (Exception exception)
            {
                outcome = RepriceAttemptOutcome.Retry(-1, $"unexpected {exception.GetType().Name}: {exception.Message}");
            }

            if (outcome.Result is { } final)
            {
                if (final.Success && attempt > 1)
                    log.Information("RepriceSelectionRecovered slot={Slot} item={Item} attempts={Attempts}: {History}",
                        state.Current.SlotIndex, listing.ItemId, attempt, string.Join(" | ", state.Attempts));
                if (!final.Success && !state.AdjustDispatched)
                    await ReleaseOwnedRepriceSurfacesAsync(state, CancellationToken.None).ConfigureAwait(false);
                return (final, final.Success ? outcome.Listing : null);
            }

            var reason = outcome.Condition ?? "unspecified pre-dispatch failure";
            state.Attempts.Add($"#{attempt} row {(outcome.Row < 0 ? "-" : outcome.Row.ToString())}: {reason}");
            log.Warning("RepriceSelectionAttemptFailed attempt={Attempt}/{Budget} slot={Slot} item={Item} row={Row}: {Condition}",
                attempt, RepriceSelectionAttemptBudget, state.Current.SlotIndex, listing.ItemId, outcome.Row, reason);
        }

        await ReleaseOwnedRepriceSurfacesAsync(state, CancellationToken.None).ConfigureAwait(false);
        var limit = attempted >= RepriceSelectionAttemptBudget
            ? $"the {RepriceSelectionAttemptBudget}-attempt budget"
            : $"the {RepriceRecoveryBudget.TotalSeconds:0}s wall-clock budget (no further attempt could finish inside it)";
        var exhausted = $"Adjust Price was never dispatched; nothing was sent. Recovery stopped at {limit} after {attempted} attempts " +
            $"in {state.Clock.Elapsed.TotalSeconds:0.0}s for item {listing.ItemId}: {string.Join(" | ", state.Attempts)}";
        log.Warning("RetainerRepriceRecoveryExhausted slot={Slot} item={Item}: {Message}", state.Current.SlotIndex, listing.ItemId, exhausted);
        return (RetainerAutomationResult.Failed("RetainerRepriceRecoveryExhausted", exhausted), null);
    }

    private async Task<RepriceAttemptOutcome> RunRepriceSelectionAttemptAsync(
        RepriceSelectionState state, CancellationToken cancellationToken)
    {
        // Recovery: close only surfaces this reprice opened, then require a quiet, stable sell list.
        await ReleaseOwnedRepriceSurfacesAsync(state, cancellationToken).ConfigureAwait(false);
        if (!await framework.RunOnTick(() => IsReady(SellingList), cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var reopened = await OpenSellingListAsync(RepriceReopenLimit, cancellationToken).ConfigureAwait(false);
            if (!reopened.Success)
                return reopened.Code == "RetainerIdentityMismatch"
                    ? RepriceAttemptOutcome.Final(reopened)
                    : RepriceAttemptOutcome.Retry(-1, $"sell list could not be reopened: {reopened.Code}: {reopened.Message}");
        }

        var stableFrames = 0;
        var surfaces = string.Empty;
        var quietClock = System.Diagnostics.Stopwatch.StartNew();
        for (var frame = 0; frame < RepriceSurfaceWaitFrames && stableFrames < RepriceStableFrames && quietClock.Elapsed < RepriceStageLimit; frame++)
        {
            var observed = await framework.RunOnTick(ObserveRepriceSurfaces, cancellationToken: cancellationToken).ConfigureAwait(false);
            surfaces = observed.Description;
            stableFrames = observed.Quiet ? stableFrames + 1 : 0;
            if (stableFrames < RepriceStableFrames)
                await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }
        if (stableFrames < RepriceStableFrames)
            return RepriceAttemptOutcome.Retry(-1,
                $"sell list did not become quiet within {quietClock.Elapsed.TotalSeconds:0.0}s ({surfaces})");

        var activation = await framework.RunOnTick(() => ActivateRepriceRow(state), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (activation.Result is not null || activation.Condition is not null)
            return activation;
        var row = activation.Row;

        if (!await WaitUntilAsync(() => IsReady("ContextMenu") || IsReady(SellingListingEditor), cancellationToken, RepriceStageLimit).ConfigureAwait(false))
        {
            state.RecordRowFailure(row);
            var after = await framework.RunOnTick(ObserveRepriceSurfaces, cancellationToken: cancellationToken).ConfigureAwait(false);
            return RepriceAttemptOutcome.Retry(row, $"row activation opened neither ContextMenu nor RetainerSell within {RepriceStageLimit.TotalSeconds:0}s ({after.Description})");
        }

        string? previousLabels = null;
        var settleClock = System.Diagnostics.Stopwatch.StartNew();
        for (var settle = 0; ; settle++)
        {
            var gate = await framework.RunOnTick(
                () => EvaluateAdjustPriceGate(state, row, ref previousLabels),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            switch (gate.Kind)
            {
                case AdjustPriceGateKind.HardStop:
                    return RepriceAttemptOutcome.Final(gate.Result!);
                case AdjustPriceGateKind.Failed:
                    state.RecordRowFailure(row);
                    return RepriceAttemptOutcome.Retry(row, gate.Condition);
                case AdjustPriceGateKind.Waiting
                    when settle < RepriceReadinessSettleFrames && settleClock.Elapsed < RepriceSettleLimit:
                    await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
                    continue;
                case AdjustPriceGateKind.Waiting:
                    state.RecordRowFailure(row);
                    return RepriceAttemptOutcome.Retry(row, $"still not ready after {settle + 1} frames / {settleClock.Elapsed.TotalSeconds:0.0}s: {gate.Condition}");
                case AdjustPriceGateKind.EditorOpened:
                {
                    // No callback was fired; the editor alone sends nothing, so a mismatch is
                    // recoverable by closing our editor without confirming it.
                    var direct = await framework.RunOnTick(() => VerifySellingEditor(state.Current), cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (direct.Success)
                        return RepriceAttemptOutcome.Final(direct, state.Current);
                    // The editor exposes no slot; a row shown to open a different listing is
                    // skipped, otherwise the failure count moves probing on to the next row.
                    state.RecordRowFailure(row);
                    var shown = await framework.RunOnTick(() => DescribeDifferentEditorListing(state.Current), cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (shown is not null)
                        state.WrongRows.Add(row);
                    await ReleaseOwnedRepriceSurfacesAsync(state, cancellationToken).ConfigureAwait(false);
                    return RepriceAttemptOutcome.Retry(row, $"row opened the editor directly, but it does not show the exact listing " +
                        $"(closed without confirming): {direct.Message}");
                }
                case AdjustPriceGateKind.DispatchUnacknowledged:
                    return await AbandonUnsentEditorAsync(state, row,
                        "the ContextMenu did not acknowledge the Adjust Price callback", cancellationToken).ConfigureAwait(false);
                case AdjustPriceGateKind.Dispatched:
                    return await CompleteAdjustPriceDispatchAsync(state, row, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Post-dispatch: Adjust Price was fired exactly once, and only the editor's Confirm can
    // send a price. The editor is the binding proof: the menu names no slot. An editor showing
    // a different listing that is then proven closed unsent is a recoverable wrong row; every
    // unproven outcome stays Indeterminate and is never retried.
    private async Task<RepriceAttemptOutcome> CompleteAdjustPriceDispatchAsync(
        RepriceSelectionState state, int row, CancellationToken cancellationToken)
    {
        if (!await WaitUntilAsync(() => IsReady(SellingListingEditor), cancellationToken, RepriceEditorAppearLimit).ConfigureAwait(false))
            return await AbandonUnsentEditorAsync(state, row,
                $"its editor did not become ready within {RepriceEditorAppearLimit.TotalSeconds:0}s", cancellationToken).ConfigureAwait(false);

        var exact = state.Current;
        var editor = await framework.RunOnTick(() => VerifySellingEditor(exact), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (editor.Success)
            return RepriceAttemptOutcome.Final(editor, exact);
        var shown = await framework.RunOnTick(() => DescribeDifferentEditorListing(exact), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (shown is null)
            return await AbandonUnsentEditorAsync(state, row, $"its editor failed verification ({editor.Message})", cancellationToken).ConfigureAwait(false);

        var reason = $"row {row} opened the editor for {shown}, not {DescribeListing(exact)}";
        var proofFailure = await CloseUnconfirmedEditorAndProveUnsentAsync(state).ConfigureAwait(false);
        if (proofFailure is not null)
            return RepriceAttemptOutcome.Final(RetainerAutomationResult.Failed("RetainerSellingListingSelectionIndeterminate",
                $"Adjust Price was dispatched once, but {reason}; it could not be proven unsent: {proofFailure}"));
        // Proven unsent: the editor was closed unconfirmed, no SelectYesno appeared, and the
        // requested listing still holds its old price. Another row may now be tried.
        state.AdjustDispatched = false;
        state.OwnsSurface = false;
        state.WrongRows.Add(row);
        state.RecordRowFailure(row);
        log.Warning("RepriceWrongRowClosedUnsent slot={Slot} item={Item} row={Row}: {Reason}",
            exact.SlotIndex, exact.ItemId, row, reason);
        return RepriceAttemptOutcome.Retry(row, $"{reason}; closed without confirming and proven unsent");
    }

    // Describes what the open RetainerSell editor shows when it is readable and is not the
    // expected listing; null when it matches or cannot be read, so an unreadable editor is
    // never classified as a different (retryable) listing.
    private unsafe string? DescribeDifferentEditorListing(RetainerMarketListingTarget expected)
    {
        var addon = gameGui.GetAddonByName<AddonRetainerSell>(SellingListingEditor, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible ||
            addon->AskingPrice == null || addon->Quantity == null || addon->ItemName == null)
            return null;
        var shownName = addon->ItemName->NodeText.ToString().Replace("\uE03C", string.Empty, StringComparison.Ordinal).Trim();
        var shownQuantity = addon->Quantity->Value;
        var shownPrice = addon->AskingPrice->Value;
        var expectedName = dataManager.GetExcelSheet<Item>().GetRow(expected.ItemId).Name.ExtractText().Trim();
        if (string.IsNullOrWhiteSpace(shownName) || shownQuantity <= 0 || shownPrice <= 0)
            return null;
        if (string.Equals(shownName, expectedName, StringComparison.Ordinal) &&
            shownQuantity == expected.Quantity && shownPrice == expected.UnitPrice)
            return null;
        return $"'{shownName}' x{shownQuantity} @ {shownPrice}";
    }

    // After the Adjust Price callback only the editor's confirm can send a price, and this
    // session has not clicked it. Close the editor/menu without confirming, then prove the
    // listing still holds its exact requested identity and old price. Proven unsent, nothing
    // reached the server, so the attempt is an ordinary pre-send failure and selection may be
    // retried within the remaining budget. Anything unproven (a confirmation appeared, a
    // surface would not close, the listing changed) stays Indeterminate and is never retried.
    private async Task<RepriceAttemptOutcome> AbandonUnsentEditorAsync(
        RepriceSelectionState state, int row, string reason, CancellationToken cancellationToken)
    {
        var proofFailure = await CloseUnconfirmedEditorAndProveUnsentAsync(state).ConfigureAwait(false);
        if (proofFailure is null)
        {
            log.Warning("RepriceEditorAbandonedUnsent slot={Slot} item={Item} row={Row}: {Reason}", state.Current.SlotIndex, state.Current.ItemId, row, reason);
            state.AdjustDispatched = false;
            state.OwnsSurface = false;
            state.RecordRowFailure(row);
            return RepriceAttemptOutcome.Retry(row,
                $"Adjust Price was dispatched once, but {reason}; the editor was closed without confirming and " +
                $"{DescribeListing(state.Requested with { SlotIndex = state.Current.SlotIndex })} is unchanged (proven unsent)");
        }
        return RepriceAttemptOutcome.Final(RetainerAutomationResult.Failed("RetainerSellingListingSelectionIndeterminate",
            $"Adjust Price was dispatched once, but {reason}; it could not be proven unsent: {proofFailure}"));
    }

    // Returns null when proven unsent, otherwise why the proof failed. Runs to completion even
    // if the caller cancels, because leaving an open editor unproven is worse than finishing.
    private async Task<string?> CloseUnconfirmedEditorAndProveUnsentAsync(RepriceSelectionState state)
    {
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var quiet = 0;
            for (var frame = 0; clock.Elapsed < RepriceAbandonLimit; frame++)
            {
                var closingFrame = frame;
                var step = await framework.RunOnTick(() =>
                {
                    if (IsReady(YesNo))
                        return "a SelectYesno confirmation is open";
                    return CloseOwnedRepriceSurface(closingFrame) ? string.Empty : "closing";
                }, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                if (step.Length > 0 && step != "closing")
                    return step;
                quiet = step.Length == 0 ? quiet + 1 : 0;
                if (quiet >= RepriceStableFrames)
                {
                    var requested = state.Requested with { SlotIndex = state.Current.SlotIndex };
                    var unchanged = await framework.RunOnTick(() => ListingStillMatches(requested), cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    return unchanged ? null : $"the live listing no longer matches {DescribeListing(requested)}";
                }
                await framework.DelayTicks(1, CancellationToken.None).ConfigureAwait(false);
            }
            return $"the editor or menu did not close within {RepriceAbandonLimit.TotalSeconds:0}s";
        }
        catch (Exception exception)
        {
            return $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    private static unsafe bool ListingStillMatches(RetainerMarketListingTarget expected)
    {
        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer(InventoryType.RetainerMarket);
        return manager != null && container != null && container->IsLoaded &&
            expected.SlotIndex >= 0 && expected.SlotIndex < container->Size &&
            MatchesMarketListing(manager, container, expected.SlotIndex, expected);
    }

    private async Task ReleaseOwnedRepriceSurfacesAsync(RepriceSelectionState state, CancellationToken cancellationToken)
    {
        if (!state.OwnsSurface || state.AdjustDispatched)
            return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var frame = 0; frame < RepriceSurfaceWaitFrames && clock.Elapsed < RepriceStageLimit; frame++)
        {
            var released = await framework.RunOnTick(() => CloseOwnedRepriceSurface(frame), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (released)
            {
                state.OwnsSurface = false;
                return;
            }
            await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }
        log.Warning("RepriceOwnedSurfaceNotClosed: the menu or editor this reprice opened did not close within {Seconds}s.", RepriceStageLimit.TotalSeconds);
    }

    // Returns true once neither surface this reprice may have opened is still visible. A
    // SelectYesno is never ours here (no price was confirmed), so it is left untouched.
    private unsafe bool CloseOwnedRepriceSurface(int frame)
    {
        var menu = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        if (menu != null && menu->IsVisible)
        {
            // -1 is the context menu's own cancel callback (no action is selected); fall
            // back to a plain close if the menu ignores it.
            if (frame % 10 == 0 && menu->IsReady)
                menu->FireCallbackInt(-1);
            else if (frame % 10 == 5 && menu->IsReady)
                menu->Close(true);
            return false;
        }
        var editor = gameGui.GetAddonByName<AtkUnitBase>(SellingListingEditor, 1);
        if (editor != null && editor->IsVisible)
        {
            if (frame % 10 == 0 && editor->IsReady)
                editor->Close(true);
            return false;
        }
        return true;
    }

    private RepriceSurfaceObservation ObserveRepriceSurfaces()
    {
        var sellList = IsReady(SellingList);
        var menu = IsReady("ContextMenu");
        var editor = IsReady(SellingListingEditor);
        var yesNo = IsReady(YesNo);
        return new(sellList && !menu && !editor && !yesNo,
            $"RetainerSellList={Flag(sellList)} ContextMenu={Flag(menu)} RetainerSell={Flag(editor)} SelectYesno={Flag(yesNo)}");
    }

    private static string Flag(bool ready) => ready ? "ready" : "absent";

    private unsafe RepriceAttemptOutcome ActivateRepriceRow(RepriceSelectionState state)
    {
        var verified = VerifyActive(active?.RetainerId ?? 0);
        if (!verified.Success)
            return RepriceAttemptOutcome.Final(verified);
        var surfaces = ObserveRepriceSurfaces();
        if (!surfaces.Quiet)
            return RepriceAttemptOutcome.Retry(-1, $"surfaces changed before row activation ({surfaces.Description})");

        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer(InventoryType.RetainerMarket);
        if (manager == null || container == null || !container->IsLoaded)
            return RepriceAttemptOutcome.Retry(-1, "the RetainerMarket container is not loaded");
        // Binding compares live prices, so it requires the same freshness proof as a scan. Nothing
        // has been dispatched yet, so failing here sends nothing.
        var priceGap = marketPriceCacheClearedAt == 0
            ? "the client price cache was not cleared when this retainer was selected"
            : DescribeMarketPriceRefreshGap(manager, container);
        if (priceGap is not null)
        {
            var message = $"Reprice refused before any row activation: market prices for {DescribeActiveRetainer()} are not " +
                $"proven current ({DescribeSinceMarketPriceClear()}): {priceGap}. Nothing was dispatched.";
            log.Warning("RetainerMarketPricesNotRefreshed: {Message}", message);
            return RepriceAttemptOutcome.Final(RetainerAutomationResult.Failed("RetainerMarketPricesNotRefreshed", message));
        }

        // Identical listings (item, quantity, quality, price) are interchangeable for a reprice,
        // so the request follows its identity if the exact slot moved or was already served.
        var slot = ResolveListingSlotByIdentity(manager, container, state.Current);
        if (slot < 0)
            return RepriceAttemptOutcome.Final(RetainerAutomationResult.Failed("RetainerMarketListingChanged",
                $"No live retainer listing matches {DescribeListing(state.Current)}; nothing was dispatched."));
        if (slot != state.Current.SlotIndex)
        {
            log.Information("RepriceListingFollowedIdentity item={Item} from slot={From} to slot={To}",
                state.Current.ItemId, state.Current.SlotIndex, slot);
            state.Current = state.Current with { SlotIndex = slot };
        }

        var occupied = new List<int>();
        var fingerprint = new System.Text.StringBuilder();
        for (var index = 0; index < container->Size; index++)
        {
            var item = container->GetInventorySlot(index);
            if (item == null || item->ItemId == 0 || item->Quantity == 0)
                continue;
            occupied.Add(index);
            fingerprint.Append(index).Append(':').Append(item->ItemId).Append('x').Append(item->Quantity)
                .Append(item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) ? "hq" : "nq")
                .Append('@').Append(manager->GetRetainerMarketPrice(checked((short)index))).Append(';');
        }
        state.ObserveMarket(fingerprint.ToString());

        var row = ChooseRepriceRow(state, occupied);
        var activated = renderedUi.TryActivateListRowIndex(SellingList, row);
        if (!activated.Success)
        {
            state.RecordRowFailure(row);
            return RepriceAttemptOutcome.Retry(row, $"row activation refused before dispatch: {activated.Code}: {activated.Message}");
        }
        state.OwnsSurface = true;
        return RepriceAttemptOutcome.Activated(row);
    }

    // The sell list's row order is not proven to equal container slot order, so rows are
    // probed: the compacted rank and raw slot of the requested listing first, then every
    // other row. A row whose editor showed a different listing is skipped until the market
    // contents change.
    private static int ChooseRepriceRow(RepriceSelectionState state, IReadOnlyList<int> occupied)
    {
        var rowLimit = occupied.Count == 0 ? 0 : occupied[^1] + 1;
        var candidates = new List<int>();
        var rank = -1;
        for (var index = 0; index < occupied.Count; index++)
            if (occupied[index] == state.Current.SlotIndex)
                rank = index;
        if (rank >= 0)
            candidates.Add(rank);
        candidates.Add(state.Current.SlotIndex);
        for (var row = 0; row < rowLimit; row++)
            candidates.Add(row);

        var choice = candidates
            .Distinct()
            .Where(row => !state.WrongRows.Contains(row))
            .OrderBy(row => state.RowFailures.GetValueOrDefault(row))
            .Select(row => (int?)row)
            .FirstOrDefault();
        if (choice is { } chosen)
            return chosen;
        state.WrongRows.Clear();
        return rank >= 0 ? rank : state.Current.SlotIndex;
    }

    private static unsafe int ResolveListingSlotByIdentity(
        InventoryManager* manager, InventoryContainer* container, RetainerMarketListingTarget expected)
    {
        if (expected.SlotIndex >= 0 && expected.SlotIndex < container->Size &&
            MatchesMarketListing(manager, container, expected.SlotIndex, expected))
            return expected.SlotIndex;
        for (var index = 0; index < container->Size; index++)
            if (MatchesMarketListing(manager, container, index, expected))
                return index;
        return -1;
    }

    // One tick of the Adjust Price gate. Each refusal names the exact condition and its
    // observed values. The callback fires at most once, only after every check passes on
    // the same tick, and AdjustDispatched is set before it so no later path can resend.
    //
    // The sell list's row menu is opened through AgentContext (Dalamud classifies it as the
    // Default menu type), not AgentInventoryContext, and AgentContext records no inventory
    // target. Ownership is therefore proven by the RetainerSellList addon owning both the
    // agent's menu and the visible ContextMenu addon; which listing the row named is proven
    // after dispatch by the RetainerSell editor, which sends nothing until it is confirmed.
    private unsafe AdjustPriceGate EvaluateAdjustPriceGate(RepriceSelectionState state, int row, ref string? previousLabels)
    {
        var verified = VerifyActive(active?.RetainerId ?? 0);
        if (!verified.Success)
            return AdjustPriceGate.Stop(verified);
        var menuReady = IsReady("ContextMenu");
        var editorReady = IsReady(SellingListingEditor);
        if (editorReady && !menuReady)
            return new(AdjustPriceGateKind.EditorOpened, -1, "editor opened directly");
        if (!IsReady(SellingList))
            return AdjustPriceGate.Wait("RetainerSellList is not ready");
        if (!menuReady)
            return AdjustPriceGate.Wait("ContextMenu is not ready");
        if (editorReady)
            return AdjustPriceGate.Wait("ContextMenu and RetainerSell are both open");
        if (IsReady(YesNo))
            return AdjustPriceGate.Wait("an unowned SelectYesno is open");
        var sellList = gameGui.GetAddonByName<AtkUnitBase>(SellingList, 1);
        var menu = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        if (sellList == null || menu == null)
            return AdjustPriceGate.Wait("the RetainerSellList or ContextMenu addon pointer is unavailable");
        var agent = AgentContext.Instance();
        if (agent == null)
            return AdjustPriceGate.Wait("AgentContext is unavailable");
        var context = agent->CurrentContextMenu;
        var ownership = $"row {row}, expected slot {state.Current.SlotIndex}, RetainerSellList addon id {sellList->Id}, " +
            $"AgentContext owner addon {agent->OwnerAddon}, ContextMenu blocked parent {menu->BlockedParentId}, " +
            $"AgentContext active {agent->IsAgentActive()}, UpdateCheckerParam {agent->UpdateCheckerParam}";
        // BlockedParentId is the same field Dalamud uses to name a menu's parent addon (it
        // logged RetainerSellList for these menus live). OwnerAddon has not been observed
        // live, so it is recorded as evidence only.
        if (menu->BlockedParentId != sellList->Id)
            return AdjustPriceGate.Wait($"the open menu is not owned by RetainerSellList ({ownership})");
        if (context == null)
            return AdjustPriceGate.Wait($"AgentContext has no current context menu ({ownership})");

        // Addon 959 is the localized market-list context action, not a guessed menu position.
        var label = ResolveAddonText(959);
        if (string.IsNullOrWhiteSpace(label))
            return new(AdjustPriceGateKind.Failed, -1, "Addon 959 (Adjust Price) has no localized text");
        if (!TryReadAgentContextLabels(context, out var labels, out var layout))
            return AdjustPriceGate.Wait($"menu labels unreadable: {layout} ({ownership})");
        var mask = context->ContextItemDisabledMask;
        var described = $"labels [{string.Join(", ", labels)}], {layout}, disabled mask 0x{mask:X}, {ownership}";
        var match = DalamudContextMenuOptionParser.Find(labels,
            new("Adjust Price", new HashSet<string>(StringComparer.Ordinal) { label }));
        if (!match.Success || match.Index < 0 || match.Index >= labels.Count)
            return AdjustPriceGate.Wait($"menu does not offer exactly one '{label}' ({described})");
        if (match.Index < 32 && (mask & (1u << match.Index)) != 0)
            return AdjustPriceGate.Wait($"'{label}' at index {match.Index} is disabled ({described})");
        var fingerprint = string.Join("\u001f", labels);
        if (previousLabels != fingerprint)
        {
            var changed = previousLabels is not null;
            previousLabels = fingerprint;
            return AdjustPriceGate.Wait(changed ? "context labels changed; waiting for them to settle" : "waiting one frame for stable context labels");
        }

        // AgentContext names no slot. UpdateCheckerParam may be the activated list index; it
        // is recorded with the row and expected slot so live runs can establish its meaning,
        // but it is not trusted as a binding. The editor verification after dispatch is.
        log.Information("RepriceAdjustPriceDispatch item={Item} index={Index}: {Evidence}",
            state.Current.ItemId, match.Index, described);
        // The index is the agent's native menu index: the callback is fired on the addon
        // directly, below Dalamud's rendered-row remapping of injected entries.
        var values = stackalloc AtkValue[5];
        for (var i = 0; i < 5; i++) values[i] = new() { Type = AtkValueType.Int, Int = i == 1 ? match.Index : 0 };
        state.AdjustDispatched = true;
        return menu->FireCallback(5, values, true)
            ? new(AdjustPriceGateKind.Dispatched, state.Current.SlotIndex, "Adjust Price dispatched")
            : new(AdjustPriceGateKind.DispatchUnacknowledged, state.Current.SlotIndex, "Adjust Price callback was not acknowledged");
    }

    // AgentContext's label slice is not AgentInventoryContext's (which records its own start
    // index). ClientStructs documents no start for it: Dalamud reads the item count from
    // value 0, places addon names after an 8-value header, and reads event ids/handlers from
    // index 7. Neither offset is assumed: the slice is accepted only when exactly one of the
    // two candidate starts holds a run of exactly `count` strings, and the layout is reported.
    private static unsafe bool TryReadAgentContextLabels(
        FFXIVClientStructs.FFXIV.Client.UI.Agent.ContextMenu* context, out List<string> labels, out string layout)
    {
        labels = [];
        var values = context->EventParams;
        var header = values[0];
        var count = header.Type switch
        {
            AtkValueType.UInt => (int)header.UInt,
            AtkValueType.Int => header.Int,
            _ => -1,
        };
        var dump = new System.Text.StringBuilder();
        for (var index = 0; index < Math.Min(values.Length, 8 + Math.Max(count, 0) + 1); index++)
        {
            var value = values[index];
            dump.Append(index == 0 ? string.Empty : " ").Append('[').Append(index).Append(']').Append(value.Type);
            if (IsMenuText(value))
                dump.Append("=\"").Append(value.GetValueAsString()).Append('"');
            else if (value.Type is AtkValueType.UInt)
                dump.Append('=').Append(value.UInt);
            else if (value.Type is AtkValueType.Int)
                dump.Append('=').Append(value.Int);
        }
        if (count <= 0 || count > 32)
        {
            layout = $"item count {count} is invalid; values {dump}";
            return false;
        }
        var starts = new List<int>();
        foreach (var start in new[] { 7, 8 })
        {
            if (start + count > values.Length || IsMenuText(values[start - 1]))
                continue;
            var run = true;
            for (var index = start; index < start + count && run; index++)
                run = IsMenuText(values[index]);
            if (run && (start + count == values.Length || !IsMenuText(values[start + count])))
                starts.Add(start);
        }
        if (starts.Count != 1)
        {
            layout = $"{starts.Count} candidate label slices for item count {count}; values {dump}";
            return false;
        }
        for (var index = starts[0]; index < starts[0] + count; index++)
            labels.Add(values[index].GetValueAsString());
        layout = $"label slice {starts[0]}..{starts[0] + count - 1}";
        return true;
    }

    private static bool IsMenuText(AtkValue value) =>
        value.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.WideString or AtkValueType.ConstString;

    private static string DescribeListing(RetainerMarketListingTarget listing) =>
        $"slot {listing.SlotIndex} item {listing.ItemId}{(listing.IsHq ? " HQ" : string.Empty)} x{listing.Quantity} @ {listing.UnitPrice}";

    private unsafe RetainerAutomationResult VerifySellingEditor(RetainerMarketListingTarget listing)
    {
        try
        {
            _ = RequireSellingEditor(listing, listing.UnitPrice!.Value);
            return RetainerAutomationResult.Succeeded("RetainerSellingListingReady", "Opened the exact verified retainer listing editor.");
        }
        catch (NativeCapabilityUnavailableException exception)
        {
            return RetainerAutomationResult.Failed("RetainerSellingListingSelectionFailed", exception.Message);
        }
    }

    /// <remarks>
    /// Waits on framework ticks, up to <see cref="MarketPriceRefreshLimit"/>, until the game's price
    /// refresh covers every occupied listing of the active retainer. Never returns unproven prices.
    /// </remarks>
    public async Task<RetainerMarketListingScanResult> ScanMarketListingsAsync(CancellationToken cancellationToken = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var attempt = await framework.RunOnTick(ScanMarketListings, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (attempt.Result is { } result)
                return result;
            if (clock.Elapsed >= MarketPriceRefreshLimit)
            {
                var message = $"Retainer market prices for {DescribeActiveRetainer()} were not refreshed within the " +
                    $"{MarketPriceRefreshLimit.TotalSeconds:0}s limit (waited {clock.Elapsed.TotalSeconds:0.0}s; " +
                    $"{DescribeSinceMarketPriceClear()}): {attempt.MissingEvidence}. No prices were returned.";
                log.Warning("RetainerMarketPricesNotRefreshed: {Message}", message);
                return RetainerMarketListingScanResult.Failed("RetainerMarketPricesNotRefreshed", message);
            }
            await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }
    }

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
            () => SetSellingListingPrice(opened.Listing, newUnitPrice),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!updated.Result.Success)
        {
            // Confirm was never clicked; close the editor so it cannot block the next reprice.
            var unsent = await CloseUnconfirmedEditorAndProveUnsentAsync(new RepriceSelectionState(opened.Listing)).ConfigureAwait(false);
            if (unsent is not null)
                log.Warning("RepricePriceEntryEditorNotProvenClosed slot={Slot} item={Item}: {Reason}",
                    opened.Listing.SlotIndex, opened.Listing.ItemId, unsent);
            return updated.Result;
        }

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
                () => ConfirmSellingListingPrice(opened.Listing, newUnitPrice, updated.Editor,
                    () => mutationMayHaveBeenSent = true),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!confirmed.Success)
                return confirmed;

            var commitClock = System.Diagnostics.Stopwatch.StartNew();
            for (var attempt = 0; attempt < 180 && commitClock.Elapsed < RepriceCommitObserveLimit; attempt++)
            {
                var observed = await framework.RunOnTick(
                    () => ObserveSellingListingPriceCommit(expected),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var action = RetainerMarketPriceUpdatePolicy.Decide(
                    observed.Committed,
                    observed.YesNoReady);
                if (action == RetainerMarketPriceUpdateAction.Complete)
                {
                    return RetainerAutomationResult.Succeeded(
                        "RetainerMarketPriceUpdated",
                        "The exact live retainer listing committed the requested unit price.");
                }
                if (action == RetainerMarketPriceUpdateAction.RejectUnexpectedConfirmation)
                {
                    return RetainerAutomationResult.Failed(
                        "UnexpectedRetainerMarketConfirmation",
                        "The price request was sent, but an unowned confirmation dialog appeared before the exact listing committed. It was not accepted or dismissed; re-scan before retrying.");
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
        var compatibility = EvaluateUiCapability();
        if (!compatibility.IsAvailable)
            return RetainerMarketListingPostResult.Failed(NativeCapabilityGuard.FailureCode, compatibility.Message);

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

    public async Task<RetainerMarketListingRemovalResult> RemoveMarketListingToPlayerInventoryAsync(
        RetainerMarketListingTarget listing,
        CancellationToken cancellationToken = default)
    {
        var compatibility = EvaluateUiCapability();
        if (!compatibility.IsAvailable)
            return RetainerMarketListingRemovalResult.Failed(listing, NativeCapabilityGuard.FailureCode, compatibility.Message);

        var verified = await framework.RunOnTick(
            () => VerifyActive(active?.RetainerId ?? 0),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!verified.Success)
            return RetainerMarketListingRemovalResult.Failed(listing, verified.Code, verified.Message);

        var requestMayHaveBeenSent = false;
        try
        {
            var started = await framework.RunOnTick(
                () => StartMarketListingRemoval(listing, () => requestMayHaveBeenSent = true),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (started.Outcome == MarketListingPostDispatchOutcome.FailedBeforeSend)
                return RetainerMarketListingRemovalResult.Failed(listing, started.Code, started.Message);
            if (started.Outcome == MarketListingPostDispatchOutcome.Indeterminate)
                return RetainerMarketListingRemovalResult.Indeterminate(listing, started.Code, started.Message);

            for (var attempt = 0; attempt < 180; attempt++)
            {
                var committed = await framework.RunOnTick(
                    () => ObserveMarketListingRemoval(listing, started.PlayerVariantQuantityBefore),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (committed)
                    return RetainerMarketListingRemovalResult.Succeeded(listing);

                await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
            }

            return RetainerMarketListingRemovalResult.Indeterminate(
                listing,
                "RetainerMarketListingRemovalIndeterminate",
                "The removal request was sent, but its exact listing removal and player-inventory increment were not observed. Re-scan before retrying.");
        }
        catch (OperationCanceledException exception) when (requestMayHaveBeenSent)
        {
            throw new RetainerMarketMutationIndeterminateException(
                "RetainerMarketListingRemovalCancelledIndeterminate",
                "Cancellation occurred after the removal request may have been sent. Re-scan before deciding whether to retry.",
                listing,
                exception,
                cancellationToken);
        }
        catch (Exception exception) when (requestMayHaveBeenSent)
        {
            return RetainerMarketListingRemovalResult.Indeterminate(
                listing,
                "RetainerMarketListingRemovalIndeterminate",
                $"The removal request may have been sent before an observation fault: {exception.Message} Re-scan before retrying.");
        }
    }

    public Task<IReadOnlyList<DalamudInventoryStack>> ScanRetainerAsync(IReadOnlySet<uint> itemIds, CancellationToken cancellationToken = default) =>
        framework.RunOnTick(() => ScanVerifiedRetainer(itemIds), cancellationToken: cancellationToken);

    private unsafe IReadOnlyList<DalamudInventoryStack> ScanVerifiedRetainer(IReadOnlySet<uint> itemIds)
    {
        var verified = VerifyActive(active?.RetainerId ?? 0);
        if (!verified.Success)
            throw new InvalidOperationException(verified.Message);
        var manager = InventoryManager.Instance();
        var required = itemIds.Count == 0 ? DalamudRetainerInventory.ItemContainers :
            itemIds.All(ElementalCurrencyCatalog.IsElementalCurrency) ? [InventoryType.RetainerCrystals] :
            itemIds.Any(ElementalCurrencyCatalog.IsElementalCurrency) ? DalamudRetainerInventory.ItemContainers :
            DalamudRetainerInventory.OrdinaryItemContainers;
        foreach (var type in required)
        {
            var container = manager == null ? null : manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                throw new InvalidOperationException($"Retainer inventory {type} is not loaded; no empty observation was published.");
        }
        return DalamudRetainerInventory.ScanLoadedStacks(itemIds);
    }

    public async Task<RetainerRetrievalResult> RetrieveAsync(
        DalamudInventoryStack stack,
        int quantity,
        CancellationToken cancellationToken = default) =>
        await RetrieveAsync(stack, quantity, null, cancellationToken).ConfigureAwait(false);

    public async Task<RetainerRetrievalResult> RetrieveAsync(
        DalamudInventoryStack stack,
        int quantity,
        int retainerVariantQuantityBefore,
        CancellationToken cancellationToken = default) =>
        await RetrieveAsync(stack, quantity, (int?)retainerVariantQuantityBefore, cancellationToken).ConfigureAwait(false);

    private async Task<RetainerRetrievalResult> RetrieveAsync(
        DalamudInventoryStack stack,
        int quantity,
        int? retainerVariantQuantityBefore,
        CancellationToken cancellationToken)
    {
        var verified = await framework.RunOnTick(() => VerifyActive(active?.RetainerId ?? 0), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!verified.Success)
            return new(false, 0, "RetainerIdentityMismatch", verified.Message);

        return retainerVariantQuantityBefore is { } knownQuantity
            ? await retrievals.RetrieveAsync(stack, quantity, knownQuantity, cancellationToken).ConfigureAwait(false)
            : await retrievals.RetrieveAsync(stack, quantity, cancellationToken).ConfigureAwait(false);
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
        if (state.ConfirmationReady)
        {
            return RetainerAutomationResult.Failed(
                "RetainerConfirmationOwnershipUnknown",
                "A confirmation dialog is visible, but this session cannot prove that it owns the dialog. It was left untouched.");
        }
        if (state.RetainerListReady)
        {
            active = null;
            return RetainerAutomationResult.Succeeded("RetainerListReady", "Retainer list is ready.");
        }

        await framework.RunOnTick(
            () =>
            {
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
        foreach (var addonName in new[] { "InputNumeric", "ContextMenu", SellingListingEditor, SellingList, MarketList, SelectString })
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
            if (addon is not null && addon->IsReady && addon->IsVisible)
                addon->Close(true);
        }

        active = null;
    }

    private static (bool IsAvailable, string Message) EvaluateUiCapability()
    {
        try
        {
            NativeCapabilityGuard.RequireAddress(AtkUnitBase.Addresses.FireCallback.Value, "Retainer UI callbacks");
            NativeCapabilityGuard.RequireAddress(AgentModule.Addresses.GetAgentByInternalId.Value, "Retainer agent lookup");
            return (true, "Retainer UI entry points are available.");
        }
        catch (NativeCapabilityUnavailableException exception)
        {
            return (false, exception.Message);
        }
    }

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

    // Waits up to 180 frames, and additionally at most limit of wall-clock time when given.
    private async Task<bool> WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken, TimeSpan? limit = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var attempt = 0; attempt < 180; attempt++)
        {
            if (await framework.RunOnTick(predicate, cancellationToken: cancellationToken).ConfigureAwait(false))
                return true;
            if (limit is { } bound && clock.Elapsed >= bound)
                return false;
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

        var activated = FireRetainerListRow(addon, entries, selectedIndex.Value);
        if (!activated.Success)
            return activated;
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

        var name = entries[selectedIndex.Value].Name;
        var activated = FireRetainerListRow(addon, entries, selectedIndex.Value);
        if (!activated.Success)
            return (activated, null);
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

    private static unsafe RetainerRosterResult ScanRetainerRoster()
    {
        var manager = RetainerManager.Instance();
        if (manager is null)
            return RetainerRosterResult.Failed("RetainerManagerUnavailable", "The live retainer manager is unavailable.");
        if (!manager->IsReady)
            return RetainerRosterResult.Failed("RetainerRosterNotReady", "The assigned retainer roster is not ready.");

        var count = manager->GetRetainerCount();
        var retainers = new List<RetainerRosterEntry>(count);
        for (uint displayOrder = 0; displayOrder < count; displayOrder++)
        {
            var retainer = manager->GetRetainerBySortedIndex(displayOrder);
            if (retainer is null || retainer->RetainerId == 0)
                return RetainerRosterResult.Failed(
                    "RetainerRosterIncomplete",
                    $"Assigned retainer {displayOrder + 1} of {count} did not expose a stable identity.");

            retainers.Add(new(
                retainer->RetainerId,
                retainer->NameString,
                checked((int)displayOrder),
                null,
                retainer->ClassJob,
                retainer->Level,
                retainer->MarketItemCount,
                retainer->Available));
        }

        return RetainerRosterResult.Succeeded(retainers);
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
        // FindEntry resolved the index from this addon's live entry table on this tick, so the
        // callback index is bound to the verified label. The game's SelectString handler takes
        // the zero-based entry index as its single Int callback value.
        addon->AtkUnitBase.FireCallbackInt(index);
        return RetainerAutomationResult.Succeeded("RetainerCommandSelected",
            $"Selected retainer command '{addon->PopupMenu.PopupMenu.EntryNames[index]}' at index {index}.");
    }

    // The retainer list's own selection callback is (Int 2, UInt rowIndex). Synthetic
    // ListItemClick/DoubleClick dispatch reaches list listeners but does not open the retainer
    // (verified live 2026-11-06: RetainerMenuTimeout with RetainerList still showing).
    private unsafe RetainerAutomationResult FireRetainerListRow(
        AtkUnitBase* addon, IReadOnlyList<RetainerListEntry> entries, int index)
    {
        if (index < 0 || index >= entries.Count || !entries[index].IsActive || string.IsNullOrWhiteSpace(entries[index].Name))
            return RetainerAutomationResult.Failed("RetainerNotVisible", "The verified retainer row changed before selection.");

        // Same 4-value shape the pre-3a054f9 implementation used successfully in production.
        var values = stackalloc AtkValue[4];
        values[0] = new() { Type = AtkValueType.Int, Int = 2 };
        values[1] = new() { Type = AtkValueType.UInt, UInt = (uint)index };
        // Same tick, before the callback: no price batch for the newly selected retainer can be
        // applied in between, so every later non-zero price belongs to the selected retainer.
        InvalidateRetainerMarketPriceCache(entries[index].Name);
        addon->FireCallback(4, values, true);
        return RetainerAutomationResult.Succeeded("RetainerListRowSelected",
            $"Selected retainer row {index} ({entries[index].Name}) through the list's native callback.");
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

    private readonly record struct MarketListingScanAttempt(RetainerMarketListingScanResult? Result, string? MissingEvidence);

    // One framework-tick observation. A null Result means the price refresh has not yet covered
    // every occupied listing; the caller waits and retries within MarketPriceRefreshLimit.
    private unsafe MarketListingScanAttempt ScanMarketListings()
    {
        var verified = VerifyActive(active?.RetainerId ?? 0);
        if (!verified.Success)
            return new(RetainerMarketListingScanResult.Failed(verified.Code, verified.Message), null);
        if (marketPriceCacheClearedAt == 0)
            return new(RetainerMarketListingScanResult.Failed(
                "RetainerMarketPriceFreshnessUnproven",
                $"Market prices for {DescribeActiveRetainer()} cannot be proven current: this session did not clear the " +
                "client's retainer market price cache when the retainer was selected, so the cache may still hold another " +
                "retainer's prices. No prices were returned."), null);

        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer(InventoryType.RetainerMarket);
        if (container == null || !container->IsLoaded)
            return new(RetainerMarketListingScanResult.Failed(
                "RetainerMarketUnavailable",
                "The live retainer market inventory is unavailable."), null);

        var missing = DescribeMarketPriceRefreshGap(manager, container);
        if (missing is not null)
            return new(null, missing);

        var listings = new List<RetainerMarketListingTarget>();
        for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
        {
            var slot = container->GetInventorySlot(slotIndex);
            if (slot == null || slot->ItemId == 0 || slot->Quantity == 0)
                continue;

            var unitPrice = manager->GetRetainerMarketPrice(checked((short)slotIndex));
            if (unitPrice is 0 or > RetainerMarketPricePolicy.MaximumUnitPrice)
                return new(RetainerMarketListingScanResult.Failed(
                    "RetainerMarketPriceInvalid",
                    $"The live listing in slot {slotIndex} has an invalid unit price."), null);

            listings.Add(new(
                slotIndex,
                slot->ItemId,
                slot->Quantity,
                slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality),
                checked((uint)unitPrice)));
        }

        return new(RetainerMarketListingScanResult.Succeeded(listings), null);
    }

    // Clears only InventoryManager's client-side retainer market price table, exactly as the
    // game's own price-batch apply and reset paths do. Never use SetRetainerMarketPrice here:
    // that function also sends a price-change command to the server.
    private unsafe void InvalidateRetainerMarketPriceCache(string retainerName)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            log.Warning("RetainerMarketPriceCacheNotCleared: InventoryManager was unavailable when {Retainer} was selected; " +
                "market scans and reprices for it will fail as unproven.", retainerName);
            return;
        }
        manager->RetainerMarketPrices.Clear();
        marketPriceCacheClearedAt = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    // Null when every occupied listing has a price written since the selection cleared the cache;
    // otherwise names the slots still lacking that evidence.
    private static unsafe string? DescribeMarketPriceRefreshGap(InventoryManager* manager, InventoryContainer* container)
    {
        List<int>? unpriced = null;
        var occupied = 0;
        for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
        {
            var slot = container->GetInventorySlot(slotIndex);
            if (slot == null || slot->ItemId == 0 || slot->Quantity == 0)
                continue;
            occupied++;
            if (manager->GetRetainerMarketPrice(checked((short)slotIndex)) == 0)
                (unpriced ??= []).Add(slotIndex);
        }
        return unpriced is null
            ? null
            : $"{unpriced.Count} of {occupied} occupied listing slot(s) [{string.Join(", ", unpriced)}] have had no price " +
              "written by the game since the selection cleared the client price cache";
    }

    private string DescribeActiveRetainer() =>
        active is { } target ? $"{target.RetainerName} ({target.RetainerId})" : "no verified active retainer";

    private string DescribeSinceMarketPriceClear() =>
        marketPriceCacheClearedAt == 0
            ? "the price cache was never cleared for this retainer"
            : $"{System.Diagnostics.Stopwatch.GetElapsedTime(marketPriceCacheClearedAt).TotalSeconds:0.0}s since selection cleared the price cache";

    private unsafe MarketListingPostDispatchResult StartMarketListingPost(
        DalamudInventoryStack source,
        int quantity,
        uint unitPrice,
        System.Action markDispatchStarted)
    {
        if (InventoryManager.Addresses.MoveToRetainerMarket.Value == 0)
            return new(MarketListingPostDispatchOutcome.FailedBeforeSend, null, 0,
                NativeCapabilityGuard.FailureCode,
                "The market listing entry point is unresolved; no request was sent.");

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
        var dispatchStarted = false;
        try
        {
            var sourceIndex = checked((ushort)source.SlotIndex);
            var destinationIndex = checked((ushort)marketSlotIndex);
            var nativeQuantity = checked((uint)quantity);
            markDispatchStarted();
            dispatchStarted = true;
            manager->MoveToRetainerMarket(
                source.Container,
                sourceIndex,
                InventoryType.RetainerMarket,
                destinationIndex,
                nativeQuantity,
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
                dispatchStarted ? MarketListingPostDispatchOutcome.Indeterminate : MarketListingPostDispatchOutcome.FailedBeforeSend,
                expected,
                source.Quantity,
                dispatchStarted ? "RetainerMarketListingPostDispatchIndeterminate" : "RetainerMarketListingPostNotSent",
                dispatchStarted
                    ? $"The listing call faulted after dispatch began: {exception.Message} Re-scan before retrying."
                    : $"The listing request could not be prepared: {exception.Message} No request was sent.");
        }
    }

    private unsafe MarketListingRemovalDispatchResult StartMarketListingRemoval(
        RetainerMarketListingTarget listing,
        System.Action markDispatchStarted)
    {
        if (InventoryManager.Addresses.MoveFromRetainerMarketToPlayerInventory.Value == 0)
            return new(MarketListingPostDispatchOutcome.FailedBeforeSend, 0,
                NativeCapabilityGuard.FailureCode,
                "The market listing removal entry point is unresolved; no request was sent.");

        if (listing.Quantity <= 0 || listing.UnitPrice is not > 0 or > RetainerMarketPricePolicy.MaximumUnitPrice)
        {
            return new(
                MarketListingPostDispatchOutcome.FailedBeforeSend,
                0,
                "InvalidObservedMarketListing",
                "Removal requires an exact observed listing with positive quantity and valid unit price.");
        }

        var manager = InventoryManager.Instance();
        var marketContainer = manager == null ? null : manager->GetInventoryContainer(InventoryType.RetainerMarket);
        if (manager == null || marketContainer == null || !marketContainer->IsLoaded)
        {
            return new(
                MarketListingPostDispatchOutcome.FailedBeforeSend,
                0,
                "RetainerMarketInventoryUnavailable",
                "The live retainer market inventory is unavailable.");
        }
        if (listing.SlotIndex < 0 || listing.SlotIndex >= marketContainer->Size ||
            !MatchesMarketListing(manager, marketContainer, listing.SlotIndex, listing))
        {
            return new(
                MarketListingPostDispatchOutcome.FailedBeforeSend,
                0,
                "RetainerMarketListingChanged",
                "The exact observed retainer market listing changed before removal.");
        }

        var playerQuantityBefore = CountPlayerVariant(listing.ItemId, listing.IsHq);
        var dispatchStarted = false;
        try
        {
            var sourceIndex = checked((ushort)listing.SlotIndex);
            var nativeQuantity = checked((uint)listing.Quantity);
            markDispatchStarted();
            dispatchStarted = true;
            manager->MoveFromRetainerMarketToPlayerInventory(
                InventoryType.RetainerMarket,
                sourceIndex,
                nativeQuantity);
            return new(
                MarketListingPostDispatchOutcome.Sent,
                playerQuantityBefore,
                "RetainerMarketListingRemovalSent",
                "The exact listing removal was sent once; awaiting its live postcondition.");
        }
        catch (Exception exception)
        {
            return new(
                dispatchStarted ? MarketListingPostDispatchOutcome.Indeterminate : MarketListingPostDispatchOutcome.FailedBeforeSend,
                playerQuantityBefore,
                dispatchStarted ? "RetainerMarketListingRemovalDispatchIndeterminate" : "RetainerMarketListingRemovalNotSent",
                dispatchStarted
                    ? $"The removal call faulted after dispatch began: {exception.Message} Re-scan before retrying."
                    : $"The removal request could not be prepared: {exception.Message} No request was sent.");
        }
    }

    private static unsafe bool ObserveMarketListingRemoval(
        RetainerMarketListingTarget expected,
        int playerQuantityBefore)
    {
        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer(InventoryType.RetainerMarket);
        if (manager == null || container == null || !container->IsLoaded ||
            expected.SlotIndex < 0 || expected.SlotIndex >= container->Size)
            return false;

        var slot = container->GetInventorySlot(expected.SlotIndex);
        return RetainerMarketListingRemovalObservation.Matches(
            expected,
            slot == null ? 0 : slot->ItemId,
            slot == null ? 0 : checked((int)slot->Quantity),
            slot != null && slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality),
            manager->GetRetainerMarketPrice(checked((short)expected.SlotIndex)),
            playerQuantityBefore,
            CountPlayerVariant(expected.ItemId, expected.IsHq));
    }

    private static int CountPlayerVariant(uint itemId, bool isHighQuality) =>
        DalamudInventoryStackScanner.ScanLoadedStacks(PlayerOrdinaryItemContainers, new HashSet<uint> { itemId })
            .Where(stack => stack.IsHighQuality == isHighQuality)
            .Sum(stack => stack.Quantity);

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

    private unsafe (RetainerAutomationResult Result, nint Editor) SetSellingListingPrice(
        RetainerMarketListingTarget listing, uint newUnitPrice)
    {
        try
        {
            var addon = RequireSellingEditor(listing, listing.UnitPrice ??
                throw new NativeCapabilityUnavailableException("An exact observed listing price is required before editing."));
            NativeCapabilityGuard.RequireAddress(AtkComponentNumericInput.Addresses.InnerSetValue.Value,
                "Retainer price numeric input");
            RequireOwnedEvent(&addon->AtkUnitBase, &addon->AskingPrice->OwnerNode->AtkResNode, AtkEventType.ValueUpdate);
            addon->AskingPrice->InnerSetValue(checked((int)newUnitPrice), triggerCallback: true, playSoundEffect: false);
            if (addon->AskingPrice->Value != newUnitPrice)
                throw new NativeCapabilityUnavailableException("The listing price input rejected the requested value.");
            return (RetainerAutomationResult.Succeeded("RetainerMarketPriceEntered",
                "Entered the requested price through its verified native numeric control."), (nint)addon);
        }
        catch (NativeCapabilityUnavailableException exception)
        {
            return (RetainerAutomationResult.Failed(NativeCapabilityGuard.FailureCode, exception.Message), 0);
        }
    }

    private unsafe RetainerAutomationResult ConfirmSellingListingPrice(RetainerMarketListingTarget listing,
        uint newUnitPrice, nint capturedEditor, System.Action markDispatchStarted)
    {
        try
        {
            var addon = RequireSellingEditor(listing, newUnitPrice);
            NativeCapabilityGuard.RequireIdentity((nint)addon, capturedEditor, "Retainer listing editor");
            RequireOwnedEvent(&addon->AtkUnitBase, &addon->Confirm->OwnerNode->AtkResNode, AtkEventType.ButtonClick);
            if (!addon->Confirm->IsEnabled)
                throw new NativeCapabilityUnavailableException("The listing confirmation button is disabled.");
            markDispatchStarted();
            addon->Confirm->ClickAddonButton(&addon->AtkUnitBase);
            return RetainerAutomationResult.Succeeded("RetainerMarketPriceConfirmationSent",
                "Activated the verified listing's native confirmation button exactly once.");
        }
        catch (NativeCapabilityUnavailableException exception)
        {
            return RetainerAutomationResult.Failed(NativeCapabilityGuard.FailureCode, exception.Message);
        }
    }

    private unsafe AddonRetainerSell* RequireSellingEditor(RetainerMarketListingTarget listing, uint displayedPrice)
    {
        var verified = VerifyActive(active?.RetainerId ?? 0);
        if (!verified.Success)
            throw new NativeCapabilityUnavailableException(verified.Message);
        var addon = gameGui.GetAddonByName<AddonRetainerSell>(SellingListingEditor, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible ||
            addon->AskingPrice == null || addon->Quantity == null || addon->Confirm == null || addon->ItemName == null)
            throw new NativeCapabilityUnavailableException("The typed retainer listing editor is unavailable.");
        RequireOwnedComponent(&addon->AtkUnitBase, addon->AskingPrice->OwnerNode,
            (nint)addon->AskingPrice, ComponentType.NumericInput);
        RequireOwnedComponent(&addon->AtkUnitBase, addon->Quantity->OwnerNode,
            (nint)addon->Quantity, ComponentType.NumericInput);
        RequireOwnedComponent(&addon->AtkUnitBase, addon->Confirm->OwnerNode,
            (nint)addon->Confirm, ComponentType.Button);
        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer(InventoryType.RetainerMarket);
        if (manager == null || container == null || !container->IsLoaded ||
            listing.SlotIndex < 0 || listing.SlotIndex >= container->Size ||
            !MatchesMarketListing(manager, container, listing.SlotIndex, listing))
            throw new NativeCapabilityUnavailableException("The exact retainer listing changed before editing.");
        var itemName = dataManager.GetExcelSheet<Item>().GetRow(listing.ItemId).Name.ExtractText();
        RetainerSellingEditorPolicy.RequireListing(listing.Quantity, displayedPrice, itemName,
            addon->Quantity->Value, addon->AskingPrice->Value, addon->ItemName->NodeText.ToString());
        return addon;
    }

    private static unsafe void RequireOwnedComponent(AtkUnitBase* addon, AtkComponentNode* node,
        nint component, ComponentType expectedType)
    {
        if (node == null || node->Component == null || (nint)node->Component != component ||
            node->Component->GetComponentType() != expectedType || !node->AtkResNode.IsVisible() ||
            addon->UldManager.NodeList == null || addon->UldManager.NodeListCount > 4096)
            throw new NativeCapabilityUnavailableException("The typed listing control has an unsupported structure.");
        var matches = 0;
        for (var index = 0; index < addon->UldManager.NodeListCount; index++)
            if (addon->UldManager.NodeList[index] == &node->AtkResNode)
                matches++;
        if (matches != 1)
            throw new NativeCapabilityUnavailableException("The listing control is not uniquely owned by this addon.");
    }

    private static unsafe void RequireOwnedEvent(AtkUnitBase* addon, AtkResNode* node, AtkEventType eventType)
    {
        var matches = 0;
        var registered = (AtkEvent*)node->AtkEventManager.Event;
        var visited = new HashSet<nint>();
        while (registered != null)
        {
            if (visited.Count >= 128 || !visited.Add((nint)registered))
                throw new NativeCapabilityUnavailableException("The listing control's event chain is unsupported.");
            if (registered->State.EventType == eventType)
            {
                if ((nint)registered->Listener != (nint)addon || registered->Node != node)
                    throw new NativeCapabilityUnavailableException("The listing event belongs to another native receiver.");
                matches++;
            }
            registered = registered->NextEvent;
        }
        if (matches != 1)
            throw new NativeCapabilityUnavailableException("The listing control does not expose one owned native event.");
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

internal static class RetainerSellingEditorPolicy
{
    public static void RequireListing(int expectedQuantity, uint expectedPrice, string expectedName,
        int displayedQuantity, int displayedPrice, string displayedName)
    {
        if (expectedQuantity <= 0 || expectedPrice == 0 || expectedPrice > int.MaxValue ||
            displayedQuantity != expectedQuantity || displayedPrice != expectedPrice ||
            string.IsNullOrWhiteSpace(expectedName) || !string.Equals(expectedName.Trim(),
                displayedName.Replace("\uE03C", string.Empty, StringComparison.Ordinal).Trim(), StringComparison.Ordinal))
            throw new NativeCapabilityUnavailableException(
                $"The rendered listing '{displayedName.Replace("", string.Empty, StringComparison.Ordinal).Trim()}' x{displayedQuantity} @ {displayedPrice} " +
                $"does not match the exact observed listing '{expectedName}' x{expectedQuantity} @ {expectedPrice}.");
    }
}
