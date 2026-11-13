using System.Numerics;
using System.Text.Json;
using Franthropy.Dalamud.Automation.Vendors;
using Franthropy.Dalamud.Automation.Vendors.Coordination;

namespace Franthropy.Dalamud.Tests.Automation.Vendors;

public sealed class GilVendorBuyCoordinatorTests
{
    private const string Context = "CONTEXT";

    [Fact]
    public void Same_vendor_batch_opens_and_reads_once_and_commits_exact_receipts()
    {
        var runtime = new ScriptedRuntime();
        var store = new MemoryStore();
        runtime.OnSubmit = () =>
        {
            Assert.Equal(GilVendorBuyPhase.VerifyReceipt, store.Current!.Phase);
            Assert.NotNull(store.Current.ArmedPurchase);
        };
        var coordinator = new GilVendorBuyCoordinator(store, runtime);

        Assert.True(coordinator.TryStart(
            Plan([Line(1, 3), Line(2, 2)], [Stop(100, 1, 2)]),
            Context,
            out var error), error);
        TickUntilTerminal(coordinator, 30);

        Assert.Equal(GilVendorBuyPhase.Completed, coordinator.ActiveRun!.Phase);
        Assert.Equal(1, runtime.ReachCalls);
        Assert.Equal(1, runtime.ShopReadCalls);
        Assert.Equal(2, runtime.SubmitCalls);
        Assert.Equal([3, 2], coordinator.ActiveRun.Receipts.Select(receipt => receipt.Quantity));
    }

