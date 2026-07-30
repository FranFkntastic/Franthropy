using Franthropy.Dalamud.Automation.MarketBoard;

namespace Franthropy.Dalamud.Tests.Automation.MarketBoard;

public sealed class MarketBoardPurchaseEvidenceTests
{
    private static readonly MarketBoardPurchaseIntent Intent =
        new(10, 22528, 2, 100, 205);

    [Fact]
    public void PacketMustMatchEveryFrozenPurchaseTerm()
    {
        Assert.True(MarketBoardPurchaseEvidenceClassifier.PacketMatches(
            Intent,
            new(10, 22528, 2, 100)));
        Assert.False(MarketBoardPurchaseEvidenceClassifier.PacketMatches(
            Intent,
            new(11, 22528, 2, 100)));
        Assert.False(MarketBoardPurchaseEvidenceClassifier.PacketMatches(
            Intent,
            new(10, 22528, 2, 101)));
    }

    [Theory]
    [InlineData(22528u, 2u, 1_000u, 795u, MarketBoardPurchaseEvidence.Verified, 205L)]
    [InlineData(22528u, 2u, 1_000u, 1_000u, MarketBoardPurchaseEvidence.Rejected, 0L)]
    [InlineData(22528u, 2u, 1_000u, 794u, MarketBoardPurchaseEvidence.Indeterminate, 206L)]
    [InlineData(22529u, 2u, 1_000u, 795u, MarketBoardPurchaseEvidence.Unrelated, null)]
    [InlineData(22528u, 1u, 1_000u, 795u, MarketBoardPurchaseEvidence.Unrelated, null)]
    public void ResponseClassificationRequiresExactIdentityAndGilDelta(
        uint itemId,
        uint quantity,
        uint gilBefore,
        uint gilAfter,
        MarketBoardPurchaseEvidence expected,
        long? expectedDelta)
    {
        var result = MarketBoardPurchaseEvidenceClassifier.ClassifyResponse(
            Intent,
            itemId,
            quantity,
            gilBefore,
            gilAfter);

        Assert.Equal(expected, result.Evidence);
        Assert.Equal(expectedDelta, result.GilDelta);
    }

    [Fact]
    public void MissingGilEvidenceIsIndeterminate()
    {
        var result = MarketBoardPurchaseEvidenceClassifier.ClassifyResponse(
            Intent,
            Intent.ItemId,
            Intent.Quantity,
            null,
            null);

        Assert.Equal(MarketBoardPurchaseEvidence.Indeterminate, result.Evidence);
        Assert.Null(result.GilDelta);
    }
}
