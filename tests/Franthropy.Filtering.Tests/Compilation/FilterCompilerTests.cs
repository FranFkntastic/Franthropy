using Franthropy.Filtering.Compilation;
using Franthropy.Filtering.Diagnostics;
using Franthropy.Filtering.Documentation;
using Franthropy.Filtering.Evaluation;
using Franthropy.Filtering.Semantics;

namespace Franthropy.Filtering.Tests.Compilation;

public sealed class FilterCompilerTests
{
    private enum Quality
    {
        Normal,
        High,
    }

    private sealed record Item(
        string Name,
        long Quantity,
        bool Unique,
        Quality Quality,
        IReadOnlyCollection<string> Jobs,
        FieldEvidence<decimal> Price);

    private static readonly FilterField<string> Name = FilterFields.Text("item.name", aliases: ["name"]);
    private static readonly FilterField<long> Quantity = FilterFields.Integer("ownership.quantity", aliases: ["qty"]);
    private static readonly FilterField<bool> Unique = FilterFields.Boolean("item.unique", aliases: ["unique"]);
    private static readonly FilterField<Quality> ItemQuality = FilterFields.Enumeration<Quality>(
        "instance.quality", valueAliases: new Dictionary<string, Quality> { ["nq"] = Quality.Normal, ["hq"] = Quality.High });
    private static readonly FilterNamedValueCatalog<string> Jobs = new(
    [
        new FilterLiteralCandidate<string>("PLD", "Paladin", ["PLD"]),
        new FilterLiteralCandidate<string>("WAR", "Warrior", ["WAR"]),
    ]);
    private static readonly FilterSetField<string> Job = FilterFields.Set("item.job", Jobs, "job", aliases: ["job"]);
    private static readonly FilterField<decimal> Price = FilterFields.Decimal("offer.unitPrice", aliases: ["price"]);
    private static readonly FilterField<long> StackQuantity = FilterFields.Integer("instance.quantity");
    private static readonly FilterCatalog Catalog = new(
        [Name, Quantity, Unique, ItemQuality, Job, Price, StackQuantity], "test-1");

    private static readonly FilterContext<Item> Context = new FilterContextBuilder<Item>(Catalog)
        .Bind(Name, item => Evidence.Known(item.Name))
        .Bind(Quantity, item => Evidence.Known(item.Quantity))
        .Bind(Unique, item => Evidence.Known(item.Unique))
        .Bind(ItemQuality, item => Evidence.Known(item.Quality))
        .BindSet(Job, item => Evidence.Known(item.Jobs))
        .Bind(Price, item => item.Price)
        .UseDefaultText(Name)
        .Build("items-test-1");

    private static readonly Item Sample = new(
        "Augmented Credendum Cuirass", 4, true, Quality.High, ["PLD", "WAR"], Evidence.Known(125_000m));

    [Fact]
    public void EmptyFilter_MatchesEverything()
    {
        var compilation = FilterCompiler.Compile<Item>("", Context);

        Assert.True(compilation.IsValid);
        Assert.True(compilation.Matches(Sample));
    }

    [Fact]
    public void FreeText_SearchesConfiguredDefaultFields()
    {
        var compilation = FilterCompiler.Compile<Item>("credendum", Context);

        Assert.True(compilation.IsValid);
        Assert.True(compilation.Matches(Sample));
        Assert.False(compilation.Matches(Sample with { Name = "Iron Ingot" }));
    }

    [Theory]
    [InlineData("qty:4", true)]
    [InlineData("qty:3..5", true)]
    [InlineData("qty:..3", false)]
    [InlineData("qty:5..", false)]
    [InlineData("qty>=4", true)]
    [InlineData("qty:>=4", true)]
    [InlineData("qty:>4", false)]
    public void NumericComparisonsAndRanges_AreTyped(string expression, bool expected)
    {
        var compilation = FilterCompiler.Compile<Item>(expression, Context);

        Assert.True(compilation.IsValid);
        Assert.Equal(expected, compilation.Matches(Sample));
    }