    [Fact]
    public void Delayed_exact_receipt_repairs_run_automatically_without_resubmitting()
    {
        var now = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
        var runtime = new ScriptedRuntime { MutateGilOnSubmit = false };
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), runtime, () => now);
        Assert.True(coordinator.TryStart(Plan([Line(1, 2)], [Stop(100, 1)]), Context, out var error), error);
        TickUntilPhase(coordinator, GilVendorBuyPhase.VerifyReceipt);
        // Exhaust the wall-clock verification budget, settle window and minimum polls.
        for (var index = 0; index < 40 && coordinator.ActiveRun!.Phase == GilVendorBuyPhase.VerifyReceipt; index++)
        {
            now += TimeSpan.FromSeconds(1);
            coordinator.Tick(Context);
        }
        Assert.Equal(GilVendorBuyPhase.ReconcileReceipt, coordinator.ActiveRun!.Phase);

        runtime.Gil -= 20;
        coordinator.Tick(Context);

        Assert.Equal(GilVendorBuyPhase.Completed, coordinator.ActiveRun!.Phase);
        Assert.Equal(1, runtime.SubmitCalls);
        Assert.Equal(2, Assert.Single(coordinator.ActiveRun.Receipts).Quantity);
    }

    [Fact]
    public void Delayed_exact_receipt_reacquires_automation_and_continues_remaining_batches()
    {
        var line = Line(1, 6, targetTotal: 6);
        line.PurchasedQuantity = 2;
        var store = new MemoryStore
        {
            Current = new GilVendorBuyRunSnapshot
            {
                RunId = "run",
                ContextSignature = Context,
                MaximumApprovedGil = 60,
                Phase = GilVendorBuyPhase.Indeterminate,
                Lines = [line],
                Stops = [Stop(100, 1, validated: true)],
                ArmedPurchase = new()
                {
                    ItemId = 1,
                    Quantity = 2,
                    ExpectedGil = 20,
                    ShopRowIndex = 0,
                    BeforeItemCount = 2,
                    BeforeGil = 980,
                    ArmedAtUtc = DateTime.UtcNow,
                },
            },
        };
        var runtime = new ScriptedRuntime { Gil = 960 };
        runtime.Counts[1] = 4;
        var coordinator = new GilVendorBuyCoordinator(store, runtime);

        Assert.Equal(GilVendorBuyPhase.ReconcileReceipt, coordinator.ActiveRun!.Phase);
        Assert.Equal(0, runtime.BeginCalls);
        coordinator.Tick(Context);

        Assert.Equal(GilVendorBuyPhase.RefreshPreconditions, coordinator.ActiveRun!.Phase);
        Assert.Equal(1, runtime.BeginCalls);
        Assert.Equal(0, runtime.SubmitCalls);

        TickUntilTerminal(coordinator, 10);

        Assert.Equal(GilVendorBuyPhase.Completed, coordinator.ActiveRun!.Phase);
        Assert.Equal(1, runtime.SubmitCalls);
        Assert.Equal([2, 2], coordinator.ActiveRun.Receipts.Select(receipt => receipt.Quantity));
    }

    [Theory]
    [InlineData(false, GilVendorBuyPhase.PurchaseLine, 1)]
    [InlineData(true, GilVendorBuyPhase.Indeterminate, 0)]
    public void Restart_reconciles_persisted_armed_intent_without_resubmission(
        bool makeEvidenceIndeterminate,
        GilVendorBuyPhase expectedPhase,
        int expectedReceipts)
    {
        var offer = Offer(1);
        var store = new MemoryStore
        {
            Current = new GilVendorBuyRunSnapshot
            {
                RunId = "run",
                ContextSignature = Context,
                MaximumApprovedGil = 20,
                Phase = GilVendorBuyPhase.VerifyReceipt,
                Lines = [Line(1, 2)],
                Stops = [Stop(100, 1, validated: true)],
                ArmedPurchase = new()
                {
                    ItemId = 1,
                    Quantity = 2,
                    ExpectedGil = 20,
                    ShopRowIndex = 0,
                    BeforeItemCount = 0,
                    BeforeGil = 1_000,
                    ArmedAtUtc = DateTime.UtcNow,
                },
            },
        };
        store.Current.Lines[0].Offer = GilVendorBuyOfferSnapshot.From(offer);
        var runtime = new ScriptedRuntime { Gil = makeEvidenceIndeterminate ? 979UL : 980UL };
        runtime.Counts[1] = 2;

        var now = DateTimeOffset.UtcNow;
        var coordinator = new GilVendorBuyCoordinator(store, runtime, () => now);
        // Uncertain evidence must ride out the verification and late-observation windows, never resubmit.
        for (var index = 0; index < 40 && coordinator.ActiveRun!.Phase is GilVendorBuyPhase.VerifyReceipt or GilVendorBuyPhase.ReconcileReceipt; index++)
        {
            coordinator.Tick(Context);
            now += TimeSpan.FromSeconds(1);
        }

        Assert.Equal(expectedPhase, coordinator.ActiveRun!.Phase);
        Assert.Equal(expectedReceipts, coordinator.ActiveRun.Receipts.Count);
        Assert.Equal(0, runtime.SubmitCalls);
        if (!makeEvidenceIndeterminate)
            Assert.Null(coordinator.ActiveRun.ArmedPurchase);
    }

    private static GilVendorBuyPlan Plan(
        IReadOnlyList<GilVendorBuyLineSnapshot> lines,
        IReadOnlyList<GilVendorBuyStopSnapshot> stops) => new()
    {
        MaximumApprovedGil = lines.Aggregate(0UL, (sum, line) =>
            checked(sum + ((ulong)line.ApprovedQuantity * line.UnitPriceGil))),
        Lines = lines,
        Stops = stops,
    };

    private static GilVendorBuyLineSnapshot Line(uint itemId, int quantity, int? targetTotal = null)
    {
        var offer = Offer(itemId);
        return new()
        {
            ItemId = itemId,
            ItemName = offer.ItemName,
            ApprovedQuantity = quantity,
            TargetTotalQuantity = targetTotal,
            UnitPriceGil = offer.UnitPriceGil,
            ApprovedGilCeiling = checked((ulong)quantity * offer.UnitPriceGil),
            Offer = GilVendorBuyOfferSnapshot.From(offer),
        };
    }

    private static GilVendorBuyStopSnapshot Stop(
        uint npcId,
        uint itemId,
        bool validated = false) => Stop(npcId, [itemId], validated);

    private static GilVendorBuyStopSnapshot Stop(uint npcId, params uint[] itemIds) =>
        Stop(npcId, itemIds, false);

    private static GilVendorBuyStopSnapshot Stop(uint npcId, IReadOnlyList<uint> itemIds, bool validated) => new()
    {
        NpcId = npcId,
        ShopId = 50,
        TerritoryId = 129,
        NpcName = $"Vendor {npcId}",
        ItemIds = [.. itemIds],
        ShopValidated = validated,
        MatchedShopRows = validated
            ? itemIds.ToDictionary(itemId => itemId, itemId => checked((int)itemId - 1))
            : [],
    };

    private static GilVendorOffer Offer(uint itemId, uint npcId = 100) => new(
        itemId,
        $"Item {itemId}",
        1,
        itemId == 1 ? 10u : 20u,
        50,
        itemId - 1,
        npcId,
        $"Vendor {npcId}",
        129,
        new Vector3(1, 2, 3),
        [2]);

    private static void TickUntilPhase(GilVendorBuyCoordinator coordinator, GilVendorBuyPhase phase)
    {
        for (var index = 0; index < 20 && coordinator.ActiveRun!.Phase != phase; index++)
            coordinator.Tick(Context);
        Assert.Equal(phase, coordinator.ActiveRun!.Phase);
    }

    private static void TickUntilTerminal(GilVendorBuyCoordinator coordinator, int maximumTicks)
    {
        for (var index = 0; index < maximumTicks && coordinator.IsRunning; index++)
            coordinator.Tick(Context);
        Assert.False(coordinator.IsRunning);
    }

    private sealed class MemoryStore : IGilVendorBuyRunStore
    {
        public GilVendorBuyRunSnapshot? Current { get; set; }
        public GilVendorBuyRunSnapshot? LoadCurrent() => Current;
        public void Save(GilVendorBuyRunSnapshot snapshot) => Current = snapshot;
    }

    private sealed class ScriptedRuntime : IGilVendorBuyRuntime
    {
        public Dictionary<uint, int> Counts { get; } = [];
        public ulong Gil { get; set; } = 1_000_000;
        public bool MutateItemOnSubmit { get; set; } = true;
        public bool MutateGilOnSubmit { get; set; } = true;
        public bool SubmitSucceeds { get; set; } = true;
        public Exception? SubmitException { get; set; }
        public Exception? BeginException { get; set; }
        public int BeginCalls { get; private set; }
        public int EndCalls { get; private set; }
        public int ResetCalls { get; private set; }
        public int ReachCalls { get; private set; }
        public int ShopReadCalls { get; private set; }
        public int SubmitCalls { get; private set; }
        public Queue<GilVendorReachResult> ReachResults { get; } = [];
        public Queue<bool> CapacityResults { get; } = [];
        public IReadOnlyList<GilVendorShopRow> ShopRows { get; set; } = [new(0, 1, 10), new(1, 2, 20)];
        public GilVendorShopReadResult? ShopReadOverride { get; set; }
        public Action? OnSubmit { get; set; }
        public IReadOnlyDictionary<uint, int>? LastCapacityQuantities { get; private set; }

        public GilVendorInventorySnapshot CaptureInventory(IReadOnlyCollection<uint> itemIds) => new(
            true,
            Gil,
            itemIds.ToDictionary(itemId => itemId, itemId => Counts.GetValueOrDefault(itemId)),
            "Inventory ready.");

        public bool HasCapacity(IReadOnlyDictionary<uint, int> quantities, out string message)
        {
            LastCapacityQuantities = new Dictionary<uint, int>(quantities);
            var result = CapacityResults.Count == 0 || CapacityResults.Dequeue();
            message = result ? "Capacity ready." : "Player inventory has no safe capacity.";
            return result;
        }

        public GilVendorReachResult AdvanceToOpenShop(GilVendorOffer offer)
        {
            ReachCalls++;
            return ReachResults.Count == 0
                ? new(GilVendorReachState.ShopOpen, "Shop open.")
                : ReachResults.Dequeue();
        }

        public void ResetVendorApproach() => ResetCalls++;

        public GilVendorShopReadResult ReadShopRows()
        {
            ShopReadCalls++;
            return ShopReadOverride ?? GilVendorShopReadResult.Success(ShopRows);
        }

        public bool TrySubmitPurchase(GilVendorShopRow row, uint quantity, out string error)
        {
            SubmitCalls++;
            if (SubmitException is not null)
                throw SubmitException;
            if (!SubmitSucceeds)
            {
                error = "Purchase submission was rejected.";
                return false;
            }
            OnSubmit?.Invoke();
            if (MutateItemOnSubmit)
                Counts[row.ItemId] = checked(Counts.GetValueOrDefault(row.ItemId) + (int)quantity);
            if (MutateGilOnSubmit)
                Gil -= row.UnitPriceGil * quantity;
            error = string.Empty;
            return true;
        }

        public bool TryConfirmPurchasePrompt() => false;
        public int ResolveMaximumBatch(uint itemId) => 99;
        public void CloseShop() { }
        public void BeginAutomation()
        {
            BeginCalls++;
            if (BeginException is not null)
                throw BeginException;
        }
        public void EndAutomation() => EndCalls++;
    }
}
