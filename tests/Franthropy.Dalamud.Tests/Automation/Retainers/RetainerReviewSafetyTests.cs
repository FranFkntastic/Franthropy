using System.Reflection;
using System.Runtime.CompilerServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.Dalamud.Diagnostics;

namespace Franthropy.Dalamud.Tests.Automation.Retainers;

public sealed class RetainerReviewSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingMarketMoveEntryPointRefusesBeforeDispatch(bool removal)
    {
        var address = removal
            ? InventoryManager.Addresses.MoveFromRetainerMarketToPlayerInventory.Value
            : InventoryManager.Addresses.MoveToRetainerMarket.Value;
        Assert.Equal((nint)0, address);
        var session = (DalamudRetainerAutomationSession)RuntimeHelpers.GetUninitializedObject(
            typeof(DalamudRetainerAutomationSession));
        var dispatched = false;
        Action marker = () => dispatched = true;
        var method = typeof(DalamudRetainerAutomationSession).GetMethod(
            removal ? "StartMarketListingRemoval" : "StartMarketListingPost",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        object[] arguments = removal
            ? [new RetainerMarketListingTarget(0, 5111, 2, false, 120), marker]
            : [new DalamudInventoryStack(InventoryType.Inventory1, 0, 5111, 2), 2, (uint)120, marker];
        var result = method.Invoke(session, arguments)!;
        Assert.False(dispatched);
        Assert.Equal("FailedBeforeSend", result.GetType().GetProperty("Outcome")!.GetValue(result)!.ToString());
        Assert.Equal(NativeCapabilityGuard.FailureCode, result.GetType().GetProperty("Code")!.GetValue(result));
    }

    [Fact]
    public void OptionalTransportCannotSwallowFailedRollback()
    {
        var failure = new AggregateException("Initialization and rollback failed",
            new InvalidOperationException("Enable"), new InvalidOperationException("Dispose"));
        Assert.Same(failure, Assert.Throws<AggregateException>(() =>
            DalamudSummoningBellInteractor.CreateOptionalTransport(() => throw failure)));
    }

    [Fact]
    public void OptionalTransportOnlyRecoversMissingCapability()
    {
        var result = DalamudSummoningBellInteractor.CreateOptionalTransport(() =>
            throw new NativeCapabilityUnavailableException("Receiver unavailable"));
        Assert.Null(result.Transport);
        Assert.Equal("Receiver unavailable", result.UnavailableReason);
        Assert.Throws<InvalidOperationException>(() =>
            DalamudSummoningBellInteractor.CreateOptionalTransport(() => throw new InvalidOperationException("Hook failure")));
    }

    [Theory]
    [InlineData(4, 120, "Other item")]
    [InlineData(3, 120, "Iron Ore")]
    [InlineData(4, 121, "Iron Ore")]
    public void PriceEditRejectsAChangedNativeControlBinding(int quantity, int price, string name)
        => Assert.Throws<NativeCapabilityUnavailableException>(() =>
            RetainerSellingEditorPolicy.RequireListing(4, 120, "Iron Ore", quantity, price, name));

    [Fact]
    public void PriceEditAcceptsExactItemQuantityAndPriceIncludingHqDecoration()
        => RetainerSellingEditorPolicy.RequireListing(4, 120, "Iron Ore", 4, 120, "Iron Ore \uE03C");
}
