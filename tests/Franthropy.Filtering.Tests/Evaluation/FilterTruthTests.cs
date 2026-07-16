using Franthropy.Filtering.Evaluation;

namespace Franthropy.Filtering.Tests.Evaluation;

public sealed class FilterTruthTests
{
    [Theory]
    [InlineData(FilterTruth.True, FilterTruth.False)]
    [InlineData(FilterTruth.False, FilterTruth.True)]
    [InlineData(FilterTruth.Unknown, FilterTruth.Unknown)]
    public void Not_UsesThreeValuedLogic(FilterTruth input, FilterTruth expected) =>
        Assert.Equal(expected, FilterTruthOperations.Not(input));

    [Theory]
    [InlineData(FilterTruth.False, FilterTruth.Unknown, FilterTruth.False)]
    [InlineData(FilterTruth.True, FilterTruth.Unknown, FilterTruth.Unknown)]
    [InlineData(FilterTruth.True, FilterTruth.True, FilterTruth.True)]
    [InlineData(FilterTruth.Unknown, FilterTruth.Unknown, FilterTruth.Unknown)]
    public void And_UsesKleeneLogic(FilterTruth left, FilterTruth right, FilterTruth expected) =>
        Assert.Equal(expected, FilterTruthOperations.And(left, right));

    [Theory]
    [InlineData(FilterTruth.True, FilterTruth.Unknown, FilterTruth.True)]
    [InlineData(FilterTruth.False, FilterTruth.Unknown, FilterTruth.Unknown)]
    [InlineData(FilterTruth.False, FilterTruth.False, FilterTruth.False)]
    [InlineData(FilterTruth.Unknown, FilterTruth.Unknown, FilterTruth.Unknown)]
    public void Or_UsesKleeneLogic(FilterTruth left, FilterTruth right, FilterTruth expected) =>
        Assert.Equal(expected, FilterTruthOperations.Or(left, right));
}
