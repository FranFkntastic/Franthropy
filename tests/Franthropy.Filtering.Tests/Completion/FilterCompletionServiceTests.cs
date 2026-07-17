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
    public void Complete_PreservesUnaryNegationWhileReplacingTheFieldPrefix()
    {
        var result = FilterCompletionService.Complete(Context, new("test", "-qua", 4));

        var field = Assert.Single(result.Items, item => item.Kind == FilterCompletionKind.Field && item.Label == "quality");
        Assert.Equal(new(1, 3), field.ReplacementSpan);
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
    public void Complete_SuggestsSupportedOperatorsAfterAnExactField()
    {
        var result = FilterCompletionService.Complete(Context, new("test", "quantity", 8));

        Assert.Equal([":", "=", "!=", "==", "!==", "<", "<=", ">", ">="],
            result.Items.Where(item => item.Kind == FilterCompletionKind.Operator).Select(item => item.Label));
        Assert.All(result.Items, item => Assert.Equal(new(8, 0), item.ReplacementSpan));
    }

    [Fact]
    public void Complete_ReplacesAPartialOperatorWithoutTouchingTheField()
    {
        var result = FilterCompletionService.Complete(Context, new("test", "quantity!", 9));

        Assert.Equal(["!=", "!=="], result.Items.Select(item => item.InsertionText));
        Assert.All(result.Items, completion => Assert.Equal(new(8, 1), completion.ReplacementSpan));
    }

    [Fact]
    public void Complete_PreservesTypedValueCompletionAfterACompleteOperator()
    {
        var result = FilterCompletionService.Complete(Context, new("test", "quality:", 8));

        Assert.Equal(["HQ", "NQ"], result.Items.Select(item => item.Label).Order());
        Assert.All(result.Items, item => Assert.Equal(FilterCompletionKind.Value, item.Kind));
    }

    [Fact]
    public void Complete_SuggestsOrderedOperatorsAfterAFriendlyColon()
    {
        var result = FilterCompletionService.Complete(Context, new("test", "quantity:>", 10));

        Assert.Equal([">", ">="], result.Items.Select(item => item.Label));
        Assert.All(result.Items, item => Assert.Equal(new(9, 1), item.ReplacementSpan));
    }

    [Fact]
    public void Complete_PrefersAnUnambiguousLeafAndFallsBackToCanonicalPathsWhenAmbiguous()
    {
        var ownedQuantity = FilterFields.Integer("ownership.quantity");
        var stackQuantity = FilterFields.Integer("instance.quantity");
        var catalog = new FilterCatalog([ownedQuantity, stackQuantity]);
        var narrow = new FilterContextBuilder<Row>(catalog)
            .Bind(stackQuantity, row => Evidence.Known(row.Quantity))
            .Build();
        var broad = new FilterContextBuilder<Row>(catalog)
            .Bind(ownedQuantity, row => Evidence.Known(row.Quantity))
            .Bind(stackQuantity, row => Evidence.Known(row.Quantity))
            .Build();

        var narrowResult = FilterCompletionService.Complete(narrow, new("test", "qua", 3));
        var broadResult = FilterCompletionService.Complete(broad, new("test", "qua", 3));

        Assert.Contains(narrowResult.Items, item => item.Kind == FilterCompletionKind.Field && item.InsertionText == "quantity");
        Assert.Contains(broadResult.Items, item => item.InsertionText == "ownership.quantity");
        Assert.Contains(broadResult.Items, item => item.InsertionText == "instance.quantity");
        Assert.DoesNotContain(broadResult.Items, item => item.InsertionText == "quantity");
    }

    [Fact]
    public void Complete_DoesNotOfferALeafShadowedByAnotherFieldsAlias()
    {
        var aliasedQuantity = FilterFields.Integer("ownership.total", aliases: ["quantity"]);
        var stackQuantity = FilterFields.Integer("instance.quantity");
        var catalog = new FilterCatalog([aliasedQuantity, stackQuantity]);
        var context = new FilterContextBuilder<Row>(catalog)
            .Bind(stackQuantity, row => Evidence.Known(row.Quantity))
            .Build();

        var result = FilterCompletionService.Complete(context, new("test", "qua", 3));

        Assert.Contains(result.Items, item => item.InsertionText == "instance.quantity");
        Assert.DoesNotContain(result.Items, item => item.InsertionText == "quantity");
    }

    [Fact]
    public void Complete_StartsANewTermAfterWhitespaceInsteadOfRepeatingThePreviousValue()
    {
        var result = FilterCompletionService.Complete(Context, new("test", "quality:hq ", 11));

        Assert.Contains(result.Items, item => item.Kind == FilterCompletionKind.Field && item.Label == "quality");
        Assert.DoesNotContain(result.Items, item => item.Kind == FilterCompletionKind.Value);
    }

    [Fact]
    public void Complete_PreservesValueSuggestionsInsideAList()
    {
        var result = FilterCompletionService.Complete(Context, new("test", "quality:(HQ|n", 13));

        var value = Assert.Single(result.Items, item => item.Kind == FilterCompletionKind.Value);
        Assert.Equal("NQ", value.Label);
        Assert.Equal(new(12, 1), value.ReplacementSpan);
    }

    [Fact]
    public void Complete_SuggestsFieldsInsideEvidenceFunctions()
    {
        var result = FilterCompletionService.Complete(Context, new("test", "unknown(qu", 10));

        Assert.Contains(result.Items, item => item.Kind == FilterCompletionKind.Field && item.Label == "quality");
    }


    [Fact]
    public void Complete_PredicateValuesUsesTheCaretPrefix()
    {
        var catalog = new FilterCatalog([QualityField, QuantityField], predicateAliases:
        [
            new("is", "hq", QualityField.Key, "HQ", "High quality."),
            new("is", "nq", QualityField.Key, "NQ", "Normal quality."),
        ]);
        var context = new FilterContextBuilder<Row>(catalog)
            .Bind(QualityField, row => Evidence.Known(row.Quality))
            .Build();

        var all = FilterCompletionService.Complete(context, new("test", "is:", 3));
        var narrowed = FilterCompletionService.Complete(context, new("test", "is:h", 4));

        Assert.Equal(["hq", "nq"], all.Items.Select(item => item.InsertionText));
        var hq = Assert.Single(narrowed.Items);
        Assert.Equal("hq", hq.InsertionText);
        Assert.Equal(new Franthropy.Filtering.Syntax.TextSpan(3, 1), hq.ReplacementSpan);
    }


    [Fact]
    public void Complete_HidesPredicatesWhoseTargetFieldIsUnavailable()
    {
        var catalog = new FilterCatalog([QualityField, QuantityField], predicateAliases:
        [
            new("is", "hq", QualityField.Key, "HQ"),
        ]);
        var context = new FilterContextBuilder<Row>(catalog)
            .Bind(QuantityField, row => Evidence.Known(row.Quantity))
            .Build();

        var result = FilterCompletionService.Complete(context, new("test", "is:", 3));

        Assert.DoesNotContain(result.Items, item => item.InsertionText == "hq");
        Assert.Empty(Franthropy.Filtering.Documentation.FilterReferenceGenerator.Create(context).Predicates);
    }

    private sealed record Row(Quality Quality, long Quantity);
}
