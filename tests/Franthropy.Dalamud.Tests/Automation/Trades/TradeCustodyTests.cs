using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation;
using Franthropy.Dalamud.Automation.Trades;

namespace Franthropy.Dalamud.Tests.Automation.Trades;

public sealed class TradeCustodyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly TradeOfferedItem Expected = new(42, true, 3);

    [Theory]
    [InlineData("quality")]
    [InlineData("quantity")]
    [InlineData("gil")]
    public void ChangedLockedOfferCannotBeConfirmed(string change)
    {
        var io = DispatchProxy.Create<ITradeIo, TradeFixture>();
        var fixture = (TradeFixture)(object)io;
        var receive = new TradeReceiveOperation(io, new(17, "Expected", 2), [Expected], Now.AddMinutes(1), _ => { });
        receive.Tick(Now);
        Assert.Equal(1, fixture.Ready);
        switch (change)
        {
            case "quality": fixture.Remote = Expected with { IsHighQuality = false }; break;
            case "quantity": fixture.Remote = Expected with { Quantity = 4 }; break;
            case "gil": fixture.RemoteGil++; break;
        }
        Assert.Equal(OperationStatus.Failed, receive.Tick(Now).Status);
        Assert.Equal(0, fixture.Confirms);
    }

    [Fact]
    public void ConfirmedSubsetReturnsOnlyExactlyObservedIncrease()
    {
        var io = DispatchProxy.Create<ITradeIo, TradeFixture>();
        var fixture = (TradeFixture)(object)io;
        var receive = new TradeReceiveOperation(io, new(17, "Expected", 2), [Expected with { Quantity = 9 }, new(43, false, 1)], Now.AddMinutes(1), _ => { });
        receive.Tick(Now);
        receive.Tick(Now);
        receive.Tick(Now);
        Assert.Equal(1, fixture.Confirms);
        fixture.Open = false;
        fixture.Quantity = 12;
        Assert.Equal(OperationStatus.Pending, receive.Tick(Now).Status);
        fixture.Quantity = 13;
        Assert.Equal(OperationStatus.Succeeded, receive.Tick(Now).Status);
        Assert.Equal(new KeyValuePair<TradeInventoryKey, int>(new(42, true), 3), Assert.Single(receive.Received!));
    }

    [Fact]
    public void VisibleProvisionalSlotCannotCompleteOrRepeatPendingQuantity()
    {
        var io = DispatchProxy.Create<ITradeIo, TradeFixture>();
        var fixture = (TradeFixture)(object)io;
        var operation = new TradeOfferLineOperation(io, new(0, 0, 42, "Ore", true, 3, 10),
            io.ObserveOffer()!, Now.AddMinutes(1));
        Assert.Equal(TradeOfferLineState.WaitingForQuantityInput, operation.Advance(Now).State);
        fixture.Local = [Expected];
        Assert.False(operation.Advance(Now).IsCompleted);
        fixture.Numeric = true;
        operation.Advance(Now);
        Assert.False(operation.Advance(Now).IsCompleted);
        fixture.Numeric = false;
        Assert.True(operation.Advance(Now).IsCompleted);
        Assert.Equal(1, fixture.Quantities);
        Assert.Equal(1, fixture.Offers);
    }

    [Fact]
    public void InventoryDeltaAndCompletedBatchRequireExactEvidence()
    {
        var queue = new List<TradeItem>
        {
            new() { ItemId = 100, ItemName = "Cobalt Ingot", Quantity = 7 },
        };
        var before = new List<TradeInventoryStack>
        {
            new(0, 0, 100, "Cobalt Ingot", false, 5),
            new(0, 1, 100, "Cobalt Ingot", false, 2),
        };
        var batch = TradePlanner.BuildNextBatch(queue, before, maximumSlots: 1);
        var after = new List<TradeInventoryStack>
        {
            new(0, 1, 100, "Cobalt Ingot", false, 2),
        };

        Assert.True(TradePlanner.HasExpectedInventoryDelta(batch, after, out _));
        TradePlanner.ApplyCompletedBatch(queue, batch);
        Assert.Equal(2, Assert.Single(queue).Quantity);

        var unchanged = TradePlanner.HasExpectedInventoryDelta(batch, before, out var diagnostic);
        Assert.False(unchanged);
        Assert.Contains("observed 0", diagnostic);
    }

    [Theory]
    [InlineData("superset")]
    [InlineData("foreign")]
    [InlineData("over")]
    [InlineData("gil")]
    public void RefusesUnacceptableOfferBeforeReady(string change)
    {
        var io = DispatchProxy.Create<ITradeIo, TradeFixture>();
        var f = (TradeFixture)(object)io;
        if (change == "superset") f.Extra = [new(55, false, 1)];
        if (change == "foreign") f.Remote = Expected with { ItemId = 43 };
        if (change == "over") f.Remote = Expected with { Quantity = 4 };
        if (change == "gil") f.RemoteGil = 1;
        var op = new TradeReceiveOperation(io, Partner, [Expected], Now.AddMinutes(1), _ => { });
        Assert.Equal(OperationStatus.Failed, op.Tick(Now).Status);
        Assert.Equal(0, f.Ready);
        Assert.Equal(0, f.Confirms);
    }

    [Fact]
    public void ForeignWindowsDoNotConsumeWaitAndRelockRefreshesBaseline()
    {
        var io = DispatchProxy.Create<ITradeIo, TradeFixture>();
        var f = (TradeFixture)(object)io;
        var warnings = new List<string>();
        var op = new TradeReceiveOperation(io, Partner, [Expected], Now.AddMinutes(1), warnings.Add);
        f.Visible = Partner with { HomeWorldId = 4 };
        Assert.Equal(OperationStatus.Pending, op.Tick(Now).Status);
        Assert.Contains("Expected@4", Assert.Single(warnings));
        Assert.Equal(0, f.Ready);
        f.Visible = Partner;
        f.Open = true;
        op.Tick(Now);
        f.Locked = f.LocalReady = false;
        op.Tick(Now);
        f.Locked = true;
        f.Quantity = 20;
        f.Remote = Expected with { Quantity = 2 };
        op.Tick(Now);
        op.Tick(Now);
        Assert.Equal(2, f.Ready);
        Assert.Equal(1, f.Confirms);
        f.Open = false;
        f.Quantity = 22;
        Assert.Equal(OperationStatus.Succeeded, op.Tick(Now).Status);
        Assert.Equal(2, Assert.Single(op.Received!).Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnconfirmedReceiveCannotTransfer(bool stop)
    {
        var io = DispatchProxy.Create<ITradeIo, TradeFixture>();
        var f = (TradeFixture)(object)io;
        var op = new TradeReceiveOperation(io, Partner, [Expected], Now.AddMinutes(1), _ => { });
        op.Tick(Now);
        if (!stop) f.Open = false;
        Assert.Equal(OperationStatus.Cancelled, (stop ? op.Stop() : op.Tick(Now)).Status);
        Assert.False(f.Open);
        Assert.Equal(0, f.Confirms);
    }

    [Fact]
    public void ReceiveGilAcrossWindowsVerifiesExactDelta()
    {
        var io = DispatchProxy.Create<ITradeIo, TradeFixture>();
        var f = (TradeFixture)(object)io;
        var gil = new TradeInventoryKey(TradePlanner.GilItemId, false);
        f.RemoteItems = [];
        f.InventoryGil = 250;
        var remaining = 1_500_000;
        foreach (var window in new[] { 1_000_000, 500_000 })
        {
            f.Open = true;
            f.LocalReady = false;
            f.RemoteGil = window;
            var op = new TradeReceiveOperation(io, Partner, [new(TradePlanner.GilItemId, false, remaining)], Now.AddMinutes(1), _ => { });
            op.Tick(Now);
            op.Tick(Now);
            f.Open = false;
            f.InventoryGil += window;
            f.Quantity++; // an unrelated stack moved: never credit the window
            Assert.Equal(OperationStatus.Pending, op.Tick(Now).Status);
            f.Quantity--;
            Assert.Equal(OperationStatus.Succeeded, op.Tick(Now).Status);
            Assert.Equal(new KeyValuePair<TradeInventoryKey, int>(gil, window), Assert.Single(op.Received!));
            Assert.Equal(window, op.Evidence!.After[gil] - op.Evidence.Before[gil]);
            remaining -= op.Received![gil];
        }
        Assert.Equal(0, remaining);
        Assert.Equal(2, f.Confirms);
    }

    [Fact]
    public void ConfirmedWindowSucceedsWhenSenderReopensBeforeClosureIsObserved()
    {
        var io = DispatchProxy.Create<ITradeIo, TradeFixture>();
        var f = (TradeFixture)(object)io;
        var op = new TradeReceiveOperation(io, Partner, [Expected], Now.AddMinutes(1), _ => { });
        op.Tick(Now);
        op.Tick(Now);
        Assert.Equal(1, f.Confirms);
        f.LocalReady = false; // the sender's next window, already locked with an identical offer; closure was never observed
        Assert.Equal(OperationStatus.Pending, op.Tick(Now).Status);
        f.Quantity += 3;
        Assert.Equal(OperationStatus.Succeeded, op.Tick(Now).Status);
        Assert.Equal(OperationStatus.Succeeded, op.Tick(Now).Status);
        Assert.Equal(new KeyValuePair<TradeInventoryKey, int>(new(42, true), 3), Assert.Single(op.Received!));
        Assert.Equal(3, op.Evidence!.After[new(42, true)] - op.Evidence.Before[new(42, true)]);
        Assert.Equal((1, 1, true), (f.Ready, f.Confirms, f.Open));
    }

    [Theory]
    [InlineData("gil-for-items")]
    [InlineData("items-for-gil")]
    [InlineData("gil-over-remaining")]
    public void ReceiveRefusesUnexpectedGilOrItems(string change)
    {
        var io = DispatchProxy.Create<ITradeIo, TradeFixture>();
        var f = (TradeFixture)(object)io;
        TradeOfferedItem[] manifest = change == "gil-for-items" ? [Expected] : [new(TradePlanner.GilItemId, false, 500_000)];
        f.RemoteItems = change == "items-for-gil" ? [Expected] : [];
        f.RemoteGil = change switch { "gil-for-items" => 1, "items-for-gil" => 500_000, _ => 500_001 };
        var op = new TradeReceiveOperation(io, Partner, manifest, Now.AddMinutes(1), _ => { });
        Assert.Equal(OperationStatus.Failed, op.Tick(Now).Status);
        Assert.Equal(0, f.Ready);
        Assert.Equal(0, f.Confirms);
    }

    private static readonly TradePartner Partner = new(17, "Expected", 2);

    private sealed class Store : ITradeIntentStore
    {
        public IList<TradeItem> Remaining { get; init; } = [new() { ItemId = 42, IsHighQuality = true, Quantity = 3 }];
        public TradeRecoveryState Recovery { get; init; } = new();
        public string Saved = "";
        public void Save() => Saved = JsonSerializer.Serialize(this);
        public Store Reload() => JsonSerializer.Deserialize<Store>(Saved)!;
    }

    public class Noop : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
    }

    private sealed class Sender
    {
        public readonly ITradeIo Io = DispatchProxy.Create<ITradeIo, TradeFixture>();
        public TradeFixture F => (TradeFixture)(object)Io;
        public Store Store = new();
        public ITradeSuppression Suppression = DispatchProxy.Create<ITradeSuppression, Noop>();
        public TradeRunner Runner;
        private DateTimeOffset now = Now;

        public TradeRunner Construct() => new(Store.Remaining, new(), Store.Save, Io,
            Suppression, DispatchProxy.Create<IPluginLog, Noop>(), () => now, Store.Recovery);
        public Sender()
        {
            Runner = Construct();
            F.Sending = true;
            F.Local = [Expected];
        }
        public void ToReady()
        {
            Assert.False(Runner.Start(Partner with { GameObjectId = 18 }).Success);
            Assert.False(Runner.Start(Partner with { Name = "Different" }).Success);
            Assert.True(Runner.Start(Partner).Success);
            for (int i = 0; i < 8 && F.Ready == 0; i++) Tick();
            Assert.Equal(1, F.Ready);
        }
        public void Tick() { now = now.AddSeconds(1); Runner.Tick(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnconfirmedLockedSenderCancels(bool stop)
    {
        var sender = new Sender();
        sender.ToReady();
        if (stop) sender.Runner.Stop();
        else { sender.F.Locked = false; sender.F.RemoteGil = 1; sender.Tick(); }
        Assert.False(sender.F.Open);
        Assert.Equal(0, sender.F.Confirms);
        Assert.Equal(stop ? TradeExecutionState.Stopped : TradeExecutionState.Failed, sender.Runner.Snapshot.State);
        Assert.NotEqual(OperationStatus.Indeterminate, sender.Runner.Result!.Status);
    }

    [Fact]
    public void ThrowingVerifiedListenerCannotAbandonOrReplay()
    {
        var sender = new Sender();
        bool abandoned = false, persistedAtNotification = false;
        sender.Runner.BatchAbandoned += (_, _) => abandoned = true;
        sender.Runner.BatchVerified += (_, _) =>
        {
            persistedAtNotification = sender.Store.Reload().Remaining.Count == 0;
            throw new InvalidOperationException();
        };
        sender.ToReady();
        sender.Tick();
        sender.F.Open = false;
        sender.F.Quantity -= 3;
        sender.Tick();
        Assert.True(persistedAtNotification);
        Assert.False(abandoned);
        Assert.Equal(OperationStatus.Succeeded, sender.Runner.Result!.Status);
        sender.Store = sender.Store.Reload();
        Assert.False(sender.Construct().Start(Partner).Success);
    }

    [Theory]
    [InlineData(TradeSettlement.Delivered)]
    [InlineData(TradeSettlement.NotDelivered)]
    public void ReloadRequiresExplicitSettlement(TradeSettlement decision)
    {
        var sender = new Sender();
        sender.ToReady();
        bool markerOnDispatch = false;
        sender.F.OnConfirm = () => markerOnDispatch = sender.Store.Reload().Recovery.AwaitingVerification?.Partner == Partner;
        sender.Tick();
        Assert.True(markerOnDispatch);
        sender.F.Open = false;
        sender.Runner.Dispose();
        sender.Store = sender.Store.Reload();
        var resumed = new TradeRunner(sender.Store, new(), sender.Io,
            DispatchProxy.Create<ITradeSuppression, Noop>(), DispatchProxy.Create<IPluginLog, Noop>());
        Assert.Equal("SettlementRequired", resumed.Start(Partner).Code);
        sender.F.Quantity = 9;
        Assert.Null(resumed.ObserveSettlement());
        sender.F.Quantity = decision == TradeSettlement.Delivered ? 7 : 10;
        Assert.Equal(decision, resumed.ObserveSettlement());
        resumed.AcknowledgeSettlement(decision);
        sender.Store = sender.Store.Reload();
        Assert.Null(sender.Store.Recovery.AwaitingVerification);
        Assert.Equal(decision == TradeSettlement.Delivered ? 0 : 3, sender.Store.Remaining.Sum(i => i.Quantity));
        Assert.Equal(decision == TradeSettlement.NotDelivered, resumed.Start(Partner).Success);
    }

    private sealed class RecordingSuppression : ITradeSuppression
    {
        public int Suppressed, Restored, Retained, Renewed;
        public void SuppressTradeAutoConfirm() => Suppressed++;
        public void RetainTradeSuppressionOnDispose() => Retained++;
        public void RestoreTradeAutoConfirm() => Restored++;
        public void RenewTradeSuppression() => Renewed++;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EscapeHatchKeepsAutoConfirmPausedUntilWindowCloses(bool settle)
    {
        var sender = new Sender();
        var suppression = new RecordingSuppression();
        sender.Suppression = suppression;
        sender.Runner = sender.Construct();
        sender.ToReady();
        if (settle)
        {
            sender.Tick(); // confirmed: the marker exists and the window is still open
            Assert.NotNull(sender.Store.Recovery.AwaitingVerification);
            Assert.True(sender.Runner.IsActive);
        }
        Assert.True(suppression.Renewed > 0);
        var restoredBefore = suppression.Restored;

        if (settle) sender.Runner.AcknowledgeSettlement(TradeSettlement.NotDelivered);
        else sender.Runner.Abandon();

        Assert.False(sender.Runner.IsActive);
        Assert.Equal(restoredBefore, suppression.Restored);
        sender.Tick();
        Assert.Equal(restoredBefore, suppression.Restored);
        sender.F.Open = false;
        sender.Tick();
        Assert.Equal(restoredBefore + 1, suppression.Restored);
        Assert.Equal(settle ? 1 : 0, sender.F.Confirms);
    }

    [Fact]
    public void ReloadWithMarkerAndOpenWindowKeepsAutoConfirmPausedUntilClosed()
    {
        var sender = new Sender();
        var first = new RecordingSuppression();
        sender.Suppression = first;
        sender.Runner = sender.Construct();
        sender.ToReady();
        sender.Tick(); // confirmed; the window stays open
        sender.Runner.Dispose();
        Assert.Equal(1, first.Retained);
        sender.Store = sender.Store.Reload();

        var reloaded = new RecordingSuppression();
        var resumed = new TradeRunner(sender.Store, new(), sender.Io, reloaded, DispatchProxy.Create<IPluginLog, Noop>());
        Assert.False(resumed.IsActive);
        Assert.Equal(1, reloaded.Suppressed);
        resumed.Tick();
        Assert.Equal(0, reloaded.Restored);
        sender.F.Open = false;
        resumed.Tick();
        Assert.Equal(1, reloaded.Restored);
    }

    [Fact]
    public void LoadWithNoWindowOpenReleasesRetainedPause()
    {
        var sender = new Sender();
        sender.F.Open = false;
        var suppression = new RecordingSuppression();
        sender.Suppression = suppression;
        sender.Construct();
        Assert.Equal(0, suppression.Suppressed);
        Assert.Equal(1, suppression.Restored);
    }

    public class TradeFixture : DispatchProxy
    {
        public TradePartner Visible = Partner;
        public bool Locked = true, LocalReady, Sending;
        public TradeOfferedItem[] Extra = [];
        public Action? OnConfirm;
        public uint Entity = 17;
        public int Ready, Confirms, Offers, Quantities, RemoteGil, InventoryGil, Quantity = 10;
        public TradeOfferedItem[]? RemoteItems;
        public bool Open = true, Numeric;
        public TradeOfferedItem Remote = Expected;
        public TradeOfferedItem[] Local = [];
        private bool Confirm() { OnConfirm?.Invoke(); return ++Confirms > 0; }
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (arguments?.Length > 0 && method!.GetParameters()[^1].IsOut) arguments[^1] = "";
            return method!.Name switch
            {
                "get_IsTradeOpen" => Open,
                "get_LocalContentId" => 99ul,
                "get_IsNumericInputOpen" => Numeric,
                "get_IsRemoteLocked" => Locked,
                "get_CanConfirmTrade" => Locked && LocalReady && Open,
                "get_CanClickReady" => !LocalReady && Open,
                "PartnerIsAvailable" => arguments![0] is TradePartner p && p.Matches(Visible.GameObjectId, Visible.Name, Visible.HomeWorldId),
                "GetAvailablePartners" => new[] { Visible },
                "TryGetExecutionReadiness" => true,
                "TryCancelUnconfirmedTrade" or "TryCancelTrade" => !(Open = false),
                "ObserveTradePartnerEntityId" => Entity,
                "ObserveOffer" => new TradeOfferObservation(Entity, 0, false, Local),
                "ObserveRemoteOffer" => new TradeOfferObservation(Entity, RemoteGil, false, Sending ? [] : RemoteItems ?? [Remote, ..Extra]),
                "ObserveTradeableInventory" => TradeInventoryObservation.Authoritative(InventoryGil > 0
                    ? [new(uint.MaxValue, -1, TradePlanner.GilItemId, "Gil", false, InventoryGil), new(0, 0, 42, "Ore", true, Quantity)]
                    : [new(0, 0, 42, "Ore", true, Quantity)]),
                "TryClickReady" => LocalReady = ++Ready > 0,
                "TryConfirmTrade" => Confirm(),
                "TrySubmitQuantity" => ++Quantities > 0,
                "TryOfferItem" => ++Offers > 0,
                _ => throw new NotSupportedException(method.Name)
            };
        }
    }
}
