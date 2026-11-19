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
    private bool confirmed, opened, closed, stopped;
    private OperationResult result = new(OperationStatus.Pending, "AwaitingTrade", "Waiting for the expected sender.");

    public TradeReceiveOperation(ITradeIo io, TradePartner partner, IReadOnlyList<TradeOfferedItem> manifest, DateTimeOffset deadline,
        Action<string> warning)
    {
        this.io = io;
        actor = io.LocalContentId;
        this.partner = partner;
        this.deadline = deadline;
        this.warning = warning ?? throw new ArgumentNullException(nameof(warning));
        if (manifest.Count is 0 or > TradePlanner.MaximumTradeSlots || manifest.Any(x => x.ItemId <= 1 || x.Quantity <= 0))
            throw new ArgumentException("Expected one batch of up to five positive item lines.", nameof(manifest));
        expected = manifest.GroupBy(x => new TradeInventoryKey(x.ItemId, x.IsHighQuality))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
    }

    public OperationResult Stop()
    {
        stopped = true;
        return result.IsTerminal || before != null ? result :
            result = new(OperationStatus.Cancelled, "StoppedBeforeLock", "Incoming trade was left untouched.");
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
        try { result = Advance(now); }
        catch (Exception ex) { result = new(before != null ? OperationStatus.Indeterminate : OperationStatus.Failed, "ReceiveObservationFailed", ex.Message); }
        if (result.Status is OperationStatus.Indeterminate or OperationStatus.Failed)
            warning($"{partner.Name}@{partner.HomeWorldId}: {result.Code}: {result.Message}");
        return result;
    }

    private OperationResult Advance(DateTimeOffset now)
    {
        if (now >= deadline)
            return new(before != null ? OperationStatus.Indeterminate : OperationStatus.Failed, "ReceiveDeadline",
                $"Receive deadline {deadline:O} expired. {result.Message}");
        if (actor == 0) return Refuse("ReceiverUnknown", "No logged-in receiver was observable when armed.");
        if (io.LocalContentId == 0) return result;
        if (io.LocalContentId != actor) return Refuse("ReceiverChanged", "Receiver identity changed during the trade.");
        if (closed || !io.IsTradeOpen)
        {
            if (!opened) return result;
            closed = true; // a later window must never inherit this window's Ready or Confirm
            if (before == null) return new(OperationStatus.Cancelled, "ClosedBeforeLock", "Incoming trade closed before Ready.");
            var inventory = io.ObserveTradeableInventory();
            if (inventory.IsAuthoritative && confirmed)
            {
                var after = TradePlanner.CountExactInventory(inventory.Stacks);
                Evidence = new(partner, now, before, after);
                if (before.Keys.Concat(after.Keys).Concat(expected.Keys).Distinct().All(k =>
                    after.GetValueOrDefault(k) - before.GetValueOrDefault(k) == expected.GetValueOrDefault(k)))
                    return new(OperationStatus.Succeeded, "IncomingDeltaVerified", "The received inventory delta equals the manifest.");
            }
            return new(OperationStatus.Pending, "AwaitingReceiveDelta", "Waiting for exact received inventory evidence.");
        }
        opened = true;
        if (io.ObserveTradePartnerEntityId() is not { } entity) return result;
        if (entity != (uint)partner.GameObjectId || !io.PartnerIsAvailable(partner))
            return Refuse("PartnerMismatch", "The live sender differs from the expected character.");
        if (!io.IsRemoteLocked || io.IsNumericInputOpen) return result;
        var local = io.ObserveOffer();
        var remote = io.ObserveRemoteOffer();
        if (local == null || remote == null || remote.SyncPending || local.SyncPending) return result;
        var actual = remote.Items.GroupBy(x => new TradeInventoryKey(x.ItemId, x.IsHighQuality))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        if (remote.PartnerEntityId != entity || local.PartnerEntityId != entity || remote.Gil != 0 ||
            local.Gil != 0 || local.Items.Count != 0 || actual.Count != expected.Count ||
            expected.Any(kv => actual.GetValueOrDefault(kv.Key) != kv.Value))
            return Refuse("LockedManifestMismatch", "The locked partner offer or local consideration differs from the expected manifest.");
        if (before == null && io.CanClickReady)
        {
            var inv = io.ObserveTradeableInventory();
            if (!inv.IsAuthoritative) return result;
            before = TradePlanner.CountExactInventory(inv.Stacks);
            if (!io.TryClickReady(out var error))
            {
                before = null;
                if (error.Length != 0) return Refuse("ReadyRejected", error);
            }
        }
        else if (before != null && !confirmed && !stopped && io.CanConfirmTrade)
        {
            if (io.TryConfirmTrade(out var error)) confirmed = true;
            else if (error.Length != 0) return Refuse("ConfirmRejected", error);
        }
        return new(OperationStatus.Pending, confirmed ? "AwaitingClosure" : "AwaitingConfirmation", "Waiting for verified transfer.");
    }

    private OperationResult Refuse(string code, string reason) =>
        new(before == null ? OperationStatus.Failed : OperationStatus.Indeterminate, code, reason);
}
