using Franthropy.Filtering.Completion;
using Franthropy.Filtering.Evaluation;
using Franthropy.Filtering.Semantics;

namespace Franthropy.Filtering.Tests.Completion;

public sealed class FilterCompletionServiceTests
{
    private enum Quality { NQ, HQ }

    private static readonly FilterField<Quality> QualityField =
        FilterFields.Enumeration<Quality>("instance.quality", "Quality", "Observed quality.", ["quality"]);
    private static readonly FilterField<long> QuantityField =
        FilterFields.Integer("instance.quantity", "Quantity", "Observed quantity.", ["quantity"], minimum: 0);
    private static readonly FilterCatalog Catalog = new([QualityField, QuantityField]);
    private static readonly FilterContext<Row> Context = new FilterContextBuilder<Row>(Catalog)
        .Bind(QualityField, row => Evidence.Known(row.Quality))
        .Bind(QuantityField, row => Evidence.Known(row.Quantity))
        .Build("test", "2");

    [Fact]
    public void Complete_SuggestsAvailableFieldsAndReplacesCurrentPrefix()
    {
        var result = FilterCompletionService.Complete(Context, new("test", "qua", 3));

        var field = Assert.Single(result.Items, item => item.Kind == FilterCompletionKind.Field && item.Label == "quality");
        Assert.Equal(new(0, 3), field.ReplacementSpan);
        Assert.Equal("quality", field.InsertionText);
    }

    [Fact]
    public void Complete_SuggestsTypedValuesAfterComparator()
    {
        var result = FilterCompletionService.Complete(Context, new("test", "quality:h", 9));

        var value = Assert.Single(result.Items, item => item.Kind == FilterCompletionKind.Value);
        Assert.Equal("HQ", value.Label);
        Assert.Equal(new(8, 1), value.ReplacementSpan);
    }

    [Fact]
    public void Complete_SuggestsFieldsInsideEvidenceFunctions()
    {
        var result = FilterCompletionService.Complete(Context, new("test", "unknown(qu", 10));

        Assert.Contains(result.Items, item => item.Kind == FilterCompletionKind.Field && item.Label == "quality");
    }

    private sealed record Row(Quality Quality, long Quantity);
}
