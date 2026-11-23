namespace Franthropy.Dalamud.Automation.Trades;

/// <summary>Accept one incoming window, never a standing auto-accept policy. The caller holds the
/// automation lease and disables other acceptors for the operation, including during recovery.</summary>
public sealed class TradeReceiveOperation
{
    private readonly TradePartner partner;
    private readonly ITradeIo io;
    private readonly DateTimeOffset deadline;
    private readonly Action<string> warning;
    private readonly ulong actor;
    public TradeDeltaEvidence? Evidence { get; private set; }
    private readonly IReadOnlyDictionary<TradeInventoryKey, int> expected;
    private IReadOnlyDictionary<TradeInventoryKey, int>? before;
    private bool confirmed, opened, closed, awaitingReadyAcknowledgement;
    private uint? foreignEntity;
    private OperationResult? closing;
    private DateTimeOffset? closingDeadline;
    private static readonly TimeSpan ClosureTimeout = TimeSpan.FromSeconds(30);
    private IReadOnlyDictionary<TradeInventoryKey, int>? locked;
    /// <summary>The exact credited offer, populated only on success. Subtract it once from the caller's remaining manifest.</summary>
    public IReadOnlyDictionary<TradeInventoryKey, int>? Received { get; private set; }
    private OperationResult result = new(OperationStatus.Pending, "AwaitingTrade", "Waiting for the expected sender.");

    public TradeReceiveOperation(ITradeIo io, TradePartner partner, IReadOnlyList<TradeOfferedItem> remainingManifest, DateTimeOffset deadline,
        Action<string> warning)
    {
        this.io = io;
        actor = io.LocalContentId;
        this.partner = partner;
        this.deadline = deadline;
        this.warning = warning ?? throw new ArgumentNullException(nameof(warning));
        if (remainingManifest.Count == 0 || remainingManifest.Any(x => x.ItemId == 0 || x.Quantity <= 0 ||
                (x.ItemId == TradePlanner.GilItemId && x.IsHighQuality)))
            throw new ArgumentException("Expected a non-empty remaining manifest of positive item or NQ gil quantities.", nameof(remainingManifest));
        expected = remainingManifest.GroupBy(x => new TradeInventoryKey(x.ItemId, x.IsHighQuality))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        expectsGil = expected.ContainsKey(GilKey);
        expectsItems = expected.Keys.Any(k => k != GilKey);
    }

    private static readonly TradeInventoryKey GilKey = new(TradePlanner.GilItemId, false);
    private readonly bool expectsGil, expectsItems;

    public OperationResult Stop()
    {
        if (result.IsTerminal || confirmed) return result;
        return result = BeginClosing(new(OperationStatus.Cancelled, "StoppedBeforeConfirm", "Stopped without confirming."));
    }

    public OperationResult Tick(DateTimeOffset now)
    {
        if (result.IsTerminal)
        {
            if (result.Status == OperationStatus.Indeterminate && before != null && io.LocalContentId == actor && !io.IsTradeOpen &&
                io.ObserveTradeableInventory() is { IsAuthoritative: true } late)
            {
                var counts = TradePlanner.CountExactInventory(late.Stacks);
                if (Evidence == null || Evidence.After.Count != counts.Count ||
                    counts.Any(x => Evidence.After.GetValueOrDefault(x.Key) != x.Value))
                {
                    Evidence = new(partner, now, before, counts);
                    warning($"Late inventory evidence for {partner.Name} after {result.Code}; no custody credit applied.");
                }
            }
            return result;
        }
        if (closing != null)
        {
            // A cancellation dispatch is not closure: keep cancelling the owned window until it is observed closed.
            if (!OwnsOpenWindow()) return result = closing;
            closingDeadline ??= now + ClosureTimeout;
            if (now >= closingDeadline)
            {
                warning($"{partner.Name}@{partner.HomeWorldId}: TradeClosureDeadline after {closing.Code}; the unconfirmed window is still open.");
                return result = new(OperationStatus.Failed, "TradeClosureDeadline",
                    $"The unconfirmed trade window did not close within {ClosureTimeout.TotalSeconds:N0}s after {closing.Code}.");
            }
            CancelOwned();
            return result;
        }
        try { result = Advance(now); }
        catch (Exception ex) { result = new(confirmed ? OperationStatus.Indeterminate : OperationStatus.Failed, "ReceiveObservationFailed", ex.Message); }
        if (result.Status is OperationStatus.Indeterminate or OperationStatus.Failed)
            warning($"{partner.Name}@{partner.HomeWorldId}: {result.Code}: {result.Message}");
        return result;
    }

