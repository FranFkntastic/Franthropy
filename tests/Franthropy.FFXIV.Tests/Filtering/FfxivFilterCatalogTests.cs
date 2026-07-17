using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Compilation;
using Franthropy.Filtering.Evaluation;
using Franthropy.Filtering.Documentation;
using Franthropy.Filtering.Semantics;

namespace Franthropy.FFXIV.Tests.Filtering;

public sealed class FfxivFilterCatalogTests
{
    private static readonly FfxivItemKey Cuirass = new(1);
    private static readonly FfxivItemKey Breeches = new(2);
    private static readonly FfxivJobKey Paladin = new(19);
    private static readonly FfxivJobKey Warrior = new(21);
    private static readonly FfxivWorldKey Siren = new(64);

    private static readonly FfxivFilterCatalog Vocabulary = FfxivFilterCatalog.Create(new FfxivFilterResolvers(
        Catalog(
            new FilterLiteralCandidate<FfxivItemKey>(Cuirass, "Augmented Credendum Cuirass"),
            new FilterLiteralCandidate<FfxivItemKey>(Breeches, "Augmented Credendum Breeches")),
        Catalog(
            new FilterLiteralCandidate<FfxivJobKey>(Paladin, "Paladin", ["PLD"]),
            new FilterLiteralCandidate<FfxivJobKey>(Warrior, "Warrior", ["WAR"])),
        Catalog(new FilterLiteralCandidate<FfxivUiCategoryKey>(new(1), "Body")),
        Catalog(new FilterLiteralCandidate<FfxivCharacterKey>(new(1), "Example Person@Siren")),
        Catalog(new FilterLiteralCandidate<FfxivRetainerKey>(new(1), "Belladonna")),
        Catalog(new FilterLiteralCandidate<FfxivWorldKey>(Siren, "Siren")),
        Catalog(new FilterLiteralCandidate<FfxivDataCenterKey>(new(4), "Aether"))));

    private sealed record Instance(
        FfxivItemKey Item,
        long ItemLevel,
        IReadOnlyCollection<FfxivJobKey> Jobs,
        FfxivItemQuality Quality,
        long Quantity,
        FfxivStorageLocation Location);

    private sealed record Offer(FfxivRegion Region);

    [Fact]
    public void Catalog_ContainsEveryDocumentedInitialField()
    {
        var keys = Vocabulary.Fields.Select(field => field.Key).ToArray();

        Assert.Equal(29, keys.Length);
        Assert.Contains("item.name", keys);
        Assert.Contains("instance.spiritbond", keys);
        Assert.Contains("ownership.quantity", keys);
        Assert.Contains("offer.totalPrice", keys);
        Assert.Contains("acquisition.source", keys);
        Assert.DoesNotContain("item.craftable", keys);
        Assert.DoesNotContain("item.vendoravailable", keys);
    }

    [Fact]
    public void ShortAliasesAndLeaves_KeepCanonicalMeaning()
    {
        Assert.Same(Vocabulary.ItemLevel, Vocabulary.Catalog.Resolve("ilvl").Field);
        Assert.Same(Vocabulary.OfferPrice, Vocabulary.Catalog.Resolve("price").Field);
        Assert.Equal(FilterFieldResolutionKind.Ambiguous, Vocabulary.Catalog.Resolve("quantity").Kind);
        Assert.Equal(FilterFieldResolutionKind.Ambiguous, Vocabulary.Catalog.Resolve("source").Kind);
    }

    [Fact]
    public void InstanceContext_CompilesTransferableVocabulary()
    {
        var context = new FilterContextBuilder<Instance>(Vocabulary.Catalog)
            .Bind(Vocabulary.ItemName, row => Evidence.Known(row.Item))
            .Bind(Vocabulary.ItemLevel, row => Evidence.Known(row.ItemLevel))
            .BindSet(Vocabulary.ItemJobs, row => Evidence.Known(row.Jobs))
            .Bind(Vocabulary.InstanceQuality, row => Evidence.Known(row.Quality))
            .Bind(Vocabulary.InstanceQuantity, row => Evidence.Known(row.Quantity))
            .Bind(Vocabulary.InstanceLocation, row => Evidence.Known(row.Location))
            .UseDefaultText(Vocabulary.ItemName, _ => Evidence.Known("Augmented Credendum Cuirass"))
            .Build("ffxiv.item-instances", "1");
        var row = new Instance(Cuirass, 660, [Paladin, Warrior], FfxivItemQuality.HQ, 2, FfxivStorageLocation.Retainer);

        var compilation = FilterCompiler.Compile<Instance>(
            "item:\"Augmented Credendum Cuirass\" ilvl>=650 job:PLD quality:HQ quantity>=2 location:retainer",
            context);

        Assert.True(compilation.IsValid, string.Join(Environment.NewLine, compilation.Diagnostics.Select(d => d.Message)));
        Assert.True(compilation.Matches(row));
        Assert.True(FilterCompiler.Compile<Instance>("credendum quality:HQ", context).Matches(row));
    }

