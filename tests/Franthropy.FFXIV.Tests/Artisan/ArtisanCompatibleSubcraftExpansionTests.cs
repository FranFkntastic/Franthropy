using Franthropy.FFXIV.Artisan;

namespace Franthropy.FFXIV.Tests.Artisan;

public sealed class ArtisanCompatibleSubcraftExpansionTests
{
    [Fact]
    public void Expand_OrdersDeepestSubcraftFirstAndRoundsByYield()
    {
        var catalog = new Catalog(
            Recipe(100, 1000, 1, 8, (2000, 3)),
            Recipe(200, 2000, 2, 8, (3000, 2)),
            Recipe(300, 3000, 3, 8));

        var result = ArtisanCompatibleSubcraftExpansion.Expand([new(100, 2)], catalog);

        Assert.Equal(
            [new ArtisanRecipeQuantity(300, 2), new(200, 3), new(100, 2)],
            result.Recipes);
        Assert.Equal(7, result.ExpandedCraftCount);
    }

    [Fact]
    public void Expand_PrefersParentCraftTypeForSubcraftRecipe()
    {
        var catalog = new Catalog(
            Recipe(100, 1000, 1, 9, (2000, 1)),
            Recipe(200, 2000, 1, 8),
            Recipe(201, 2000, 1, 9));

        var result = ArtisanCompatibleSubcraftExpansion.Expand([new(100, 1)], catalog);

        Assert.Equal([new ArtisanRecipeQuantity(201, 1), new(100, 1)], result.Recipes);
    }

    [Fact]
    public void Expand_MergesSharedSubcraftAndTidiesOverproduction()
    {
        var catalog = new Catalog(
            Recipe(100, 1000, 1, 8, (2000, 1)),
            Recipe(101, 1001, 1, 8, (2000, 1)),
            Recipe(200, 2000, 3, 8));

        var result = ArtisanCompatibleSubcraftExpansion.Expand([new(100, 2), new(101, 2)], catalog);

        Assert.Equal(
            [new ArtisanRecipeQuantity(200, 2), new(100, 2), new(101, 2)],
            result.Recipes);
    }

    [Fact]
    public void Expand_TreatsMissingIngredientRecipeAsTerminalMaterial()
    {
        var catalog = new Catalog(Recipe(100, 1000, 1, 8, (9000, 4)));

        var result = ArtisanCompatibleSubcraftExpansion.Expand([new(100, 2)], catalog);

        Assert.Equal([new ArtisanRecipeQuantity(100, 2)], result.Recipes);
    }

    [Fact]
    public void Expand_RejectsRecipeCycles()
    {
        var catalog = new Catalog(
            Recipe(100, 1000, 1, 8, (2000, 1)),
            Recipe(200, 2000, 1, 8, (1000, 1)));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ArtisanCompatibleSubcraftExpansion.Expand([new(100, 1)], catalog));

        Assert.Contains("cycle", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ArtisanRecipeDefinition Recipe(
        uint recipeId,
        uint resultItemId,
        int yield,
        uint craftType,
        params (uint ItemId, int Quantity)[] ingredients) =>
        new(
            recipeId,
            resultItemId,
            yield,
            craftType,
            ingredients.Select(value => new ArtisanRecipeIngredient(value.ItemId, value.Quantity)).ToList());

    private sealed class Catalog(params ArtisanRecipeDefinition[] recipes) : IArtisanRecipeCatalog
    {
        private readonly IReadOnlyList<ArtisanRecipeDefinition> values = recipes.OrderBy(value => value.RecipeId).ToList();

        public ArtisanRecipeDefinition? FindByRecipeId(uint recipeId) =>
            values.FirstOrDefault(value => value.RecipeId == recipeId);

        public ArtisanRecipeDefinition? FindForResult(uint itemId, uint? preferredCraftTypeId = null) =>
            values.FirstOrDefault(value =>
                value.ResultItemId == itemId &&
                preferredCraftTypeId is not null &&
                value.CraftTypeId == preferredCraftTypeId) ??
            values.FirstOrDefault(value => value.ResultItemId == itemId);
    }
}