    private OperationResult Advance(DateTimeOffset now)
    {
        if (now >= deadline)
        {
            return BeginClosing(new(confirmed ? OperationStatus.Indeterminate : OperationStatus.Failed, "ReceiveDeadline",
                $"Receive deadline {deadline:O} expired. {result.Message}"));
        }
        if (actor == 0) return Refuse("ReceiverUnknown", "No logged-in receiver was observable when armed.");
        if (io.LocalContentId == 0) return result;
        if (io.LocalContentId != actor) return Refuse("ReceiverChanged", "Receiver identity changed during the trade.");
        if (closed || !io.IsTradeOpen)
        {
            if (!opened) return result;
            closed = true; // a later window must never inherit this window's Ready or Confirm
            if (!confirmed) return new(OperationStatus.Cancelled, "ClosedBeforeConfirm", "Window closed without confirmation; retry the remaining manifest.");
            var inventory = io.ObserveTradeableInventory();
            if (inventory.IsAuthoritative && before != null && locked != null)
            {
                var after = TradePlanner.CountExactInventory(inventory.Stacks);
                Evidence = new(partner, now, before, after);
                if (MatchesLocked(after)) return Credit();
            }
            return new(OperationStatus.Pending, "AwaitingReceiveDelta", "Waiting for exact received inventory evidence.");
        }
        if (confirmed)
        {
            // A window is open again, but the sender may have opened its next window in the same frame this one
            // completed, so closure is never observed. Credit lands only on completion: an exact delta equal to the
            // confirmed offer proves this window completed. Only read here; the open window belongs to a later op.
            if (before == null || locked == null || io.ObserveTradeableInventory() is not { IsAuthoritative: true } open)
                return result;
            var after = TradePlanner.CountExactInventory(open.Stacks);
            if (!MatchesLocked(after)) return result;
            closed = true;
            Evidence = new(partner, now, before, after);
            return Credit();
        }
        if (io.ObserveTradePartnerEntityId() is not { } entity) return result;
        if (entity != (uint)partner.GameObjectId || !io.PartnerIsAvailable(partner))
        {
            opened = false;
            before = null;
            locked = null;
            if (foreignEntity != entity)
            {
                var who = io.GetAvailablePartners().FirstOrDefault(p => (uint)p.GameObjectId == entity);
                warning($"Ignoring unexpected sender {who?.Name}@{who?.HomeWorldId} (entity {entity}); expected {partner.Name}@{partner.HomeWorldId}.");
                foreignEntity = entity;
            }
            io.TryCancelTrade(out _); // foreign session: pre-lock only
            return result;
        }
        opened = true;
        foreignEntity = null;
        if (!io.IsRemoteLocked)
        {
            before = null;
            locked = null;
            return result;
        }
        if (io.IsNumericInputOpen) return result;
        var local = io.ObserveOffer();
        var remote = io.ObserveRemoteOffer();
        if (local == null || remote == null || remote.SyncPending || local.SyncPending) return result;
        // Gil is credited exactly like an item under (GilItemId, NQ): the inventory observation reports it that way,
        // so SameOffer, the before/after delta and Received all account for it without a separate path.
        var actual = remote.Items.GroupBy(x => new TradeInventoryKey(x.ItemId, x.IsHighQuality))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        if (remote.Gil > 0) actual[GilKey] = remote.Gil; // a gil-id item slot is refused below
        if (remote.PartnerEntityId != entity || local.PartnerEntityId != entity ||
            remote.Gil < 0 || remote.Gil > TradePlanner.MaximumGilPerTrade ||
            (remote.Gil != 0 && !expectsGil) || (remote.Items.Count != 0 && !expectsItems) ||
            local.Gil != 0 || local.Items.Count != 0 || actual.Count == 0 ||
            remote.Items.Any(i => i.ItemId <= TradePlanner.GilItemId || i.Quantity <= 0) ||
            actual.Any(kv => kv.Value > expected.GetValueOrDefault(kv.Key)))
            return Refuse("LockedManifestMismatch", "The locked partner offer or local consideration differs from the expected manifest.");
        var canReady = io.CanClickReady;
        if (!canReady) awaitingReadyAcknowledgement = false;
        if (canReady && !io.CanConfirmTrade && (before == null || !awaitingReadyAcknowledgement || !SameOffer(actual)))
        {
            var inv = io.ObserveTradeableInventory();
            if (!inv.IsAuthoritative) return result;
            locked = actual;
            before = TradePlanner.CountExactInventory(inv.Stacks);
            if (!io.TryClickReady(out var error))
            {
                before = null;
                if (error.Length != 0) return Refuse("ReadyRejected", error);
            }
            else awaitingReadyAcknowledgement = true;
        }
        else if (before != null && io.CanConfirmTrade)
        {
            if (!SameOffer(actual)) return Refuse("LockedManifestMismatch", "The locked offer changed after Ready.");
            confirmed = true;
            if (!io.TryConfirmTrade(out var error))
            {
                confirmed = false;
                if (error.Length != 0) return Refuse("ConfirmRejected", error);
            }
        }
        return new(OperationStatus.Pending, confirmed ? "AwaitingClosure" : "AwaitingConfirmation", "Waiting for verified transfer.");
    }

