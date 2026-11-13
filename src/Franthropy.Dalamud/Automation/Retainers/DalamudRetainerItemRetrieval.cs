using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Diagnostics;
using GameInventoryType = Dalamud.Game.Inventory.GameInventoryType;

using Franthropy.Dalamud.Automation.Ui;

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
/// game's typed retainer command. The normal verifier watches the addressed
/// slot; a bounded aggregate settle window handles slot reordering only when
/// that cheap path times out.
/// </summary>
public sealed class DalamudRetainerItemRetrieval
{
    private const string InputNumericAddon = "InputNumeric";
    // Server round trips and frame hitches require elapsed time, not a frame budget.
    private const int VerificationBudgetMs = 3_000;
    private const int SettleBudgetMs = 1_000;
    private const int LateObservationBudgetMs = 1_000;
    private const int AggregatePollIntervalTicks = 3;
    // A single long hitch must not consume a whole phase in one or two polls.
    private const int MinimumPollsPerPhase = 10;
    // Agent/container reads can flicker for a frame around dispatch and popups;
    // only a loss that persists this long ends verification early.
    private const int PreconditionLossToleranceMs = 500;
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
    private readonly IGameInventory? gameInventory;
    private RetainerItemCommandDelegate? retainerItemCommand;

    public DalamudRetainerItemRetrieval(
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

    public async Task<RetainerRetrievalResult> RetrieveAsync(
        DalamudInventoryStack stack,
        int requestedQuantity,
        CancellationToken cancellationToken = default) =>
        await RetrieveAsync(stack, requestedQuantity, null, cancellationToken).ConfigureAwait(false);

    public async Task<RetainerRetrievalResult> RetrieveAsync(
        DalamudInventoryStack stack,
        int requestedQuantity,
        int retainerVariantQuantityBefore,
        CancellationToken cancellationToken = default) =>
        await RetrieveAsync(stack, requestedQuantity, (int?)retainerVariantQuantityBefore, cancellationToken).ConfigureAwait(false);

    private async Task<RetainerRetrievalResult> RetrieveAsync(
        DalamudInventoryStack stack,
        int requestedQuantity,
        int? retainerVariantQuantityBefore,
        CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        string outcome = "ExceptionOrCancellation";
        var diagnostics = new RetrievalDiagnostics();
        try
        {
            var result = await RetrieveCoreAsync(stack, requestedQuantity, retainerVariantQuantityBefore, diagnostics, cancellationToken).ConfigureAwait(false);
            diagnostics.Failed = !result.Success;
            outcome = $"{result.Code}: {result.Message}";
            return result;
        }
        finally
        {
            if (outcome == "ExceptionOrCancellation")
                diagnostics.Failed = true;
            diagnostics.FlushFailure(log);
            if (diagnostics.Failed)
                log.Warning("RetrievalQuantity summary: seen={Seen} submitted={Submitted}", diagnostics.QuantitySeen, diagnostics.QuantitySubmitted);
            // One bounded final read, never a resend. Do not mask the original outcome.
            try
            {
                await framework.RunOnTick(() => LogFinalObservation(stack, requestedQuantity, started.ElapsedMilliseconds, outcome, diagnostics)).ConfigureAwait(false);
            }
            catch (Exception exception) { log.Warning(exception, "Retrieval final diagnostic unavailable."); }
        }
    }

    private async Task<RetainerRetrievalResult> RetrieveCoreAsync(
        DalamudInventoryStack stack,
        int requestedQuantity,
        int? retainerVariantQuantityBefore,
        RetrievalDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Subscribe before submitting the native command. Dalamud reports the
        // source and destination changes as immutable snapshots, which lets us
        // prove the transfer even when the game immediately repopulates the slot.
        using var mutation = gameInventory is null
            ? null
            : new RetainerRetrievalMutationTracker(gameInventory, stack.ItemId, stack.IsHighQuality);
        var pending = await framework.RunOnTick(
            () => BeginRetrieval(stack, requestedQuantity, retainerVariantQuantityBefore, diagnostics),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!pending.Success)
            return new(false, 0, pending.Code, pending.Message);

        var phase = Stopwatch.StartNew();
        var phasePolls = 0;
        long? lostSinceMs = null;
        while (phase.ElapsedMilliseconds < VerificationBudgetMs || phasePolls < MinimumPollsPerPhase)
        {
            var immediate = await framework.RunOnTick(
                () => VerifyCompleted(stack, pending, diagnostics),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            diagnostics.Polls++;
            phasePolls++;
            if (immediate.Success)
                return immediate;

            if (mutation?.Matches(pending.Requested) == true)
                return MutationVerified(stack.ItemId, pending.Requested, mutation);
            if (PreconditionLossPersisted(immediate, phase, ref lostSinceMs))
                return await FinishFailureAsync(stack, pending, mutation, diagnostics, phase, immediate, cancellationToken).ConfigureAwait(false);

            if (pending.NeedsQuantityInput && lostSinceMs is null)
            {
                var submitted = await framework.RunOnTick(
                    () => SubmitQuantity(stack.ItemId, pending.Requested, diagnostics),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (submitted.Success)
                {
                    return await WaitForCompletionAsync(
                        stack,
                        pending with { Requested = submitted.Transferred },
                        mutation,
                        diagnostics,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }

        return await ReconcileAfterTimeoutAsync(stack, pending, mutation, diagnostics, phase, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RetainerRetrievalResult> WaitForCompletionAsync(
        DalamudInventoryStack stack,
        PendingRetainerRetrieval pending,
        RetainerRetrievalMutationTracker? mutation,
        RetrievalDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        RetainerRetrievalResult last = new(false, 0, "TransferPending", $"Retrieval did not complete for item {stack.ItemId}.");
        var phase = Stopwatch.StartNew();
        var phasePolls = 0;
        long? lostSinceMs = null;
        while (phase.ElapsedMilliseconds < VerificationBudgetMs || phasePolls < MinimumPollsPerPhase)
        {
            last = await framework.RunOnTick(
                () => VerifyCompleted(stack, pending, diagnostics),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            diagnostics.Polls++;
            phasePolls++;
            if (last.Success)
                return last;

            if (mutation?.Matches(pending.Requested) == true)
                return MutationVerified(stack.ItemId, pending.Requested, mutation);
            if (PreconditionLossPersisted(last, phase, ref lostSinceMs))
                return await FinishFailureAsync(stack, pending, mutation, diagnostics, phase, last, cancellationToken).ConfigureAwait(false);

            await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }

        return await ReconcileAfterTimeoutAsync(stack, pending, mutation, diagnostics, phase, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tolerates single-frame agent/container flicker: verification ends early only
    /// when preconditions stay lost for <see cref="PreconditionLossToleranceMs"/>.
    /// </summary>
    private static bool PreconditionLossPersisted(RetainerRetrievalResult observation, Stopwatch phase, ref long? lostSinceMs)
    {
        if (observation.Code != "VerificationPreconditionsLost")
        {
            lostSinceMs = null;
            return false;
        }
        lostSinceMs ??= phase.ElapsedMilliseconds;
        return phase.ElapsedMilliseconds - lostSinceMs.Value >= PreconditionLossToleranceMs;
    }

    /// <summary>
    /// Validates the selected source, captures both verification baselines, and
    /// submits exactly one native retrieval command.
    /// </summary>
    private unsafe PendingRetainerRetrieval BeginRetrieval(
        DalamudInventoryStack stack,
        int requestedQuantity,
        int? knownRetainerVariantQuantity,
        RetrievalDiagnostics diagnostics)
    {
        try
        {
            retainerItemCommand ??= Marshal.GetDelegateForFunctionPointer<RetainerItemCommandDelegate>(
                RetainerNativeCommandCapability.Resolve(sigScanner));
        }
        catch (Exception exception)
        {
            return PendingRetainerRetrieval.Fail(NativeCapabilityGuard.FailureCode, exception.Message);
        }

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
        IReadOnlyList<InventoryType> playerContainers = stack.Container == InventoryType.RetainerCrystals
            ? [InventoryType.Crystals] : PlayerOrdinaryItemContainers;
        foreach (var type in playerContainers)
        {
            var playerContainer = inventoryManager->GetInventoryContainer(type);
            if (playerContainer == null || !playerContainer->IsLoaded)
                return PendingRetainerRetrieval.Fail("InventoryUnavailable", $"Player inventory {type} is not loaded before dispatch.");
        }

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
        // Quartermaster already owns this total from its route scan. Other consumers
        // retain compatibility by paying for one baseline scan before mutation.
        int retainerBefore;
        if (knownRetainerVariantQuantity is { } knownQuantity)
        {
            if (knownQuantity < stack.Quantity)
            {
                return PendingRetainerRetrieval.Fail(
                    "InvalidRetainerBaseline",
                    $"The known item total {knownQuantity} is smaller than source stack quantity {stack.Quantity}.");
            }

            retainerBefore = knownQuantity;
        }
        else if (!TryCountRetainer(stack, out retainerBefore))
        {
            return PendingRetainerRetrieval.Fail(
                "RetainerInventoryUnavailable",
                "Every retainer inventory container must be loaded before retrieval can be verified.");
        }
        var module = AgentModule.Instance();
        var retainerAgent = module == null ? null : module->GetAgentByInternalId(AgentId.Retainer);
        if (retainerAgent == null || !retainerAgent->IsAgentActive())
            return PendingRetainerRetrieval.Fail("RetainerAgentUnavailable", "Retainer agent is unavailable.");

        nint context;
        try { context = RetainerNativeCommandCapability.Context(retainerAgent); }
        catch (Exception exception) { return PendingRetainerRetrieval.Fail("CommandUnavailable", exception.Message); }
        if (context == 0)
            return PendingRetainerRetrieval.Fail("CommandUnavailable", "Retainer native context is unavailable before dispatch.");
        var existingNumeric = gameGui.GetAddonByName<AtkUnitBase>(InputNumericAddon, 1);
        if (existingNumeric != null && existingNumeric->IsVisible)
            return PendingRetainerRetrieval.Fail("QuantityPopupAlreadyOpen", "An unowned quantity popup exists before dispatch.");
        diagnostics.Write(log, "RetrievalDispatch utc={Utc} source={Container}/{Slot} item={Item} hq={Hq} sourceQuantity={SourceQuantity} requested={Requested} transfer={Transfer} command={Command} agent={Agent:X} context={Context:X} target={Target:X} active={Active} loaded={Loaded} playerBefore={Player} retainerBefore={Retainer} quantityExpected={QuantityExpected}",
            DateTime.UtcNow, stack.Container, stack.SlotIndex, stack.ItemId, stack.IsHighQuality, stack.Quantity,
            requestedQuantity, transfer, (long)selection.Command, (nint)retainerAgent, context,
            Marshal.GetFunctionPointerForDelegate(retainerItemCommand), retainerAgent->IsAgentActive(), container->IsLoaded,
            playerBefore, retainerBefore, selection.NeedsQuantityInput);
        try
        {
            retainerItemCommand(
                context,
                (uint)stack.SlotIndex,
                stack.Container,
                0,
                selection.Command);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Unable to invoke the retainer retrieval command.");
            return PendingRetainerRetrieval.Fail(
                "RetrievalDispatchIndeterminate",
                $"Retainer retrieval dispatch faulted after invocation began; movement must be reconciled, not retried. {ex.Message}");
        }

        return new(
            true,
            transfer,
            playerBefore,
            retainerBefore,
            selection.NeedsQuantityInput,
            "CommandSubmitted",
            $"Submitted retrieval command for item {stack.ItemId}.");
    }

    private unsafe RetainerRetrievalResult SubmitQuantity(uint itemId, int requested, RetrievalDiagnostics diagnostics)
    {
        var numeric = gameGui.GetAddonByName<AtkUnitBase>(InputNumericAddon, 1);
        diagnostics.QuantitySeen |= numeric != null && numeric->IsVisible;
        if (!DalamudNumericDialog.TryGetBounds(numeric, out var minimum, out var maximum))
            return new(false, 0, "QuantityInputPending", $"Numeric quantity popup did not open for item {itemId}.");
        if (!diagnostics.QuantityBoundsLogged)
        {
            diagnostics.QuantityBoundsLogged = true;
            diagnostics.Write(log, "RetrievalQuantity bounds utc={Utc} item={Item} min={Min} max={Max} requested={Requested}",
                DateTime.UtcNow, itemId, minimum, maximum, requested);
        }
        diagnostics.QuantitySeen = true;

        if (maximum <= 0)
            return new(false, 0, "QuantityUnavailable", $"Retainer reported no retrievable quantity for item {itemId}.");

        var submitted = Math.Clamp(requested, Math.Max(1, minimum), maximum);
        if (DalamudNumericDialog.TrySubmit(numeric, submitted) != NumericDialogSubmission.Submitted)
            return new(false, 0, "QuantityInputPending", $"Numeric quantity popup is not ready for item {itemId}.");
        diagnostics.QuantitySubmitted = true;
        diagnostics.Write(log, "RetrievalQuantity utc={Utc} item={Item} min={Min} max={Max} requested={Requested} submitted={Submitted}", DateTime.UtcNow, itemId, minimum, maximum, requested, submitted);
        return new(true, submitted, "QuantitySubmitted", $"Submitted {submitted}x item {itemId} for retrieval.");
    }

    private unsafe void LogFinalObservation(DalamudInventoryStack original, int requested, long elapsedMs, string outcome, RetrievalDiagnostics diagnostics)
    {
        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer(original.Container);
        var slot = container == null || !container->IsLoaded ? null : container->GetInventorySlot(original.SlotIndex);
        var aggregateKnown = TryCountRetainer(original, out var aggregate);
        var numeric = gameGui.GetAddonByName<AtkUnitBase>(InputNumericAddon, 1);
        diagnostics.Write(log, "RetrievalFinal utc={Utc} elapsedMs={Elapsed} source={Container}/{Slot} item={Item} hq={Hq} requested={Requested} exactReadable={Readable} exactItem={ExactItem} exactQuantity={ExactQuantity} exactHq={ExactHq} aggregateKnown={AggregateKnown} aggregate={Aggregate} player={Player} numericPresent={Numeric} numericReady={NumericReady} outcome={Outcome}",
            DateTime.UtcNow, elapsedMs, original.Container, original.SlotIndex, original.ItemId, original.IsHighQuality,
            requested, slot != null, slot == null ? "unknown" : slot->ItemId.ToString(), slot == null ? "unknown" : slot->Quantity.ToString(),
            slot == null ? "unknown" : slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality).ToString(), aggregateKnown,
            aggregateKnown ? aggregate.ToString() : "unknown", VerificationUnavailable(original) is null ? CountPlayer(original).ToString() : "unavailable", numeric != null,
            numeric != null && numeric->IsReady && numeric->IsVisible, outcome);
    }

    /// <summary>
    /// Polls the inexpensive common-path evidence: the addressed source slot and
    /// the player's total for the same item variant.
    /// </summary>
    private static unsafe RetainerRetrievalResult VerifyCompleted(
        DalamudInventoryStack original,
        PendingRetainerRetrieval pending,
        RetrievalDiagnostics diagnostics)
    {
        var unavailable = VerificationUnavailable(original);
        if (unavailable is not null)
            return new(false, 0, "VerificationPreconditionsLost", unavailable);
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return new(false, 0, "VerificationPreconditionsLost", "Inventory manager became unavailable.");

        var container = inventoryManager->GetInventoryContainer(original.Container);
        if (container == null || !container->IsLoaded)
            return new(false, 0, "VerificationPreconditionsLost", "Retainer source container became unavailable.");

        var slot = container->GetInventorySlot(original.SlotIndex);
        if (slot == null)
            return new(false, 0, "VerificationPreconditionsLost", "Retainer source slot became unavailable.");

        var playerAfter = CountPlayer(original);
        diagnostics.ChangeObserved |= playerAfter != pending.PlayerQuantityBefore ||
            slot->ItemId != original.ItemId || slot->Quantity != original.Quantity;
        if ((slot->ItemId != original.ItemId || slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) == original.IsHighQuality) &&
            RetainerRetrievalObservation.Matches(
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

    /// <summary>
    /// Polls aggregate evidence through a bounded settle window. These scans are
    /// reserved for the rare case where the game moved the item but changed which
    /// physical slot represents the remaining stack.
    /// </summary>
    private async Task<RetainerRetrievalResult> ReconcileAfterTimeoutAsync(
        DalamudInventoryStack original,
        PendingRetainerRetrieval pending,
        RetainerRetrievalMutationTracker? mutation,
        RetrievalDiagnostics diagnostics,
        Stopwatch phase,
        CancellationToken cancellationToken)
    {
        var settle = Stopwatch.StartNew();
        RetainerRetrievalResult last;
        do
        {
            if (mutation?.Matches(pending.Requested) == true)
                return MutationVerified(original.ItemId, pending.Requested, mutation);
            last = await framework.RunOnTick(
                () => VerifyAggregateCompleted(original, pending, diagnostics),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            diagnostics.Polls++;
            if (last.Success)
                return last;
            if (last.Code == "VerificationPreconditionsLost")
                break;
            await framework.DelayTicks(AggregatePollIntervalTicks, cancellationToken).ConfigureAwait(false);
        } while (settle.ElapsedMilliseconds < SettleBudgetMs);
        return await FinishFailureAsync(original, pending, mutation, diagnostics, phase, last, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RetainerRetrievalResult> FinishFailureAsync(
        DalamudInventoryStack original,
        PendingRetainerRetrieval pending,
        RetainerRetrievalMutationTracker? mutation,
        RetrievalDiagnostics diagnostics,
        Stopwatch phase,
        RetainerRetrievalResult last,
        CancellationToken cancellationToken)
    {
        // Freeze the failure before observing late arrivals. Keep the subscription scoped
        // to this call: there is no plugin-disposal lifetime to safely own detached work.
        var result = await framework.RunOnTick(() => DescribeFailure(original, pending, mutation, diagnostics, phase, last),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Success)
            return result;
        var late = Stopwatch.StartNew();
        using var lateCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lateCancellation.CancelAfter(LateObservationBudgetMs);
        try
        {
            while (late.ElapsedMilliseconds < LateObservationBudgetMs)
            {
                await Task.Delay(50, lateCancellation.Token).ConfigureAwait(false);
                if (late.ElapsedMilliseconds >= LateObservationBudgetMs)
                    break;
                var observed = await framework.RunOnTick(
                    () => mutation?.Matches(pending.Requested) == true || VerifyAggregateCompleted(original, pending).Success,
                    cancellationToken: lateCancellation.Token).ConfigureAwait(false);
                if (!observed)
                    continue;
                log.Warning("RetrievalLateConfirmation: item {Item} arrived {LateMs} ms after the verification window closed; outcome remains {Code}; never redispatch.",
                    original.ItemId, late.ElapsedMilliseconds, result.Code);
                break;
            }
        }
        // The late watch is diagnostic only: caller cancellation must not discard the
        // already-classified failure.
        catch (OperationCanceledException) { }
        catch (Exception exception) { log.Warning(exception, "Retrieval late diagnostic unavailable; preserving {Code}.", result.Code); }
        return result;
    }

    private static RetainerRetrievalResult DescribeFailure(
        DalamudInventoryStack original, PendingRetainerRetrieval pending,
        RetainerRetrievalMutationTracker? mutation, RetrievalDiagnostics diagnostics,
        Stopwatch phase, RetainerRetrievalResult last)
    {
        if (mutation?.Matches(pending.Requested) == true)
            return MutationVerified(original.ItemId, pending.Requested, mutation);
        diagnostics.Polls++;
        var final = VerifyAggregateCompleted(original, pending, diagnostics);
        if (final.Success)
            return final;
        var unavailable = VerificationUnavailable(original);
        var retainerKnown = TryCountRetainer(original, out var retainer);
        var player = unavailable is null ? CountPlayer(original).ToString() : "unavailable";
        var evidence = mutation?.Snapshot();
        var changed = diagnostics.ChangeObserved || (retainerKnown && retainer != pending.RetainerQuantityBefore) ||
            (unavailable is null && CountPlayer(original) != pending.PlayerQuantityBefore) ||
            (evidence is { } e && (e.RetainerDelta != 0 || e.PlayerDelta != 0));
        var lost = unavailable is not null || !retainerKnown || last.Code == "VerificationPreconditionsLost";
        var code = lost ? "VerificationPreconditionsLost" : changed ? "RetrievalDeltaMismatch" : "VerificationWindowExpired";
        var reason = lost ? $"Verification preconditions lost: {unavailable ?? last.Message}. " :
            changed ? "Partial or mismatched inventory movement observed. " : "No matching inventory movement observed. ";
        return new(false, 0, code,
            $"{reason}Stopped watching after {diagnostics.Elapsed.ElapsedMilliseconds:N0} ms total " +
            $"(current phase {phase.ElapsedMilliseconds:N0} ms, verification budget {VerificationBudgetMs:N0} ms, settle budget {SettleBudgetMs:N0} ms, {diagnostics.Polls} total polls; " +
            $"phase {(diagnostics.QuantitySubmitted ? "after quantity submit" : "after dispatch")}): " +
            $"retainer {pending.RetainerQuantityBefore}->{(retainerKnown ? retainer.ToString() : "unavailable")}, " +
            $"player {pending.PlayerQuantityBefore}->{player}, expected retainer -{pending.Requested}/player +{pending.Requested}; " +
            $"quantity prompt expected={pending.NeedsQuantityInput}, seen={diagnostics.QuantitySeen}, submitted={diagnostics.QuantitySubmitted}; " +
            (evidence is { } events ? $"inventory events={events.EventCount}, net deltas retainer={events.RetainerDelta}, player={events.PlayerDelta}." : "inventory event tracker unavailable."));
    }

    private static unsafe string? VerificationUnavailable(DalamudInventoryStack original)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return "inventory manager unavailable";
        var module = AgentModule.Instance();
        var agent = module == null ? null : module->GetAgentByInternalId(AgentId.Retainer);
        if (agent == null || !agent->IsAgentActive())
            return "retainer agent unavailable";
        IReadOnlyList<InventoryType> playerContainers = original.Container == InventoryType.RetainerCrystals
            ? [InventoryType.Crystals] : PlayerOrdinaryItemContainers;
        foreach (var type in playerContainers.Append(original.Container))
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                return $"container {type} unavailable";
        }
        return null;
    }

    private static RetainerRetrievalResult MutationVerified(
        uint itemId,
        int requested,
        RetainerRetrievalMutationTracker mutation)
    {
        var evidence = mutation.Snapshot();
        return new(
            true,
            requested,
            "TransferVerified",
            $"Retrieved {requested}x item {itemId}; command-scoped inventory changes confirmed retainer {evidence.RetainerDelta:+#;-#;0}, player {evidence.PlayerDelta:+#;-#;0}.");
    }

    /// <summary>
    /// Accepts the timeout recovery only when the retainer and player report equal,
    /// opposite, exact deltas for the requested quantity.
    /// </summary>
    private static RetainerRetrievalResult VerifyAggregateCompleted(
        DalamudInventoryStack original,
        PendingRetainerRetrieval pending,
        RetrievalDiagnostics? diagnostics = null)
    {
        var unavailable = VerificationUnavailable(original);
        if (unavailable is not null)
            return new(false, 0, "VerificationPreconditionsLost", unavailable);
        var playerAfter = CountPlayer(original);
        if (!TryCountRetainer(original, out var retainerAfter))
        {
            return new(
                false,
                0,
                "VerificationPreconditionsLost",
                $"Retrieval of item {original.ItemId} could not be reconciled because retainer inventory became unavailable.");
        }
        if (RetainerRetrievalObservation.MatchesAggregate(
                pending.Requested,
                pending.RetainerQuantityBefore,
                retainerAfter,
                pending.PlayerQuantityBefore,
                playerAfter))
        {
            return new(
                true,
                pending.Requested,
                "TransferVerified",
                $"Retrieved {pending.Requested}x item {original.ItemId}; aggregate inventories confirmed a reordered source slot.");
        }

        if (diagnostics is not null)
            diagnostics.ChangeObserved |= retainerAfter != pending.RetainerQuantityBefore || playerAfter != pending.PlayerQuantityBefore;

        return new(
            false,
            0,
            "TransferPending",
            $"Waiting for aggregate deltas: retainer {pending.RetainerQuantityBefore}->{retainerAfter}, player {pending.PlayerQuantityBefore}->{playerAfter}, expected {pending.Requested}.");
    }

    /// <summary>Counts the affected item variant across the player's loaded bags.</summary>
    private static int CountPlayer(DalamudInventoryStack stack) =>
        stack.Container == InventoryType.RetainerCrystals
            ? DalamudInventoryStackScanner.CountLoadedItem(InventoryType.Crystals, stack.ItemId)
            : PlayerOrdinaryItemContainers.Sum(
                type => DalamudInventoryStackScanner.CountLoadedItem(type, stack.ItemId, stack.IsHighQuality));

    /// <summary>
    /// Counts only the affected item variant across the currently open retainer.
    /// Callers should supply their existing route-scan total for the before value;
    /// this reader is retained for bounded timeout reconciliation and compatible
    /// consumers that do not already own such a snapshot.
    /// </summary>
    private static unsafe bool TryCountRetainer(DalamudInventoryStack stack, out int quantity)
    {
        quantity = 0;
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return false;

        IReadOnlyList<InventoryType> containers = stack.Container == InventoryType.RetainerCrystals
            ? [InventoryType.RetainerCrystals]
            : DalamudRetainerInventory.OrdinaryItemContainers;
        foreach (var inventoryType in containers)
        {
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
                return false;

            quantity += DalamudInventoryStackScanner.CountLoadedItem(
                inventoryType,
                stack.ItemId,
                stack.Container == InventoryType.RetainerCrystals ? null : stack.IsHighQuality);
        }

        return true;
    }

    private sealed class RetrievalDiagnostics
    {
        private readonly List<(string Template, object[] Arguments)> buffered = [];
        public bool Failed { get; set; }
        public Stopwatch Elapsed { get; } = Stopwatch.StartNew();
        public bool ChangeObserved { get; set; }
        public bool QuantitySeen { get; set; }
        public bool QuantitySubmitted { get; set; }
        public bool QuantityBoundsLogged { get; set; }
        public int Polls { get; set; }

        public void Write(IPluginLog log, string template, params object[] arguments)
        {
            if (Failed)
                log.Warning(template, arguments);
            else
            {
                buffered.Add((template, arguments));
                log.Information(template, arguments);
            }
        }

        public void FlushFailure(IPluginLog log)
        {
            if (Failed)
                foreach (var entry in buffered)
                    log.Warning(entry.Template, entry.Arguments);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
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
        int RetainerQuantityBefore,
        bool NeedsQuantityInput,
        string Code,
        string Message)
    {
        public static PendingRetainerRetrieval Fail(string code, string message) =>
            new(false, 0, 0, 0, false, code, message);
    }
}

/// <summary>
/// Accumulates the net item movement emitted while one retrieval command is in
/// flight. Internal rearrangement produces equal additions and removals, so it
/// cannot masquerade as stock crossing the retainer boundary.
/// </summary>
internal sealed class RetainerRetrievalMutationAccumulator
{
    private int playerDelta;
    private int retainerDelta;
    private int eventCount;

    public void RecordPlayer(int quantityDelta)
    {
        playerDelta += quantityDelta;
        eventCount++;
    }

    public void RecordRetainer(int quantityDelta)
    {
        retainerDelta += quantityDelta;
        eventCount++;
    }
    public RetainerRetrievalMutationEvidence Snapshot() => new(retainerDelta, playerDelta, eventCount);
    public bool Matches(int requested) =>
        RetainerRetrievalObservation.MatchesMutation(requested, retainerDelta, playerDelta);
}

internal readonly record struct RetainerRetrievalMutationEvidence(
    int RetainerDelta,
    int PlayerDelta,
    int EventCount);

/// <summary>
/// Converts Dalamud's per-slot inventory events into one command-scoped net
/// movement. The subscription is deliberately short-lived and is disposed as
/// soon as the retrieval reaches a terminal result.
/// </summary>
internal sealed class RetainerRetrievalMutationTracker : IDisposable
{
    private static readonly IReadOnlySet<GameInventoryType> PlayerContainers = new HashSet<GameInventoryType>
    {
        GameInventoryType.Inventory1,
        GameInventoryType.Inventory2,
        GameInventoryType.Inventory3,
        GameInventoryType.Inventory4,
    };
    private static readonly IReadOnlySet<GameInventoryType> RetainerContainers = new HashSet<GameInventoryType>
    {
        GameInventoryType.RetainerPage1,
        GameInventoryType.RetainerPage2,
        GameInventoryType.RetainerPage3,
        GameInventoryType.RetainerPage4,
        GameInventoryType.RetainerPage5,
        GameInventoryType.RetainerPage6,
        GameInventoryType.RetainerPage7,
        GameInventoryType.RetainerCrystals,
    };

    private readonly IGameInventory inventory;
    private readonly uint itemId;
    private readonly bool isHighQuality;
    private readonly object gate = new();
    private readonly RetainerRetrievalMutationAccumulator accumulator = new();

    public RetainerRetrievalMutationTracker(IGameInventory inventory, uint itemId, bool isHighQuality)
    {
        this.inventory = inventory;
        this.itemId = itemId;
        this.isHighQuality = isHighQuality;
        inventory.InventoryChanged += OnInventoryChanged;
    }

    public bool Matches(int requested)
    {
        lock (gate)
            return accumulator.Matches(requested);
    }

    public RetainerRetrievalMutationEvidence Snapshot()
    {
        lock (gate)
            return accumulator.Snapshot();
    }

    public void Dispose() => inventory.InventoryChanged -= OnInventoryChanged;

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        lock (gate)
        {
            foreach (var change in events.SelectMany(Flatten))
            {
                var quantityDelta = QuantityDelta(change);
                if (quantityDelta == 0)
                    continue;
                if (PlayerContainers.Contains(change.Item.ContainerType))
                    accumulator.RecordPlayer(quantityDelta);
                else if (RetainerContainers.Contains(change.Item.ContainerType))
                    accumulator.RecordRetainer(quantityDelta);
            }
        }
    }

    private int QuantityDelta(InventoryEventArgs change)
    {
        if (change is InventoryItemChangedArgs changed)
            return TrackedQuantity(change.Item) - TrackedQuantity(changed.OldItemState);
        if (change.Type == GameInventoryEvent.Added)
            return TrackedQuantity(change.Item);
        if (change.Type == GameInventoryEvent.Removed)
            return -TrackedQuantity(change.Item);
        return 0;
    }

    private int TrackedQuantity(GameInventoryItem item) =>
        item.BaseItemId == itemId && item.IsHq == isHighQuality
            ? checked((int)item.Quantity)
            : 0;

    private static IEnumerable<InventoryEventArgs> Flatten(InventoryEventArgs change)
    {
        if (change is InventoryComplexEventArgs complex)
        {
            foreach (var source in Flatten(complex.SourceEvent))
                yield return source;
            foreach (var target in Flatten(complex.TargetEvent))
                yield return target;
            yield break;
        }
        yield return change;
    }
}