    [Fact]
    public void BareWordsAndNegation_AlwaysUseDefaultTextSemantics()
    {
        Assert.False(FilterCompiler.Compile<Item>("unique", Context).Matches(Sample));
        Assert.True(FilterCompiler.Compile<Item>("-unique", Context).Matches(Sample));
        Assert.True(FilterCompiler.Compile<Item>("-iron", Context).Matches(Sample));
        Assert.False(FilterCompiler.Compile<Item>("-iron", Context).Matches(Sample with { Name = "Iron Ingot" }));
    }

    [Fact]
    public void QuotedBooleanName_RemainsFreeText()
    {
        var compilation = FilterCompiler.Compile<Item>("\"unique\"", Context);

        Assert.True(compilation.IsValid);
        Assert.False(compilation.Matches(Sample));
    }

    [Theory]
    [InlineData("instance.quality:hq", true)]
    [InlineData("instance.quality:nq", false)]
    [InlineData("job:Paladin", true)]
    [InlineData("job=(Paladin | Warrior)", true)]
    [InlineData("job=Paladin", true)]
    public void NamedAndSetValues_AreResolvedBeforeEvaluation(string expression, bool expected)
    {
        var compilation = FilterCompiler.Compile<Item>(expression, Context);

        Assert.True(compilation.IsValid);
        Assert.Equal(expected, compilation.Matches(Sample));
    }

    [Theory]
    [InlineData("name=credendum", true)]
    [InlineData("name!=credendum", false)]
    [InlineData("name==\"augmented credendum cuirass\"", true)]
    [InlineData("name!==\"augmented credendum cuirass\"", false)]
    [InlineData("name==credendum", false)]
    public void EqualityOperators_FormFuzzyAndExactNegationPairs(string expression, bool expected)
    {
        var compilation = FilterCompiler.Compile<Item>(expression, Context);
        Assert.True(compilation.IsValid);
        Assert.Equal(expected, compilation.Matches(Sample));
    }

