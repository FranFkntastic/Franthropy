using Franthropy.Dalamud.Equipment;

namespace Franthropy.Dalamud.Tests.Equipment;

public sealed class MateriaMeldCostEstimatorTests
{
    [Fact]
    public void GuaranteedAndAdvancedMeldsIncludeExpectedFailures()
    {
        var result = MateriaMeldCostEstimator.Estimate(
        [
            new("native", 1_000, 1d),
            new("first-overmeld", 1_700, .17d),
            new("fourth-overmeld", 500, .05d),
        ]);

        Assert.Equal(3_200UL, result.OneCopyCostGil);
        Assert.Equal(21_000UL, result.ExpectedCostGil);
        Assert.True(result.PlanningCostGil > result.ExpectedCostGil);
        Assert.Equal(.90d, result.PlanningConfidence);
    }

    [Fact]
    public void WholePlanCeilingAllocatesConfidenceAcrossRiskyMelds()
    {
        var result = MateriaMeldCostEstimator.Estimate(
        [
            new("a", 1, .17d),
            new("b", 1, .17d),
        ], .90d);

        var attempts = Assert.Single(result.Lines.Select(line => line.PlanningCopies).Distinct());
        var completionProbability = Math.Pow(1d - Math.Pow(1d - .17d, attempts), 2d);
        Assert.True(completionProbability >= .90d);
        Assert.Equal((ulong)(attempts * 2), result.PlanningCostGil);
    }

    [Theory]
    [InlineData(true, 12, 0, .17d)]
    [InlineData(true, 11, 1, .10d)]
    [InlineData(true, 11, 2, .07d)]
    [InlineData(true, 11, 3, .05d)]
    [InlineData(false, 12, 0, .12d)]
    public void DoHDolRateTableMatchesGameData(
        bool highQuality,
        int tier,
        int advancedMeldIndex,
        double expected)
    {
        Assert.Equal(expected, DohDolMateriaMeldingRates.Resolve(highQuality, tier, advancedMeldIndex));
    }

    [Fact]
    public void EvenGradeCannotBeUsedPastFirstAdvancedMeld()
    {
        Assert.Throws<InvalidOperationException>(() => DohDolMateriaMeldingRates.Resolve(true, 12, 1));
    }
}