    [Fact]
    public void OwnershipOwned_IsNotImplicitlyBoundForObservedStacks()
    {
        var context = new FilterContextBuilder<Instance>(Vocabulary.Catalog)
            .Bind(Vocabulary.ItemName, row => Evidence.Known(row.Item))
            .Bind(Vocabulary.InstanceQuantity, row => Evidence.Known(row.Quantity))
            .UseDefaultText(Vocabulary.ItemName, _ => Evidence.Known("Owned Blade"))
            .Build("ffxiv.item-instances", "1");

        var compilation = FilterCompiler.Compile<Instance>("-owned", context);

        Assert.True(compilation.IsValid);
        Assert.False(compilation.Matches(new Instance(Cuirass, 1, [], FfxivItemQuality.NQ, 1, FfxivStorageLocation.Inventory)));
    }

    [Fact]
    public void RegionAliases_ResolveUserLanguage()
    {
        var context = new FilterContextBuilder<Offer>(Vocabulary.Catalog)
            .Bind(Vocabulary.OfferRegion, row => Evidence.Known(row.Region))
            .Build("ffxiv.purchase-offers", "1");

        Assert.True(FilterCompiler.Compile<Offer>("region:\"North America\"", context).Matches(new Offer(FfxivRegion.NorthAmerica)));
        Assert.True(FilterCompiler.Compile<Offer>("region:NA", context).Matches(new Offer(FfxivRegion.NorthAmerica)));
    }

    [Theory]
    [InlineData("condition>101")]
    [InlineData("spiritbond:101..200")]
    public void CanonicalNumericBounds_RejectImpossibleLiterals(string expression)
    {
        var context = new FilterContextBuilder<Instance>(Vocabulary.Catalog)
            .Bind(Vocabulary.ItemLevel, row => Evidence.Known(row.ItemLevel))
            .Bind(Vocabulary.InstanceCondition, _ => Evidence.Known(100m))
            .Bind(Vocabulary.InstanceSpiritbond, _ => Evidence.Known(100m))
            .Build("ffxiv.item-instances", "1");

        Assert.False(FilterCompiler.Compile<Instance>(expression, context).IsValid);
    }

    [Fact]
    public void GeneratedReference_IsDeterministicAndContainsCanonicalMetadata()
    {
        var reference = FilterReferenceGenerator.Create(Vocabulary.Catalog);
        var markdown = FilterReferenceWriter.ToMarkdown(reference, "Canonical FFXIV filter vocabulary");
        var json = FilterReferenceWriter.ToJson(reference);

        Assert.Contains("## `item.itemLevel`", markdown);
        Assert.Contains("`ilvl`", markdown);
        Assert.Contains("\"catalogVersion\": \"1.1\"", json);
        Assert.Contains("\"key\": \"acquisition.source\"", json);
    }


    [Fact]
    public void ItemNameDirectMatch_FiltersEveryMatchingRecordWithoutCollapsingToOneIdentity()
    {
        var context = new FilterContextBuilder<Instance>(Vocabulary.Catalog)
            .Bind(Vocabulary.ItemName, row => Evidence.Known(row.Item))
            .UseDefaultText(Vocabulary.ItemName, row => Evidence.Known(row.Item == Cuirass
                ? "Augmented Credendum Cuirass"
                : "Augmented Credendum Breeches"))
            .Build();
        var rows = new[]
        {
            new Instance(Cuirass, 660, [], FfxivItemQuality.HQ, 1, FfxivStorageLocation.Inventory),
            new Instance(Breeches, 660, [], FfxivItemQuality.NQ, 1, FfxivStorageLocation.Inventory),
        };

        var direct = FilterCompiler.Compile<Instance>("name:credendum", context);
        var identity = FilterCompiler.Compile<Instance>("name=credendum", context);

        Assert.True(direct.IsValid);
        Assert.Equal(2, rows.Count(direct.Matches));
        Assert.Contains(identity.Diagnostics, diagnostic => diagnostic.Code == "FLT3005");
    }


    [Theory]
    [InlineData("quality:hq", FfxivItemQuality.HQ, true)]
    [InlineData("is:hq", FfxivItemQuality.HQ, true)]
    [InlineData("is:nq", FfxivItemQuality.NQ, true)]
    [InlineData("-is:equipped", FfxivItemQuality.HQ, true)]
    public void FriendlyPredicates_ShareCanonicalSemanticIdentity(string expression, FfxivItemQuality quality, bool expected)
    {
        var context = new FilterContextBuilder<Instance>(Vocabulary.Catalog)
            .Bind(Vocabulary.ItemName, row => Evidence.Known(row.Item))
            .Bind(Vocabulary.InstanceQuality, row => Evidence.Known(row.Quality))
            .Bind(Vocabulary.InstanceEquipped, _ => Evidence.Known(false))
            .UseDefaultText(Vocabulary.ItemName, _ => Evidence.Known("Augmented Credendum Cuirass"))
            .Build();
        var row = new Instance(Cuirass, 1, [], quality, 1, FfxivStorageLocation.Inventory);
        var compilation = FilterCompiler.Compile(expression, context);

        Assert.True(compilation.IsValid, string.Join(Environment.NewLine, compilation.Diagnostics.Select(d => d.Message)));
        Assert.Equal(expected, compilation.Matches(row));
        if (expression == "is:hq")
            Assert.Equal(FilterCompiler.Compile<Instance>("quality:hq", context).SemanticExpression, compilation.SemanticExpression);
    }

    private static FilterNamedValueCatalog<T> Catalog<T>(params FilterLiteralCandidate<T>[] values) => new(values);
}
