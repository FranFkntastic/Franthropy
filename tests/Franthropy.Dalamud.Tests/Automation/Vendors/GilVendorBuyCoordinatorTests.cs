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
    public void Submission_rejection_clears_armed_intent_and_fails_truthfully()
    {
        var runtime = new ScriptedRuntime { SubmitSucceeds = false };
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), runtime);
        Assert.True(coordinator.TryStart(Plan([Line(1, 2)], [Stop(100, 1)]), Context, out var error), error);

        TickUntilTerminal(coordinator, 10);

        Assert.Equal(GilVendorBuyPhase.Failed, coordinator.ActiveRun!.Phase);
        Assert.Null(coordinator.ActiveRun.ArmedPurchase);
        Assert.Contains("rejected", coordinator.ActiveRun.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(coordinator.ActiveRun.Receipts);
    }

    [Fact]
    public void Submission_exception_clears_armed_intent_and_fails_truthfully()
    {
        var runtime = new ScriptedRuntime { SubmitException = new InvalidOperationException("submit exploded") };
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), runtime);
        Assert.True(coordinator.TryStart(Plan([Line(1, 2)], [Stop(100, 1)]), Context, out var error), error);

        TickUntilTerminal(coordinator, 10);

        Assert.Equal(GilVendorBuyPhase.Failed, coordinator.ActiveRun!.Phase);
        Assert.Null(coordinator.ActiveRun.ArmedPurchase);
        Assert.Contains("submit exploded", coordinator.ActiveRun.Message);
        Assert.Empty(coordinator.ActiveRun.Receipts);
    }

    [Fact]
    public void Second_no_mutation_timeout_fails_without_another_retry()
    {
        var now = new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero);
        var runtime = new ScriptedRuntime { MutateItemOnSubmit = false, MutateGilOnSubmit = false };
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), runtime, () => now);
        Assert.True(coordinator.TryStart(Plan([Line(1, 2)], [Stop(100, 1)]), Context, out var error), error);
        TickUntilPhase(coordinator, GilVendorBuyPhase.VerifyReceipt);

        now += TimeSpan.FromSeconds(4);
        coordinator.Tick(Context);
        Assert.Equal(GilVendorBuyPhase.PurchaseLine, coordinator.ActiveRun!.Phase);
        coordinator.Tick(Context);
        Assert.Equal(2, runtime.SubmitCalls);
        now += TimeSpan.FromSeconds(4);
        coordinator.Tick(Context);

        Assert.Equal(GilVendorBuyPhase.Failed, coordinator.ActiveRun.Phase);
        Assert.Equal(2, runtime.SubmitCalls);
        Assert.Empty(coordinator.ActiveRun.Receipts);
    }

    [Fact]
    public void Indeterminate_evidence_stops_without_retry()
    {
        var runtime = new ScriptedRuntime { MutateGilOnSubmit = false };
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), runtime);
        Assert.True(coordinator.TryStart(Plan([Line(1, 2)], [Stop(100, 1)]), Context, out var error), error);

        TickUntilTerminal(coordinator, 10);

        Assert.Equal(GilVendorBuyPhase.Indeterminate, coordinator.ActiveRun!.Phase);
        Assert.Equal(1, runtime.SubmitCalls);
        Assert.Empty(coordinator.ActiveRun.Receipts);
    }

    [Fact]
    public void Delayed_exact_receipt_repairs_indeterminate_run_without_resubmitting()
    {
        var runtime = new ScriptedRuntime { MutateGilOnSubmit = false };
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), runtime);
        Assert.True(coordinator.TryStart(Plan([Line(1, 2)], [Stop(100, 1)]), Context, out var error), error);
        TickUntilTerminal(coordinator, 10);
        Assert.Equal(GilVendorBuyPhase.Indeterminate, coordinator.ActiveRun!.Phase);

        runtime.Gil -= 20;
        Assert.True(coordinator.TryReconcileIndeterminate(out var message), message);

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

        Assert.True(coordinator.TryReconcileIndeterminate(out var message), message);

        Assert.Equal(GilVendorBuyPhase.RefreshPreconditions, coordinator.ActiveRun!.Phase);
        Assert.Equal(1, runtime.BeginCalls);
        Assert.Equal(0, runtime.SubmitCalls);

        TickUntilTerminal(coordinator, 10);

        Assert.Equal(GilVendorBuyPhase.Completed, coordinator.ActiveRun!.Phase);
        Assert.Equal(1, runtime.SubmitCalls);
        Assert.Equal([2, 2], coordinator.ActiveRun.Receipts.Select(receipt => receipt.Quantity));
    }

    [Fact]
    public void Delayed_exact_receipt_pauses_only_when_automation_cannot_be_reacquired()
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
        var runtime = new ScriptedRuntime
        {
            Gil = 960,
            BeginException = new InvalidOperationException("another automation owns the client"),
        };
        runtime.Counts[1] = 4;
        var coordinator = new GilVendorBuyCoordinator(store, runtime);

        Assert.True(coordinator.TryReconcileIndeterminate(out var message), message);

        Assert.Equal(GilVendorBuyPhase.Paused, coordinator.ActiveRun!.Phase);
        Assert.Equal(GilVendorBuyPhase.RefreshPreconditions, coordinator.ActiveRun.ResumePhase);
        Assert.Equal(1, runtime.BeginCalls);
        Assert.Equal(2, Assert.Single(coordinator.ActiveRun.Receipts).Quantity);
        Assert.Contains("another automation owns the client", coordinator.ActiveRun.Message);
    }

    [Fact]
    public void Delayed_receipt_checks_every_target_before_completing_multi_line_run()
    {
        var runtime = new ScriptedRuntime { MutateGilOnSubmit = false };
        runtime.Counts[2] = 5;
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), runtime);
        Assert.True(coordinator.TryStart(
            Plan(
                [Line(1, 2, targetTotal: 2), Line(2, 5, targetTotal: 5)],
                [Stop(100, 1)]),
            Context,
            out var error), error);
        TickUntilTerminal(coordinator, 10);
        Assert.Equal(GilVendorBuyPhase.Indeterminate, coordinator.ActiveRun!.Phase);

        runtime.Gil -= 20;
        Assert.True(coordinator.TryReconcileIndeterminate(out var message), message);

        Assert.Equal(GilVendorBuyPhase.Completed, coordinator.ActiveRun!.Phase);
        Assert.Equal(1, runtime.SubmitCalls);
    }

    [Fact]
    public void Delayed_receipt_fails_when_an_unavailable_target_remains_unmet()
    {
        var unavailableLine = Line(1, 1, targetTotal: 1);
        unavailableLine.VendorUnavailable = true;
        unavailableLine.Status = "Ixali shop was unavailable.";
        var purchasedLine = Line(2, 2, targetTotal: 2);
        var store = new MemoryStore
        {
            Current = new GilVendorBuyRunSnapshot
            {
                RunId = "run",
                ContextSignature = Context,
                MaximumApprovedGil = 30,
                Phase = GilVendorBuyPhase.Indeterminate,
                Lines = [unavailableLine, purchasedLine],
                Stops = [Stop(100, [1u, 2u], validated: true)],
                ArmedPurchase = new()
                {
                    ItemId = 2,
                    Quantity = 2,
                    ExpectedGil = 40,
                    ShopRowIndex = 0,
                    BeforeItemCount = 0,
                    BeforeGil = 1_000,
                    ArmedAtUtc = DateTime.UtcNow,
                },
            },
        };
        var runtime = new ScriptedRuntime { Gil = 960 };
        runtime.Counts[2] = 2;
        var coordinator = new GilVendorBuyCoordinator(store, runtime);

        Assert.True(coordinator.TryReconcileIndeterminate(out var message), message);

        Assert.Equal(GilVendorBuyPhase.Failed, coordinator.ActiveRun!.Phase);
        Assert.Equal(2, Assert.Single(coordinator.ActiveRun.Receipts).Quantity);
        Assert.Contains("Ixali shop was unavailable", coordinator.ActiveRun.Message);
        Assert.Equal(0, runtime.SubmitCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Shop_validation_failure_fails_before_purchase(bool readFails)
    {
        var runtime = new ScriptedRuntime();
        if (readFails)
            runtime.ShopReadOverride = GilVendorShopReadResult.Fail("ReadFailed", "Shop read failed.");
        else
            runtime.ShopRows = [];
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), runtime);
        Assert.True(coordinator.TryStart(Plan([Line(1, 2)], [Stop(100, 1)]), Context, out var error), error);

        TickUntilTerminal(coordinator, 10);

        Assert.Equal(GilVendorBuyPhase.Failed, coordinator.ActiveRun!.Phase);
        Assert.Equal(1, runtime.ShopReadCalls);
        Assert.Equal(0, runtime.SubmitCalls);
    }

    [Fact]
    public void Capacity_loss_after_start_pauses_before_vendor_mutation()
    {
        var runtime = new ScriptedRuntime();
        runtime.CapacityResults.Enqueue(true);
        runtime.CapacityResults.Enqueue(false);
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), runtime);

        Assert.True(coordinator.TryStart(Plan([Line(1, 2)], [Stop(100, 1)]), Context, out var error), error);
        coordinator.Tick(Context);

        Assert.Equal(GilVendorBuyPhase.Paused, coordinator.ActiveRun!.Phase);
        Assert.Equal(0, runtime.ReachCalls);
        Assert.Equal(0, runtime.SubmitCalls);
    }

    [Fact]
    public void Quantity_above_shop_limit_splits_into_exact_receipts()
    {
        var runtime = new ScriptedRuntime();
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), runtime);
        Assert.True(coordinator.TryStart(Plan([Line(1, 120)], [Stop(100, 1)]), Context, out var error), error);

        TickUntilTerminal(coordinator, 20);

        Assert.Equal([99, 21], coordinator.ActiveRun!.Receipts.Select(receipt => receipt.Quantity));
        Assert.Equal(
            1_200UL,
            coordinator.ActiveRun.Receipts.Aggregate(0UL, (sum, receipt) => sum + receipt.SpentGil));
        Assert.Equal(2, runtime.SubmitCalls);
        Assert.Equal(1, runtime.ShopReadCalls);
        Assert.Equal(1, runtime.ReachCalls);
    }

    [Fact]
    public void Pending_evidence_within_timeout_can_later_verify()
    {
        var now = new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero);
        var runtime = new ScriptedRuntime { MutateItemOnSubmit = false, MutateGilOnSubmit = false };
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), runtime, () => now);
        Assert.True(coordinator.TryStart(Plan([Line(1, 2)], [Stop(100, 1)]), Context, out var error), error);
        TickUntilPhase(coordinator, GilVendorBuyPhase.VerifyReceipt);

        coordinator.Tick(Context);
        Assert.Equal(GilVendorBuyPhase.VerifyReceipt, coordinator.ActiveRun!.Phase);
        Assert.Empty(coordinator.ActiveRun.Receipts);
        runtime.Counts[1] = 2;
        runtime.Gil -= 20;
        now += TimeSpan.FromSeconds(3);
        coordinator.Tick(Context);

        Assert.Equal(GilVendorBuyPhase.PurchaseLine, coordinator.ActiveRun.Phase);
        Assert.Single(coordinator.ActiveRun.Receipts);
        Assert.Equal(1, runtime.SubmitCalls);
    }

    [Fact]
    public void Target_mode_does_not_double_subtract_observed_inventory()
    {
        var runtime = new ScriptedRuntime();
        runtime.Counts[1] = 4;
        var store = new MemoryStore();
        var coordinator = new GilVendorBuyCoordinator(store, runtime);
        Assert.True(coordinator.TryStart(Plan([Line(1, 6, targetTotal: 10)], [Stop(100, 1)]), Context, out var error), error);

        TickUntilTerminal(coordinator, 10);

        Assert.Equal(GilVendorBuyPhase.Completed, coordinator.ActiveRun!.Phase);
        Assert.Equal([6], coordinator.ActiveRun.Receipts.Select(receipt => receipt.Quantity));
        Assert.Equal(6, coordinator.ActiveRun.Lines[0].PurchasedQuantity);
        Assert.Equal(10, store.Current!.Lines[0].TargetTotalQuantity);
        Assert.Equal(10, runtime.Counts[1]);
    }

    [Fact]
    public void Target_mode_preflight_uses_live_need_without_shrinking_approved_ceilings()
    {
        var runtime = new ScriptedRuntime { Gil = 30 };
        runtime.Counts[1] = 7;
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), runtime);

        Assert.True(coordinator.TryStart(
            Plan([Line(1, 6, targetTotal: 10)], [Stop(100, 1)]),
            Context,
            out var error), error);

        Assert.Equal(3, runtime.LastCapacityQuantities![1]);
        Assert.Equal(60UL, coordinator.ActiveRun!.MaximumApprovedGil);
        Assert.Equal(60UL, coordinator.ActiveRun.Lines[0].ApprovedGilCeiling);
    }

    [Fact]
    public void Target_mode_mid_run_gain_clamps_purchase_and_completes_ready()
    {
        var runtime = new ScriptedRuntime();
        runtime.Counts[1] = 4;
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), runtime);
        Assert.True(coordinator.TryStart(Plan([Line(1, 6, targetTotal: 10)], [Stop(100, 1)]), Context, out var error), error);
        TickUntilPhase(coordinator, GilVendorBuyPhase.PurchaseLine);

        runtime.Counts[1] = 6;
        TickUntilTerminal(coordinator, 10);

        Assert.Equal(GilVendorBuyPhase.Completed, coordinator.ActiveRun!.Phase);
        Assert.Equal([4], coordinator.ActiveRun.Receipts.Select(receipt => receipt.Quantity));
        Assert.Equal(4, coordinator.ActiveRun.Lines[0].PurchasedQuantity);
        Assert.Equal("Ready", coordinator.ActiveRun.Lines[0].Status);
        Assert.Equal(10, runtime.Counts[1]);
    }

    [Fact]
    public void Absent_target_mode_buys_approved_delta_regardless_of_observed_inventory()
    {
        var runtime = new ScriptedRuntime();
        runtime.Counts[1] = 4;
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), runtime);
        Assert.True(coordinator.TryStart(Plan([Line(1, 6)], [Stop(100, 1)]), Context, out var error), error);

        TickUntilTerminal(coordinator, 10);

        Assert.Equal([6], coordinator.ActiveRun!.Receipts.Select(receipt => receipt.Quantity));
        Assert.Null(coordinator.ActiveRun.Lines[0].TargetTotalQuantity);
        Assert.Equal(10, runtime.Counts[1]);
    }

    [Fact]
    public void Snapshot_round_trip_without_target_preserves_exact_purchase_mode()
    {
        const string legacyJson = """
            {"Lines":[{"ItemId":1,"ApprovedQuantity":6}]}
            """;

        var loaded = JsonSerializer.Deserialize<GilVendorBuyRunSnapshot>(legacyJson)!;
        var roundTripped = JsonSerializer.Deserialize<GilVendorBuyRunSnapshot>(
            JsonSerializer.Serialize(loaded))!;

        Assert.Null(roundTripped.Lines.Single().TargetTotalQuantity);
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
    public void Blank_context_is_rejected_and_cannot_bypass_pause_or_resume_guard()
    {
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), new ScriptedRuntime());

        Assert.False(coordinator.TryStart(Plan([Line(1, 2)], [Stop(100, 1)]), " ", out _));
        Assert.Null(coordinator.ActiveRun);
        Assert.True(coordinator.TryStart(Plan([Line(1, 2)], [Stop(100, 1)]), Context, out var error), error);

        coordinator.Tick(string.Empty);

        Assert.Equal(GilVendorBuyPhase.Paused, coordinator.ActiveRun!.Phase);
        Assert.False(coordinator.Resume("\t", out _));
    }

    [Fact]
    public void Run_snapshots_are_cloned_at_public_and_store_boundaries()
    {
        var store = new MemoryStore();
        var coordinator = new GilVendorBuyCoordinator(store, new ScriptedRuntime());
        Assert.True(coordinator.TryStart(Plan([Line(1, 2)], [Stop(100, 1)]), Context, out var error), error);
        var exposed = coordinator.ActiveRun!;
        var saved = store.Current!;

        exposed.MaximumApprovedGil = ulong.MaxValue;
        exposed.Lines[0].ApprovedQuantity = int.MaxValue;
        exposed.Receipts.Add(new() { ItemId = 1, Quantity = 999 });
        saved.MaximumApprovedGil = ulong.MaxValue;
        saved.Lines[0].ApprovedGilCeiling = ulong.MaxValue;

        Assert.Equal(20UL, coordinator.ActiveRun!.MaximumApprovedGil);
        Assert.Equal(2, coordinator.ActiveRun.Lines[0].ApprovedQuantity);
        Assert.Equal(20UL, coordinator.ActiveRun.Lines[0].ApprovedGilCeiling);
        Assert.Empty(coordinator.ActiveRun.Receipts);

        var reloaded = new GilVendorBuyCoordinator(store, new ScriptedRuntime());
        store.Current!.MaximumApprovedGil = 0;
        Assert.Equal(ulong.MaxValue, reloaded.ActiveRun!.MaximumApprovedGil);
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
        var runtime = new ScriptedRuntime { MutateItemOnSubmit = false, MutateGilOnSubmit = false };
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

    [Fact]
    public void Unreachable_vendor_without_alternative_fails_instead_of_completing_zero()
    {
        var runtime = new ScriptedRuntime();
        runtime.ReachResults.Enqueue(new(
            GilVendorReachState.Unavailable,
            "Could not summon a mount after repeated automatic attempts."));
        var coordinator = new GilVendorBuyCoordinator(new MemoryStore(), runtime);

        Assert.True(coordinator.TryStart(
            Plan([Line(1, 4, targetTotal: 4)], [Stop(100, 1)]),
            Context,
            out var error), error);
        TickUntilTerminal(coordinator, 10);

        Assert.Equal(GilVendorBuyPhase.Failed, coordinator.ActiveRun!.Phase);
        Assert.True(coordinator.ActiveRun.Lines[0].VendorUnavailable);
        Assert.Empty(coordinator.ActiveRun.Receipts);
        Assert.Contains("unmet vendor line", coordinator.ActiveRun.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Could not summon a mount", coordinator.ActiveRun.Message);
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

        public void ResetVendorApproach() { }

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
        public void EndAutomation() { }
    }
}
