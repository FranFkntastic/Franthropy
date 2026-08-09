using System.Numerics;
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
    public void Pause_resume_and_context_mismatch_preserve_phase_and_refuse_wrong_context()
    {
        var runtime = new ScriptedRuntime();
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), runtime);
        Assert.True(coordinator.TryStart(Plan([Line(1, 2)], [Stop(100, 1)]), Context, out var error), error);

        Assert.True(coordinator.Pause());
        Assert.False(coordinator.Resume("WRONG", out error));
        Assert.True(coordinator.Resume(Context, out error), error);
        coordinator.Tick("WRONG");

        Assert.Equal(GilVendorBuyPhase.Paused, coordinator.ActiveRun!.Phase);
        Assert.Equal(GilVendorBuyPhase.RefreshPreconditions, coordinator.ActiveRun.ResumePhase);
        Assert.Equal(0, runtime.ReachCalls);
        Assert.Equal(0, runtime.SubmitCalls);
        Assert.True(coordinator.Resume(Context, out error), error);
    }

    [Fact]
    public void Stop_with_armed_verified_purchase_records_receipt_then_stops()
    {
        var runtime = new ScriptedRuntime();
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), runtime);
        Assert.True(coordinator.TryStart(Plan([Line(1, 2)], [Stop(100, 1)]), Context, out var error), error);
        TickUntilPhase(coordinator, GilVendorBuyPhase.VerifyReceipt);

        Assert.True(coordinator.Stop());
        coordinator.Tick(Context);

        Assert.Equal(GilVendorBuyPhase.Stopped, coordinator.ActiveRun!.Phase);
        Assert.Single(coordinator.ActiveRun.Receipts);
        Assert.Null(coordinator.ActiveRun.ArmedPurchase);
    }

    [Fact]
    public void Stop_with_armed_no_mutation_waits_four_seconds_then_stops_without_receipt()
    {
        var now = new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero);
        var runtime = new ScriptedRuntime { MutateOnSubmit = false };
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), runtime, () => now);
        Assert.True(coordinator.TryStart(Plan([Line(1, 2)], [Stop(100, 1)]), Context, out var error), error);
        TickUntilPhase(coordinator, GilVendorBuyPhase.VerifyReceipt);

        Assert.True(coordinator.Stop());
        coordinator.Tick(Context);
        Assert.Equal(GilVendorBuyPhase.VerifyReceipt, coordinator.ActiveRun!.Phase);
        now += TimeSpan.FromSeconds(4);
        coordinator.Tick(Context);

        Assert.Equal(GilVendorBuyPhase.Stopped, coordinator.ActiveRun.Phase);
        Assert.Empty(coordinator.ActiveRun.Receipts);
        Assert.Null(coordinator.ActiveRun.ArmedPurchase);
        Assert.Equal(1, runtime.SubmitCalls);
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

        var coordinator = new GilVendorBuyCoordinator(store, runtime);
        coordinator.Tick(Context);

        Assert.Equal(expectedPhase, coordinator.ActiveRun!.Phase);
        Assert.Equal(expectedReceipts, coordinator.ActiveRun.Receipts.Count);
        Assert.Equal(0, runtime.SubmitCalls);
        if (!makeEvidenceIndeterminate)
            Assert.Null(coordinator.ActiveRun.ArmedPurchase);
    }

    [Fact]
    public void Unreachable_vendor_replans_without_expanding_quantity_or_gil_ceiling()
    {
        var runtime = new ScriptedRuntime();
        runtime.ReachResults.Enqueue(new(GilVendorReachState.Unavailable, "First unavailable."));
        runtime.ReachResults.Enqueue(new(GilVendorReachState.ShopOpen, "Alternative open."));
        var line = Line(1, 4);
        line.AlternativeOffers.Add(GilVendorBuyOfferSnapshot.From(Offer(1, 200)));
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), runtime);
        var plan = Plan([line], [Stop(100, 1)]);
        plan = new GilVendorBuyPlan
        {
            MaximumApprovedGil = plan.MaximumApprovedGil,
            Lines = plan.Lines,
            Stops = plan.Stops,
            FallbackReplanner = request => new(
                [Stop(200, 1)],
                [new(1, request.Lines.Single().AlternativeOffers.Single())],
                "Replanned without expanding ceilings."),
        };

        Assert.True(coordinator.TryStart(plan, Context, out var error), error);
        TickUntilTerminal(coordinator, 30);

        Assert.Equal(GilVendorBuyPhase.Completed, coordinator.ActiveRun!.Phase);
        Assert.Equal(4, coordinator.ActiveRun.Lines[0].ApprovedQuantity);
        Assert.Equal(40UL, coordinator.ActiveRun.Lines[0].ApprovedGilCeiling);
        Assert.Equal(40UL, coordinator.ActiveRun.MaximumApprovedGil);
        Assert.Equal(4, coordinator.ActiveRun.Receipts.Sum(receipt => receipt.Quantity));
        Assert.Equal(200u, coordinator.ActiveRun.Lines[0].Offer!.NpcId);
        Assert.Equal(2, runtime.ReachCalls);
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

    private static GilVendorBuyLineSnapshot Line(uint itemId, int quantity)
    {
        var offer = Offer(itemId);
        return new()
        {
            ItemId = itemId,
            ItemName = offer.ItemName,
            ApprovedQuantity = quantity,
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
        public bool MutateOnSubmit { get; set; } = true;
        public int ReachCalls { get; private set; }
        public int ShopReadCalls { get; private set; }
        public int SubmitCalls { get; private set; }
        public Queue<GilVendorReachResult> ReachResults { get; } = [];
        public Action? OnSubmit { get; set; }

        public GilVendorInventorySnapshot CaptureInventory(IReadOnlyCollection<uint> itemIds) => new(
            true,
            Gil,
            itemIds.ToDictionary(itemId => itemId, itemId => Counts.GetValueOrDefault(itemId)),
            "Inventory ready.");

        public bool HasCapacity(IReadOnlyDictionary<uint, int> quantities, out string message)
        {
            message = "Capacity ready.";
            return true;
        }

        public GilVendorReachResult AdvanceToOpenShop(GilVendorOffer offer)
        {
            ReachCalls++;
            return ReachResults.Count == 0
                ? new(GilVendorReachState.ShopOpen, "Shop open.")
                : ReachResults.Dequeue();
        }

        public void ResetVendorApproach() { }

        public GilVendorShopReadResult ReadShopRows()
        {
            ShopReadCalls++;
            return GilVendorShopReadResult.Success([new(0, 1, 10), new(1, 2, 20)]);
        }

        public bool TrySubmitPurchase(GilVendorShopRow row, uint quantity, out string error)
        {
            SubmitCalls++;
            OnSubmit?.Invoke();
            if (MutateOnSubmit)
            {
                Counts[row.ItemId] = checked(Counts.GetValueOrDefault(row.ItemId) + (int)quantity);
                Gil -= row.UnitPriceGil * quantity;
            }
            error = string.Empty;
            return true;
        }

        public bool TryConfirmPurchasePrompt() => false;
        public int ResolveMaximumBatch(uint itemId) => 99;
        public void CloseShop() { }
        public void BeginAutomation() { }
        public void EndAutomation() { }
    }
}