    private bool MatchesLocked(IReadOnlyDictionary<TradeInventoryKey, int> after) =>
        before!.Keys.Concat(after.Keys).Concat(locked!.Keys).Distinct().All(k =>
            after.GetValueOrDefault(k) - before.GetValueOrDefault(k) == locked.GetValueOrDefault(k));

    private OperationResult Credit()
    {
        Received = locked;
        return new(OperationStatus.Succeeded, "IncomingDeltaVerified", "The received inventory delta equals the confirmed offer.");
    }

    private bool SameOffer(IReadOnlyDictionary<TradeInventoryKey, int> actual) =>
        locked != null && locked.Count == actual.Count && actual.All(p => locked.GetValueOrDefault(p.Key) == p.Value);

    private bool OwnsOpenWindow() =>
        opened && !confirmed && io.LocalContentId == actor && io.IsTradeOpen &&
        io.ObserveTradePartnerEntityId() == (uint)partner.GameObjectId && io.PartnerIsAvailable(partner);

    private void CancelOwned()
    {
        if (OwnsOpenWindow())
            io.TryCancelUnconfirmedTrade(out _);
    }

    /// <summary>Returns <paramref name="final"/> at once when nothing owned is open; otherwise stays pending
    /// and keeps cancelling until closure is observed.</summary>
    private OperationResult BeginClosing(OperationResult final)
    {
        if (confirmed) return final;
        CancelOwned();
        if (!OwnsOpenWindow()) return final;
        closing = final;
        return new(OperationStatus.Pending, "ClosingTrade", $"Closing the unconfirmed trade window after {final.Code}.");
    }

    private OperationResult Refuse(string code, string reason)
    {
        return BeginClosing(new(confirmed ? OperationStatus.Indeterminate : OperationStatus.Failed, code, reason));
    }
}
