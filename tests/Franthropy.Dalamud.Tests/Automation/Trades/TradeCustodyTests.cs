using System.Reflection;
using Franthropy.Dalamud.Automation;
using Franthropy.Dalamud.Automation.Trades;

namespace Franthropy.Dalamud.Tests.Automation.Trades;

public sealed class TradeCustodyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly TradeOfferedItem Expected = new(42, true, 3);

    [Theory]
    [InlineData("partner")]
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
            case "partner": fixture.Entity++; break;
            case "quality": fixture.Remote = Expected with { IsHighQuality = false }; break;
            case "quantity": fixture.Remote = Expected with { Quantity = 4 }; break;
            case "gil": fixture.RemoteGil++; break;
        }
        Assert.Equal(OperationStatus.Indeterminate, receive.Tick(Now).Status);
        Assert.Equal(0, fixture.Confirms);
    }

    [Fact]
    public void ClosedConfirmedTradeRequiresExactInventoryDelta()
    {
        var io = DispatchProxy.Create<ITradeIo, TradeFixture>();
        var fixture = (TradeFixture)(object)io;
        var receive = new TradeReceiveOperation(io, new(17, "Expected", 2), [Expected], Now.AddMinutes(1), _ => { });
        receive.Tick(Now);
        receive.Tick(Now);
        receive.Tick(Now);
        Assert.Equal(1, fixture.Confirms);
        fixture.Open = false;
        fixture.Quantity = 12;
        Assert.Equal(OperationStatus.Pending, receive.Tick(Now).Status);
        fixture.Quantity = 13;
        Assert.Equal(OperationStatus.Succeeded, receive.Tick(Now).Status);
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

    public class TradeFixture : DispatchProxy
    {
        public uint Entity = 17;
        public int Ready, Confirms, Offers, Quantities, RemoteGil, Quantity = 10;
        public bool Open = true, Numeric;
        public TradeOfferedItem Remote = Expected;
        public TradeOfferedItem[] Local = [];
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (arguments?.Length > 0 && method!.GetParameters()[^1].IsOut) arguments[^1] = "";
            return method!.Name switch
            {
                "get_IsTradeOpen" => Open,
                "get_LocalContentId" => 99ul,
                "get_IsNumericInputOpen" => Numeric,
                "get_IsRemoteLocked" or "get_CanConfirmTrade" or "get_CanClickReady" or "PartnerIsAvailable" => true,
                "ObserveTradePartnerEntityId" => Entity,
                "ObserveOffer" => new TradeOfferObservation(Entity, 0, false, Local),
                "ObserveRemoteOffer" => new TradeOfferObservation(Entity, RemoteGil, false, [Remote]),
                "ObserveTradeableInventory" => TradeInventoryObservation.Authoritative([new(0, 0, 42, "Ore", true, Quantity)]),
                "TryClickReady" => ++Ready > 0,
                "TryConfirmTrade" => ++Confirms > 0,
                "TrySubmitQuantity" => ++Quantities > 0,
                "TryOfferItem" => ++Offers > 0,
                _ => throw new NotSupportedException(method.Name)
            };
        }
    }
}
