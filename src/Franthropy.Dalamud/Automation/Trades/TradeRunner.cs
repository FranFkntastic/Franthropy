using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace Franthropy.Dalamud.Automation.Trades;

public sealed class TradeRunner : IDisposable
{
    private static readonly TimeSpan OpenTradeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InventoryPreparationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OfferItemsTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PartnerTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);

    private readonly IList<TradeItem> queue;
    private readonly TradeTimingOptions timing;
    private readonly TradeRecoveryState recovery;
    private readonly Action save;
    private readonly ITradeIo io;
    private readonly ITradeSuppression externalAutomation;
    private readonly IPluginLog log;
    private readonly Func<DateTimeOffset> clock;

    private TradePartner? partner;
    private TradeBatch? batch;
    private DateTimeOffset deadline;
    private DateTimeOffset nextActionAt;
    private (TradeExecutionState State, string Message)? pendingFinish;
    private bool cancellationCheckpointSaved;
    private DateTimeOffset batchRecoveryDeadline;
    private TradeOfferLineOperation? offerLineOperation;
    private TradeOfferGilOperation? offerGilOperation;
    private bool readyClicked;
    private bool confirmationSubmitted;
    private ulong actor;
    private bool stopAfterBatch;
    public OperationResult? Result { get; private set; }
    public TradeDeltaEvidence? Evidence { get; private set; }
    private TradeBatch? unsettledBatch;

    /// <summary>Read-only follow-up after an indeterminate result; never modifies remaining intent.</summary>
    public TradeDeltaEvidence? ObserveLateEvidence()
    {
        if (Result?.Status != OperationStatus.Indeterminate || unsettledBatch == null || partner == null ||
            io.IsTradeOpen || actor == 0 || io.LocalContentId != actor) return null;
        var inventory = io.ObserveTradeableInventory();
        if (!inventory.IsAuthoritative) return null;
        var after = TradePlanner.CountExactInventory(inventory.Stacks);
        if (Evidence == null || after.Count != Evidence.After.Count ||
            after.Any(x => x.Value != Evidence.After.GetValueOrDefault(x.Key)))
        {
            Evidence = new(partner, clock(), unsettledBatch.ExpectedInventoryBefore, after);
            log.Warning("[Franthropy] Late inventory evidence for batch {Batch} after {Code}; no custody credit applied.", batchNumber, Result.Code);
        }
        return Evidence;
    }
    private int batchNumber;
    private int initialUnitCount;
    private int completedUnitCount;
    private int completedBatchCount;
    private string checkpointQueueSignature = string.Empty;
    private string? runId;

    public TradeRunner(
        ITradeIntentStore store,
        TradeTimingOptions timing,
        ITradeIo io,
        ITradeSuppression externalAutomation,
        IPluginLog log)
        : this(store.Remaining, timing, store.Save, io, externalAutomation, log, () => DateTimeOffset.UtcNow, store.Recovery)
    {
    }

    internal TradeRunner(
        IList<TradeItem> queue,
        TradeTimingOptions timing,
        Action save,
        ITradeIo io,
        ITradeSuppression externalAutomation,
        IPluginLog log,
        Func<DateTimeOffset> clock,
        TradeRecoveryState? recovery = null)
    {
        this.queue = queue;
        this.timing = timing;
        this.recovery = recovery ?? new();
        this.save = save;
        this.io = io;
        this.externalAutomation = externalAutomation;
        this.log = log;
        this.clock = clock;
        if (this.recovery.CancellationPending)
        {
            cancellationCheckpointSaved = true;
            externalAutomation.SuppressTradeAutoConfirm();
            pendingFinish = (this.recovery.ObserveOnly ? TradeExecutionState.Failed : TradeExecutionState.Stopped,
                "Recovering the previous trade closure.");
            if (this.recovery.ObserveOnly)
                Result = new(OperationStatus.Indeterminate, "InterruptedAfterLock", "An interrupted locked trade requires reconciliation.");
            SetActive(TradeExecutionState.ClosingTrade, pendingFinish.Value.Message, CompletionTimeout);
        }
    }

    public TradeExecutionSnapshot Snapshot { get; private set; } =
        new(TradeExecutionState.Idle, "Trade is idle.", null, null, 0, 0, 0, 0, 0, 0, 0, false);

    public bool IsActive => Snapshot.IsActive;

    /// <summary>
    /// Optional recipient restriction checked before any run starts. Returns null to allow the partner, or the
    /// refusal message (which must name the expected and the actual recipient).
    /// </summary>
    public Func<TradePartner, string?>? RecipientGuard { get; set; }

    /// <summary>Raised on the framework thread after each batch's inventory delta is verified, before the queue is saved.</summary>
    public event Action<TradePartner, TradeBatch>? BatchVerified;

    /// <summary>
    /// Raised on the framework thread when a batch is planned, before its trade is opened or offered. A rebuilt
    /// batch raises it again; the latest one replaces the earlier plan.
    /// </summary>
    public event Action<TradePartner, TradeBatch>? BatchStarting;

    /// <summary>
    /// Raised on the framework thread just before this client clicks Ready on a batch's offer (possibly on several
    /// ticks until the click lands). A trade cannot complete without that click, so a batch that never raised this
    /// cannot have been handed over.
    /// </summary>
    public event Action<TradePartner, TradeBatch>? BatchLocking;

    /// <summary>Raised when a run fails or stops with a planned batch still unverified.</summary>
    public event Action<TradePartner, TradeBatch>? BatchAbandoned;

    /// <summary>Resolves capability evidence without selecting a recipient or starting any operation.</summary>
    public (bool Available, string Message) InspectExecutionCapability()
    {
        var available = io.TryGetExecutionReadiness(out var error);
        return (available, available ? "Trade UI capabilities are available; item offering is checked when needed." : error);
    }

    public bool HasResumeCheckpoint =>
        Snapshot.State is TradeExecutionState.Failed or TradeExecutionState.Stopped &&
        queue.Count > 0 &&
        checkpointQueueSignature == ComputeQueueSignature(queue) &&
        partner != null;

    public bool CanResume
    {
        get
        {
            if (!io.TryGetSelectedPartner(out var selectedPartner))
                return false;

            return CanResumeWith(selectedPartner);
        }
    }

    public TradeStartResult Start()
    {
        if (Result?.Status == OperationStatus.Indeterminate)
            return new(false, "Previous trade has unresolved custody.");
        if (IsActive)
            return new(false, "Trade is already running.");
        var hasSelectedPartner = io.TryGetSelectedPartner(out var selectedPartner);
        if (!io.TryGetExecutionReadiness(out var readinessError))
            return RejectStart(hasSelectedPartner ? selectedPartner : null, readinessError);
        if (!hasSelectedPartner)
            return RejectStart(null, "Select or focus-target the player who should receive this queue.");

        return Start(selectedPartner);
    }

    public TradeStartResult Start(TradePartner selectedPartner)
    {
        if (Result?.Status == OperationStatus.Indeterminate)
            return new(false, "Previous trade is indeterminate; its custody must be settled before another run.");
        if (IsActive)
            return new(false, "Trade is already running.");
        if (!io.TryGetExecutionReadiness(out var readinessError))
            return RejectStart(selectedPartner, readinessError);
        if (RecipientGuard?.Invoke(selectedPartner) is { } refusal)
            return RejectStart(selectedPartner, refusal);
        if (!io.PartnerIsAvailable(selectedPartner))
            return RejectStart(selectedPartner, $"{selectedPartner.Name} @ {selectedPartner.HomeWorldName} is not an exact visible trade recipient.");

        actor = io.LocalContentId;
        if (actor == 0) return RejectStart(selectedPartner, "No logged-in sender identity is observable.");
        partner = selectedPartner;
        Result = null;
        Evidence = null;
        stopAfterBatch = readyClicked = confirmationSubmitted = false;
        var observation = io.ObserveTradeableInventory();
        if (!observation.IsAuthoritative)
        {
            runId = Guid.NewGuid().ToString("N");
            initialUnitCount = queue.Sum(item => item.Quantity);
            completedUnitCount = 0;
            completedBatchCount = 0;
            batchNumber = 1;
            SetActive(
                TradeExecutionState.PreparingInventory,
                "Checking current tradeable inventory...",
                InventoryPreparationTimeout);
            return new(true, $"Started Trade for {partner.Name}; checking current inventory.");
        }

        return StartFromAuthoritativeInventory(observation.Stacks);
    }

    private TradeStartResult RejectStart(TradePartner? recipient, string reason)
    {
        var attemptId = Guid.NewGuid().ToString("N");
        Result = new(OperationStatus.Failed, "PreflightRejected", reason);
        var preserveCheckpoint = HasResumeCheckpoint;
        if (!preserveCheckpoint)
        {
            runId = null;
            partner = null;
            checkpointQueueSignature = string.Empty;
            batchNumber = initialUnitCount = completedUnitCount = completedBatchCount = 0;
        }
        Snapshot = new(
            TradeExecutionState.Failed, reason, runId ?? attemptId, partner?.Name ?? recipient?.Name,
            batchNumber, 0, completedBatchCount,
            initialUnitCount > 0 ? initialUnitCount : queue.Sum(item => item.Quantity), completedUnitCount,
            queue.Count, queue.Sum(item => item.Quantity), false);
        log.Warning(
            "[Franthropy] Trade start rejected attempt={AttemptId} recipient={Recipient}@{World} stage=preflight remainingLines={RemainingLines} remainingUnits={RemainingUnits} reason={Reason}",
            attemptId, recipient?.Name ?? "unselected", recipient?.HomeWorldName ?? "",
            queue.Count, Snapshot.RemainingUnitCount, reason);
        return new(false, reason);
    }

    private TradeStartResult StartFromAuthoritativeInventory(
        IReadOnlyList<TradeInventoryStack> inventory)
    {
        var currentPartner = partner ?? throw new InvalidOperationException("Trade has no captured recipient.");
        var validation = TradePlanner.Validate(queue.ToList(), inventory);
        if (!validation.Success)
        {
            return RejectStart(currentPartner, validation.Message);
        }

        var isResume = CanResumeWith(currentPartner);
        if (!isResume)
        {
            runId = Guid.NewGuid().ToString("N");
            initialUnitCount = queue.Sum(item => item.Quantity);
            completedUnitCount = 0;
            completedBatchCount = 0;
        }

        batchNumber = completedBatchCount + 1;
        checkpointQueueSignature = ComputeQueueSignature(queue);
        externalAutomation.SuppressTradeAutoConfirm();
        return PrepareBatch(inventory, "Opening first trade.")
            ? new(true, Snapshot.Message) : new(false, Snapshot.Message);
    }

    public void Tick()
    {
        try
        {
            if (Snapshot.State == TradeExecutionState.ClosingTrade)
            {
                TickClosingTrade();
                return;
            }
            if (!IsActive) return;

            var now = clock();
            if (deadline != default && now > deadline)
            {
                if (Snapshot.State == TradeExecutionState.OfferingItems &&
                    now < batchRecoveryDeadline && io.IsTradeOpen)
                {
                    BeginClosing(TradeExecutionState.OpeningTrade,
                        "Offer acknowledgement timed out; rebuilding the uncommitted trade.");
                    return;
                }
                Result = new(readyClicked ? OperationStatus.Indeterminate : OperationStatus.Failed,
                    $"{Snapshot.State}Deadline", $"Deadline {deadline:O} expired while {DescribeState(Snapshot.State)}. Last observation: {Snapshot.Message}");
                Fail(Result.Message);
                return;
            }

            if (io.LocalContentId == 0) return;
            if (io.LocalContentId != actor)
            {
                Result = new(OperationStatus.Indeterminate, "SenderChanged", "Sender changed before trade settlement.");
                CompleteFinish(TradeExecutionState.Failed, Result.Message);
                return;
            }
            switch (Snapshot.State)
            {
                case TradeExecutionState.PreparingInventory:
                    TickPreparingInventory();
                    break;
                case TradeExecutionState.OpeningTrade:
                    TickOpeningTrade(now);
                    break;
                case TradeExecutionState.OfferingItems:
                    TickOfferingItems(now);
                    break;
                case TradeExecutionState.WaitingForPartner:
                    TickWaitingForPartner(now);
                    break;
                case TradeExecutionState.ConfirmingTrade:
                    TickConfirmingTrade();
                    break;
                case TradeExecutionState.VerifyingInventory:
                    TickVerifyingInventory(now);
                    break;
            }
        }
        catch (Exception exception)
        {
            log.Error(exception, "[Franthropy] Trade runtime failed.");
            if (Snapshot.State == TradeExecutionState.ClosingTrade)
                Snapshot = Snapshot with { Message = $"Waiting to close the owned trade. {exception.Message}" };
            else
                Fail($"Trade failed: {exception.Message}");
        }
    }

    private void TickPreparingInventory()
    {
        if (partner == null || !io.PartnerIsAvailable(partner))
        {
            Fail("The selected trade partner is no longer available while checking current inventory.");
            return;
        }

        var observation = io.ObserveTradeableInventory();
        if (!observation.IsAuthoritative)
            return;

        StartFromAuthoritativeInventory(observation.Stacks);
    }

    public void Stop(string message = "Trade stopped; unverified quantities remain queued.")
    {
        if (!IsActive)
            return;

        if (Snapshot.State == TradeExecutionState.ClosingTrade)
        {
            pendingFinish = (TradeExecutionState.Stopped, message);
            return;
        }
        if (readyClicked)
        {
            stopAfterBatch = true;
            Snapshot = Snapshot with { Message = "Stop requested after lock; waiting for the outcome." };
            return;
        }
        Finish(TradeExecutionState.Stopped, message);
    }

    public void Dispose()
    {
        if (IsActive && readyClicked && Snapshot.State != TradeExecutionState.ClosingTrade && io.IsTradeOpen)
            BeginClosing(TradeExecutionState.Failed, "Unloading after lock; retaining trade ownership without cancellation.");
        if (IsActive && Snapshot.State != TradeExecutionState.ClosingTrade)
            Stop("Trade stopped because the plugin is unloading.");
        if (recovery.CancellationPending || (IsActive && readyClicked))
            externalAutomation.RetainTradeSuppressionOnDispose();
        else
            externalAutomation.RestoreTradeAutoConfirm();
    }

    private void TickOpeningTrade(DateTimeOffset now)
    {
        if (partner == null || !io.PartnerIsAvailable(partner))
        {
            Fail("The selected trade partner is no longer available. Queue execution stopped before opening another trade.");
            return;
        }

        if (io.IsTradeOpen)
        {
            offerLineOperation = null;
            offerGilOperation = null;
            readyClicked = false;
            confirmationSubmitted = false;
            nextActionAt = now + timing.ActionDelay;
            SetActive(
                TradeExecutionState.OfferingItems,
                $"Trade opened with {partner.Name}; offering batch {batchNumber:N0}.",
                OfferItemsTimeout);
            if (deadline > batchRecoveryDeadline)
                deadline = batchRecoveryDeadline;
            return;
        }

        if (now < nextActionAt)
            return;

        var commandSent = io.TryOpenTrade(partner);
        nextActionAt = now + (commandSent ? timing.TradeRetryDelay : timing.ActionDelay);
    }

    private void TickOfferingItems(DateTimeOffset now)
    {
        if (!io.IsTradeOpen)
        {
            Fail("Trade closed before the sender finished offering the current batch.");
            return;
        }
        if (batch == null)
        {
            Fail("Trade batch state is unavailable.");
            return;
        }

        var liveOffer = io.ObserveOffer();
        if (liveOffer == null)
        {
            Snapshot = Snapshot with { Message = "Verifying the trade recipient..." };
            return;
        }
        if (partner == null || liveOffer.PartnerEntityId != (uint)partner.GameObjectId)
        {
            Fail("The live trade recipient differs from the selected recipient.");
            return;
        }

        if (offerLineOperation != null)
        {
            var result = offerLineOperation.Advance(now);
            Snapshot = Snapshot with { Message = result.Message };
            if (result.IsFailed)
                Fail(result.Message);
            else if (result.IsCompleted || !offerLineOperation.HasDispatched)
            {
                offerLineOperation = null;
                nextActionAt = now + timing.ActionDelay;
            }
            return;
        }

        if (liveOffer.SyncPending)
            return;
        if (offerGilOperation == null && io.IsNumericInputOpen)
            return;
        // During our numeric-input protocol the provisional slot need not yet
        // contain the requested amount. Outside it, never lock an excess offer.
        if (offerGilOperation == null || offerGilOperation.IsCompleted)
        {
            var conflict = liveOffer.Mismatch(partner, batch, allowPartial: true);
            if (conflict != null)
            {
                Snapshot = Snapshot with { Message = $"Waiting for the offer to match the queue. {conflict}" };
                return;
            }
        }

        if (liveOffer.Gil != batch.GilAmount || offerGilOperation is { IsCompleted: false })
        {
            if (now < nextActionAt)
                return;

            if (offerGilOperation == null || offerGilOperation.IsCompleted)
                offerGilOperation = new TradeOfferGilOperation(io, batch.GilAmount);
            var gilResult = offerGilOperation.Advance();
            Snapshot = Snapshot with { Message = gilResult.Message };
            if (gilResult.IsFailed)
            {
                Fail(gilResult.Message);
                return;
            }

            if (gilResult.ActionIssued || gilResult.IsCompleted)
                nextActionAt = now + timing.ActionDelay;
            return;
        }

        var inventory = io.ObserveTradeableInventory();
        if (!inventory.IsAuthoritative)
        {
            Snapshot = Snapshot with { Message = "Refreshing source inventory for the remaining offer..." };
            return;
        }
        if (liveOffer.Mismatch(partner, batch) == null)
        {
            batch = batch with { ExpectedInventoryBefore = TradePlanner.CountExactInventory(inventory.Stacks) };
            SetActive(
                TradeExecutionState.WaitingForPartner,
                $"Batch {batchNumber:N0} is loaded; locking the trade when ready.",
                OfferItemsTimeout);
            if (deadline > batchRecoveryDeadline)
                deadline = batchRecoveryDeadline;
            return;
        }

        if (now < nextActionAt)
            return;

        var nextLine = TradeOfferProgress.NextLine(batch, liveOffer, inventory.Stacks);
        if (nextLine == null)
        {
            // A manual partial offer can occupy the only usable source stack or
            // all five slots. Reopen before locking, rather than guessing how a
            // second native offer call would alter an already-offered stack.
            if (liveOffer.Items.Count > 0 && now < batchRecoveryDeadline)
            {
                BeginClosing(TradeExecutionState.OpeningTrade, "Rebuilding the remaining trade from current inventory.");
            }
            else
                Snapshot = Snapshot with { Message = "Waiting for sufficient eligible inventory for the planned offer..." };
            return;
        }
        offerLineOperation = new TradeOfferLineOperation(io, nextLine, liveOffer, deadline);
        var dispatched = offerLineOperation.Advance(now);
        Snapshot = Snapshot with { Message = dispatched.Message };
        if (dispatched.IsFailed)
            Fail(dispatched.Message);
        else if (!offerLineOperation.HasDispatched)
        {
            offerLineOperation = null;
            nextActionAt = now + timing.ActionDelay;
        }
    }

    private void TickWaitingForPartner(DateTimeOffset now)
    {
        if (!io.IsTradeOpen)
        {
            SetActive(
                TradeExecutionState.VerifyingInventory,
                $"Trade closed; verifying batch {batchNumber:N0}.",
                CompletionTimeout);
            return;
        }
        if (readyClicked || now < nextActionAt)
            return;

        var offer = io.ObserveOffer();
        if (offer == null || offer.SyncPending || io.IsNumericInputOpen)
            return;
        if (partner == null || batch == null)
        {
            Fail("The expected trade recipient or batch is unavailable.");
            return;
        }
        if (offer.PartnerEntityId != (uint)partner.GameObjectId)
        {
            Fail("The live trade recipient changed before locking.");
            return;
        }
        if (!CheckConsideration(partner)) return;
        if (offer.Mismatch(partner, batch) != null)
        {
            SetActive(TradeExecutionState.OfferingItems, "Reconciling the changed offer before locking...", null);
            deadline = batchRecoveryDeadline;
            return;
        }

        var inventory = io.ObserveTradeableInventory();
        if (!inventory.IsAuthoritative)
            return;
        batch = batch with { ExpectedInventoryBefore = TradePlanner.CountExactInventory(inventory.Stacks) };

        NotifyBatch(BatchLocking, batch, "locking");
        if (!io.TryClickReady(out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                Fail(error);
            else
                Snapshot = Snapshot with { Message = $"Waiting for trade readiness. {io.ReadyDiagnostic}" };
            return;
        }

        readyClicked = true;
        SetActive(
            TradeExecutionState.ConfirmingTrade,
            $"Batch {batchNumber:N0} is locked; waiting for partner confirmation.",
            PartnerTimeout);
    }

    private void TickConfirmingTrade()
    {
        if (!io.IsTradeOpen)
        {
            SetActive(
                TradeExecutionState.VerifyingInventory,
                $"Trade closed; verifying batch {batchNumber:N0}.",
                CompletionTimeout);
            return;
        }
        if (confirmationSubmitted || stopAfterBatch || !io.IsRemoteLocked)
            return;

        if (!VerifyOffer())
            return;

        if (!io.TryConfirmTrade(out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                Fail(error);
            return;
        }

        confirmationSubmitted = true;
        SetActive(
            TradeExecutionState.VerifyingInventory,
            $"Confirmed batch {batchNumber:N0}; waiting for inventory evidence.",
            CompletionTimeout);
    }

    private bool VerifyOffer()
    {
        var offer = io.ObserveOffer();
        if (offer == null || offer.SyncPending || io.IsNumericInputOpen)
        {
            Snapshot = Snapshot with { Message = "Verifying the live trade offer..." };
            return false;
        }
        if (partner == null || batch == null)
        {
            Fail("The expected trade recipient or batch is unavailable.");
            return false;
        }
        var mismatch = offer.Mismatch(partner, batch);
        if (mismatch == null)
            return CheckConsideration(partner);
        Fail(mismatch);
        return false;
    }

    private void TickVerifyingInventory(DateTimeOffset now)
    {
        if (io.IsTradeOpen)
            return;
        if (batch == null)
        {
            Fail("Trade batch state is unavailable during verification.");
            return;
        }

        var observation = io.ObserveTradeableInventory();
        if (!observation.IsAuthoritative)
        {
            Snapshot = Snapshot with
            {
                Message = $"Trade closed; checking batch {batchNumber:N0} inventory evidence.",
            };
            return;
        }
        var inventory = observation.Stacks;
        if (!readyClicked)
        {
            Fail("Trade closed before this operation locked the manifest.");
            return;
        }
        Evidence = new(partner!, now, batch.ExpectedInventoryBefore, TradePlanner.CountExactInventory(inventory));
        if (!TradePlanner.HasExpectedInventoryDelta(batch, inventory, out var diagnostic))
        {
            Snapshot = Snapshot with
            {
                Message = $"Trade closed; waiting for batch {batchNumber:N0} inventory evidence. {diagnostic}",
            };
            return;
        }

        TradePlanner.ApplyCompletedBatch(queue, batch);
        completedUnitCount = checked(completedUnitCount + batch.UnitCount);
        completedBatchCount++;
        NotifyBatchVerified(batch);
        save();
        checkpointQueueSignature = ComputeQueueSignature(queue);
        if (stopAfterBatch && queue.Count != 0)
        {
            Finish(TradeExecutionState.Stopped, "Stopped after the locked batch was verified.");
            return;
        }
        if (queue.Count == 0)
        {
            Finish(
                TradeExecutionState.Completed,
                $"Trade completed with {partner?.Name} across {completedBatchCount:N0} verified batch(es).");
            return;
        }

        batchNumber = completedBatchCount + 1;
        nextActionAt = now;
        if (!PrepareBatch(
                inventory,
                $"Batch {completedBatchCount:N0} completed; opening batch {batchNumber:N0}."))
            return;

        TickOpeningTrade(now);
    }

    private void NotifyBatchVerified(TradeBatch verified) => NotifyBatch(BatchVerified, verified, "verified");

    private bool CheckConsideration(TradePartner expected)
    {
        var remote = io.ObserveRemoteOffer();
        if (remote == null || remote.SyncPending) return false;
        if (io.PartnerIsAvailable(expected) && remote.PartnerEntityId == (uint)expected.GameObjectId &&
            remote.Items.Count == 0 && remote.Gil == 0) return true;
        Fail("Partner identity or incoming consideration changed.");
        return false;
    }

    private void NotifyBatch(Action<TradePartner, TradeBatch>? listeners, TradeBatch planned, string stage)
    {
        if (partner == null || listeners == null)
            return;
        try
        {
            listeners(partner, planned);
        }
        catch (Exception exception)
        {
            log.Error(exception, "[Franthropy] Trade batch {Stage} listener failed run={RunId} batch={Batch}.", stage, runId ?? "", batchNumber);
            if (stage != "abandoned") throw;
        }
    }

    private bool PrepareBatch(IReadOnlyList<TradeInventoryStack> inventory, string message)
    {
        var validation = TradePlanner.Validate(queue.ToList(), inventory);
        if (!validation.Success)
        {
            Fail(validation.Message);
            return false;
        }

        batch = TradePlanner.BuildNextBatch(
            queue.ToList(),
            inventory);
        NotifyBatch(BatchStarting, batch, "starting");
        batchRecoveryDeadline = clock() + OpenTradeTimeout + OfferItemsTimeout;
        offerLineOperation = null;
        offerGilOperation = null;
        readyClicked = false;
        confirmationSubmitted = false;
        SetActive(TradeExecutionState.OpeningTrade, message, OpenTradeTimeout);
        return true;
    }

    private void SetActive(TradeExecutionState state, string message, TimeSpan? timeout)
    {
        var previousState = Snapshot.State;
        deadline = timeout is { } bounded && bounded > TimeSpan.Zero
            ? clock() + bounded
            : default;
        Snapshot = new(
            state,
            message,
            runId,
            partner?.Name,
            batchNumber,
            batch?.SlotCount ?? 0,
            completedBatchCount,
            initialUnitCount,
            completedUnitCount,
            queue.Count,
            queue.Sum(item => item.Quantity),
            true);
        if (previousState != state)
            LogTransition(previousState, state, message);
    }

    private void Fail(string message)
    {
        Result ??= new(readyClicked ? OperationStatus.Indeterminate : OperationStatus.Failed,
            $"{Snapshot.State}Failed", message);
        Finish(TradeExecutionState.Failed, message);
    }

    private void Finish(TradeExecutionState state, string message)
    {
        if (state is TradeExecutionState.Failed or TradeExecutionState.Stopped &&
            io.IsTradeOpen)
        {
            BeginClosing(state, message);
            return;
        }

        CompleteFinish(state, message);
    }

    private void BeginClosing(TradeExecutionState state, string message)
    {
        recovery.ObserveOnly = readyClicked;
        recovery.OwnerContentId = actor;
        pendingFinish = (state, message);
        if (!recovery.CancellationPending)
            cancellationCheckpointSaved = false;
        recovery.CancellationPending = true;
        recovery.PartnerEntityId = partner == null ? recovery.PartnerEntityId : (uint)partner.GameObjectId;
        externalAutomation.SuppressTradeAutoConfirm();
        nextActionAt = default;
        SetActive(TradeExecutionState.ClosingTrade, $"{message} Waiting for trade closure.", CompletionTimeout);
        TickClosingTrade();
    }

    private void TickClosingTrade()
    {
        if (pendingFinish is not { } terminal)
            throw new InvalidOperationException("Trade closure has no terminal result.");
        if (clock() < nextActionAt)
            return;
        nextActionAt = clock() + timing.TradeRetryDelay;
        if (deadline != default && clock() >= deadline)
        {
            var reason = $"TradeClosureDeadline {deadline:O} expired; retaining ownership until closure is observed.";
            Result = new(OperationStatus.Indeterminate, "TradeClosureDeadline", reason);
            log.Warning("[Franthropy] {Reason}", reason);
            pendingFinish = terminal = (TradeExecutionState.Failed, reason);
            deadline = default;
        }
        if (!cancellationCheckpointSaved)
        {
            if (!TrySaveCancellationState(true))
                return;
            cancellationCheckpointSaved = true;
        }
        if (io.IsTradeOpen)
        {
            // Invalid offer contents prevent confirmation, not cancellation of
            // our own session. Read recipient ownership independently.
            if (io.LocalContentId == 0) return;
            if (io.LocalContentId != recovery.OwnerContentId)
            {
                Result = new(OperationStatus.Indeterminate, "ClosureOwnerChanged", "Sender changed; the open trade was left untouched.");
                CompleteFinish(TradeExecutionState.Failed, Result.Message);
                return;
            }
            var partnerEntityId = io.ObserveTradePartnerEntityId();
            if (partnerEntityId == null)
                return;
            if (partnerEntityId != recovery.PartnerEntityId)
            {
                CompleteFinish(TradeExecutionState.Stopped, "The previous trade session ended; a different trade was left untouched.");
                return;
            }
            var error = string.Empty;
            if (!recovery.ObserveOnly) io.TryCancelTrade(out error);
            Snapshot = Snapshot with
            {
                Message = $"{terminal.Message} Waiting for trade closure." +
                          (string.IsNullOrWhiteSpace(error) ? string.Empty : $" {error}"),
            };
        }
        if (!io.IsTradeOpen)
        {
            if (terminal.State == TradeExecutionState.OpeningTrade)
            {
                if (clock() > batchRecoveryDeadline)
                {
                    CompleteFinish(TradeExecutionState.Failed, "Trade closed; the batch recovery deadline expired before rebuilding the offer.");
                    return;
                }
                var inventory = io.ObserveTradeableInventory();
                if (!inventory.IsAuthoritative)
                {
                    Snapshot = Snapshot with { Message = "Trade closed; refreshing inventory before rebuilding the offer..." };
                    return;
                }
                var recoveryDeadline = batchRecoveryDeadline;
                if (!TrySaveCancellationState(false))
                    return;
                if (!PrepareBatch(inventory.Stacks, terminal.Message))
                    return;
                pendingFinish = null;
                batchRecoveryDeadline = recoveryDeadline;
                if (deadline > recoveryDeadline)
                    deadline = recoveryDeadline;
                nextActionAt = clock();
            }
            else
            {
                CompleteFinish(terminal.State, $"{terminal.Message} The trade is closed.");
            }
        }
    }

    private bool TrySaveCancellationState(bool pending)
    {
        var previous = recovery.CancellationPending;
        recovery.CancellationPending = pending;
        try
        {
            save();
            return true;
        }
        catch (Exception exception)
        {
            recovery.CancellationPending = previous;
            log.Error(exception, "[Franthropy] Could not persist trade cancellation ownership.");
            Snapshot = Snapshot with { Message = "Retaining trade ownership while retrying cancellation checkpoint storage." };
            return false;
        }
    }

    private void CompleteFinish(TradeExecutionState state, string message)
    {
        var previousState = Snapshot.State;
        pendingFinish = (state, message);
        if (recovery.CancellationPending && !TrySaveCancellationState(false))
            return;
        pendingFinish = null;

        externalAutomation.RestoreTradeAutoConfirm();
        deadline = default;
        Result ??= new(state switch
        {
            TradeExecutionState.Completed => OperationStatus.Succeeded,
            TradeExecutionState.Stopped => OperationStatus.Cancelled,
            _ when readyClicked => OperationStatus.Indeterminate,
            _ => OperationStatus.Failed
        }, $"{previousState}.{state}", message);
        nextActionAt = default;
        if (batch != null && state is TradeExecutionState.Failed or TradeExecutionState.Stopped)
            NotifyBatch(BatchAbandoned, batch, "abandoned");
        unsettledBatch = Result.Status == OperationStatus.Indeterminate ? batch : null;
        batch = null;
        Snapshot = new(
            state,
            message,
            runId,
            partner?.Name,
            batchNumber,
            0,
            completedBatchCount,
            initialUnitCount,
            completedUnitCount,
            queue.Count,
            queue.Sum(item => item.Quantity),
            false);
        LogTransition(previousState, state, message);
    }

    private void LogTransition(TradeExecutionState previousState, TradeExecutionState state, string reason)
    {
        const string template = "[Franthropy] Trade transition run={RunId} recipient={Recipient}@{World} previous={PreviousState} state={State} batch={Batch} completedBatches={CompletedBatches} remainingLines={RemainingLines} remainingUnits={RemainingUnits} reason={Reason}";
        object[] values = [runId ?? "none", partner?.Name ?? "unselected", partner?.HomeWorldName ?? "",
            previousState, state, batchNumber, completedBatchCount, queue.Count, Snapshot.RemainingUnitCount, reason];
        if (state == TradeExecutionState.Failed)
            log.Warning(template, values);
        else
            log.Information(template, values);
    }

    private static string ComputeQueueSignature(IEnumerable<TradeItem> items) =>
        string.Join(
            "|",
            items
                .Where(item => item.Quantity > 0)
                .GroupBy(item => (item.ItemId, item.IsHighQuality))
                .OrderBy(group => group.Key.ItemId).ThenBy(group => group.Key.IsHighQuality)
                .Select(group => $"{group.Key}:{group.Sum(item => item.Quantity)}"));

    private bool CanResumeWith(TradePartner selectedPartner) =>
        HasResumeCheckpoint &&
        selectedPartner.GameObjectId == partner!.GameObjectId &&
        selectedPartner.HomeWorldId == partner.HomeWorldId;

    private static string DescribeState(TradeExecutionState state) => state switch
    {
        TradeExecutionState.PreparingInventory => "checking current inventory",
        TradeExecutionState.OpeningTrade => "opening the trade",
        TradeExecutionState.OfferingItems => "offering items",
        TradeExecutionState.WaitingForPartner => "waiting for the partner",
        TradeExecutionState.ConfirmingTrade => "waiting for trade confirmation",
        TradeExecutionState.VerifyingInventory => "verifying inventory changes",
        _ => state.ToString(),
    };
}
