using Franthropy.Dalamud.UI.Items;

namespace Franthropy.Dalamud.Tests.UI.Items;

public sealed class DalamudItemAutocompletePresenterTests
{
    [Fact]
    public void ResolveSelectedItem_WhenInputExactlyMatchesOneItem_ReturnsThatItem()
    {
        var resolved = DalamudItemAutocompletePresenter.ResolveSelectedItem(Options(), "fire shard", null);

        Assert.NotNull(resolved);
        Assert.Equal(2u, resolved.ItemId);
    }

    [Fact]
    public void ResolveSelectedItem_WhenInputAmbiguouslyMatchesMultipleItems_ReturnsNull()
    {
        DalamudItemOption[] options = [new(1, "Copper Ore"), new(2, "Copper Ore")];

        Assert.Null(DalamudItemAutocompletePresenter.ResolveSelectedItem(options, "Copper Ore", null));
    }

    [Fact]
    public void ResolveSelectedItem_WhenSelectedItemStillMatchesName_KeepsSelectedItem()
    {
        var selected = new DalamudItemOption(2, "Copper Ore");
        DalamudItemOption[] options = [new(1, "Copper Ore"), selected];

        Assert.Same(selected, DalamudItemAutocompletePresenter.ResolveSelectedItem(options, "copper ore", selected));
    }

    [Fact]
    public void GetSearchResults_OrdersPrefixMatchesBeforeContainsMatches()
    {
        var results = DalamudItemAutocompletePresenter.GetSearchResults(Options(), "shard");

        Assert.Equal(["Shard Glue", "Fire Shard", "Lightning Shard"], results.Select(item => item.Name));
    }

    [Fact]
    public void FormatDisplayName_WhenNameIsDuplicated_AddsStableDuplicateOrdinal()
    {
        DalamudItemOption[] options = [new(10, "Copper Ore"), new(8, "Copper Ore"), new(12, "Silver Ore")];

        Assert.Equal("Copper Ore - duplicate 1", DalamudItemAutocompletePresenter.FormatDisplayName(options, options[1]));
        Assert.Equal("Copper Ore - duplicate 2", DalamudItemAutocompletePresenter.FormatDisplayName(options, options[0]));
    }

    [Fact]
    public void StateResolve_ReusesSnapshotUntilSearchStateChanges()
    {
        var options = Options();
        var state = new DalamudItemAutocompleteState { SearchBuffer = "shard" };

        var first = state.Resolve(options);
        var second = state.Resolve(options);
        state.SearchBuffer = "fire";
        var changed = state.Resolve(options);

        Assert.Same(first, second);
        Assert.NotSame(first, changed);
        Assert.Equal(["Fire Shard"], changed.SearchResults.Select(item => item.Name));
    }

    private static IReadOnlyList<DalamudItemOption> Options() =>
    [
        new(4, "Lightning Shard"),
        new(2, "Fire Shard"),
        new(8, "Shard Glue"),
    ];
}
