namespace Franthropy.FFXIV.Artisan;

public sealed record ArtisanRecipeIngredient(uint ItemId, int Quantity);

public sealed record ArtisanRecipeDefinition(
    uint RecipeId,
    uint ResultItemId,
    int Yield,
    uint CraftTypeId,
    IReadOnlyList<ArtisanRecipeIngredient> Ingredients);

public sealed record ArtisanRecipeQuantity(uint RecipeId, int CraftCount);

public interface IArtisanRecipeCatalog
{
    ArtisanRecipeDefinition? FindByRecipeId(uint recipeId);

    ArtisanRecipeDefinition? FindForResult(uint itemId, uint? preferredCraftTypeId = null);
}

public sealed record ArtisanSubcraftExpansionLimits(
    int MaximumDepth = 64,
    int MaximumRecipes = 2_000,
    int MaximumExpandedCrafts = 10_000);

public sealed record ArtisanSubcraftExpansionResult(
    IReadOnlyList<ArtisanRecipeQuantity> Recipes,
    int ExpandedCraftCount);

/// <summary>
/// Reproduces Artisan's "with subcrafts" ordering and quantity behavior while
/// adding explicit graph bounds and cycle detection around valid recipe data.
/// </summary>
public static class ArtisanCompatibleSubcraftExpansion
{
    public static ArtisanSubcraftExpansionResult Expand(
        IEnumerable<ArtisanRecipeQuantity> roots,
        IArtisanRecipeCatalog catalog,
        ArtisanSubcraftExpansionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(catalog);
        limits ??= new();
        ValidateLimits(limits);

        var ordered = new List<ArtisanRecipeQuantity>();
        var indexes = new Dictionary<uint, int>();
        var path = new HashSet<uint>();
        foreach (var root in roots)
        {
            if (root.RecipeId == 0 || root.CraftCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(roots), "Root recipe IDs and craft counts must be positive.");

            var recipe = catalog.FindByRecipeId(root.RecipeId) ??
                         throw new InvalidOperationException($"Recipe {root.RecipeId} is unavailable.");
            AddSubcrafts(recipe, amounts: 1, loops: root.CraftCount, depth: 0);
            AddOrMerge(recipe.RecipeId, root.CraftCount);
        }

        Tidy();
        var expanded = ordered.Sum(recipe => recipe.CraftCount);
        if (expanded > limits.MaximumExpandedCrafts)
            throw new InvalidOperationException($"Artisan expansion exceeds {limits.MaximumExpandedCrafts:N0} crafts.");
        return new(ordered, expanded);

        void AddSubcrafts(ArtisanRecipeDefinition recipe, int amounts, int loops, int depth)
        {
            if (depth >= limits.MaximumDepth)
                throw new InvalidOperationException($"Artisan expansion exceeds depth {limits.MaximumDepth:N0} at recipe {recipe.RecipeId}.");
            if (!path.Add(recipe.RecipeId))
                throw new InvalidOperationException($"Artisan expansion contains a recipe cycle at recipe {recipe.RecipeId}.");

            try
            {
                foreach (var ingredient in recipe.Ingredients.Where(value => value.ItemId > 0 && value.Quantity > 0))
                {
                    var subRecipe = catalog.FindForResult(ingredient.ItemId, recipe.CraftTypeId);
                    if (subRecipe is null)
                        continue;

                    var nestedAmounts = checked(ingredient.Quantity * amounts);
                    AddSubcrafts(subRecipe, nestedAmounts, loops, depth + 1);
                    var craftCount = DivideRoundUp(
                        checked((long)ingredient.Quantity * loops * amounts),
                        Math.Max(1, subRecipe.Yield));
                    AddOrMerge(subRecipe.RecipeId, craftCount);
                }
            }
            finally
            {
                path.Remove(recipe.RecipeId);
            }
        }

        void Tidy()
        {
            var requiredItems = new Dictionary<uint, long>();
            foreach (var request in ordered.ToArray())
            {
                var recipe = catalog.FindByRecipeId(request.RecipeId) ??
                             throw new InvalidOperationException($"Recipe {request.RecipeId} disappeared during Artisan tidy.");
                foreach (var ingredient in recipe.Ingredients.Where(value => value.ItemId > 0 && value.Quantity > 0))
                {
                    requiredItems[ingredient.ItemId] = checked(
                        requiredItems.GetValueOrDefault(ingredient.ItemId) +
                        (long)ingredient.Quantity * request.CraftCount);
                }
            }

            foreach (var required in requiredItems)
            {
                var recipe = catalog.FindForResult(required.Key);
                if (recipe is null || !indexes.TryGetValue(recipe.RecipeId, out var index))
                    continue;

                var current = ordered[index];
                if ((long)current.CraftCount * Math.Max(1, recipe.Yield) <= required.Value)
                    continue;

                ordered[index] = current with
                {
                    CraftCount = DivideRoundUp(required.Value, Math.Max(1, recipe.Yield)),
                };
            }
        }

        void AddOrMerge(uint recipeId, int craftCount)
        {
            if (craftCount <= 0)
                return;
            if (indexes.TryGetValue(recipeId, out var index))
            {
                var existing = ordered[index];
                ordered[index] = existing with { CraftCount = checked(existing.CraftCount + craftCount) };
                return;
            }

            if (ordered.Count >= limits.MaximumRecipes)
                throw new InvalidOperationException($"Artisan expansion exceeds {limits.MaximumRecipes:N0} recipes.");
            indexes.Add(recipeId, ordered.Count);
            ordered.Add(new(recipeId, craftCount));
        }
    }

    private static int DivideRoundUp(long numerator, int denominator)
    {
        if (numerator <= 0 || denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(numerator));
        return checked((int)((numerator + denominator - 1) / denominator));
    }

    private static void ValidateLimits(ArtisanSubcraftExpansionLimits limits)
    {
        if (limits.MaximumDepth <= 0 || limits.MaximumRecipes <= 0 || limits.MaximumExpandedCrafts <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "Artisan expansion limits must be positive.");
    }
}