    [Fact]
    public void FuzzyFiniteVocabulary_DiagnosesAmbiguityInsteadOfChoosing()
    {
        var compilation = FilterCompiler.Compile<Item>("job=a", Context);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == FilterDiagnosticCodes.AmbiguousValue);
    }

    [Fact]
    public void HumanSpellingAndSemanticNormalization_AreSeparate()
    {
        var direct = FilterCompiler.Compile<Item>("qty:4 -name:iron", Context);
        var legacy = FilterCompiler.Compile<Item>("ownership.quantity==4 AND !item.name=iron", Context);

        Assert.Equal("qty:4 -name:iron", direct.NormalizedExpression);
        Assert.Equal("qty:4 -name:iron", direct.Syntax.Source);
        Assert.Equal(legacy.SemanticExpression, direct.SemanticExpression);
    }

    [Fact]
    public void ReservedNestedQualifier_HasFocusedDiagnosticAndRoundTrips()
    {
        const string expression = "stat:range:>=50";
        var compilation = FilterCompiler.Compile<Item>(expression, Context);
        Assert.Equal(expression, compilation.NormalizedExpression);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == FilterDiagnosticCodes.ReservedNestedQualifier);
    }

    [Fact]
    public void UnknownEvidence_StaysUnknownThroughNegation()
    {
        var item = Sample with { Price = Evidence.Unknown<decimal>("No listing observation.") };

        Assert.Equal(FilterTruth.Unknown, FilterCompiler.Compile<Item>("price<200000", Context).Evaluate(item));
        Assert.Equal(FilterTruth.Unknown, FilterCompiler.Compile<Item>("NOT price<200000", Context).Evaluate(item));
        Assert.True(FilterCompiler.Compile<Item>("unknown(price)", Context).Matches(item));
        Assert.False(FilterCompiler.Compile<Item>("known(price)", Context).Matches(item));
        Assert.Equal(FilterTruth.Unknown, FilterCompiler.Compile<Item>("price!==200000", Context).Evaluate(item));
    }


    [Fact]
    public void NestedQualifierShape_IsReservedRatherThanReinterpreted()
    {
        var compilation = FilterCompiler.Compile<Item>("is:quality:hq", Context);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == FilterDiagnosticCodes.ReservedNestedQualifier);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Code == FilterDiagnosticCodes.UnknownField);
    }

    [Fact]
    public void CatalogKnownButUnboundField_IsUnavailable()
    {
        var compilation = FilterCompiler.Compile<Item>("instance.quantity>1", Context);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == FilterDiagnosticCodes.UnavailableField);
        Assert.False(compilation.IsValid);
    }

    [Fact]
    public void AmbiguousLeaf_RequiresCanonicalPathWhenMultipleCandidatesAreBound()
    {
        var context = new FilterContextBuilder<Item>(Catalog)
            .Bind(Quantity, item => Evidence.Known(item.Quantity))
            .Bind(StackQuantity, item => Evidence.Known(item.Quantity))
            .Build();

        var compilation = FilterCompiler.Compile<Item>("quantity>1", context);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == FilterDiagnosticCodes.AmbiguousField);
    }

    [Fact]
    public void UnknownField_ProducesStableDiagnostic()
    {
        var compilation = FilterCompiler.Compile<Item>("banana:yes", Context);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == FilterDiagnosticCodes.UnknownField);
    }

    [Fact]
    public void InvalidOperator_IsRejectedDuringBinding()
    {
        var compilation = FilterCompiler.Compile<Item>("item.name>iron", Context);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == FilterDiagnosticCodes.InvalidOperator);
    }

    [Theory]
    [InlineData("qty>1..5")]
    [InlineData("qty>(1 | 5)")]
    [InlineData("qty:5..1")]
    public void InvalidOrderedShapes_AreRejected(string expression)
    {
        var compilation = FilterCompiler.Compile<Item>(expression, Context);

        Assert.False(compilation.IsValid);
        Assert.False(compilation.Matches(Sample));
    }

    [Fact]
    public void CompilationCacheKey_IncludesEveryCompatibilityDimension()
    {
        var cache = new FilterCompilationCache<Item>(Context);
        var key = cache.CreateKey("unique", System.Globalization.CultureInfo.GetCultureInfo("ja-JP"));

        Assert.Equal("test-1", key.CatalogVersion);
        Assert.Equal("items-test-1", key.ContextId);
        Assert.Equal("1", key.ContextSchemaVersion);
        Assert.Equal("ja-JP", key.Locale);
        Assert.Same(cache.GetOrCompile("unique"), cache.GetOrCompile("unique"));
    }

    [Fact]
    public void ReferenceModel_ReportsCatalogAndContextAvailability()
    {
        var reference = FilterReferenceGenerator.Create(Context);

        Assert.Equal("test-1", reference.CatalogVersion);
        Assert.Contains(reference.Fields, field => field.Key == "item.name" && field.IsAvailable);
        Assert.Contains(reference.Fields, field => field.Key == "instance.quantity" && !field.IsAvailable);
        Assert.Contains(reference.Fields.Single(field => field.Key == "instance.quality").Values,
            value => value.Aliases.Contains("hq"));
    }

    [Fact]
    public void ReferenceModel_UsesCatalogResolutionForAliasShadowedLeaves()
    {
        var aliasedQuantity = FilterFields.Integer("ownership.total", aliases: ["quantity"]);
        var stackQuantity = FilterFields.Integer("instance.quantity");
        var catalog = new FilterCatalog([aliasedQuantity, stackQuantity]);
        var context = new FilterContextBuilder<Item>(catalog)
            .Bind(stackQuantity, item => Evidence.Known(item.Quantity))
            .Build();

        var reference = FilterReferenceGenerator.Create(context);

        Assert.Equal("quantity", reference.Fields.Single(field => field.Key == "ownership.total").PreferredName);
        Assert.Equal("instance.quantity", reference.Fields.Single(field => field.Key == "instance.quantity").PreferredName);
    }

    [Fact]
    public void ContextCannotBindFieldOutsideCatalog()
    {
        var foreign = FilterFields.Text("foreign.name");
        var builder = new FilterContextBuilder<Item>(Catalog);

        Assert.Throws<ArgumentException>(() => builder.Bind(foreign, item => Evidence.Known(item.Name)));
    }

    [Fact]
    public void CatalogRejectsAliasCollisions()
    {
        var left = FilterFields.Text("left.name", aliases: ["shared"]);
        var right = FilterFields.Text("right.name", aliases: ["shared"]);

        Assert.Throws<ArgumentException>(() => new FilterCatalog([left, right]));
    }
}
