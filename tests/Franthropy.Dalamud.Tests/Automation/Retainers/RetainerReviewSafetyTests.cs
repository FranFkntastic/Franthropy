using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.Dalamud.Diagnostics;

namespace Franthropy.Dalamud.Tests.Automation.Retainers;

public sealed class RetainerReviewSafetyTests
{
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
