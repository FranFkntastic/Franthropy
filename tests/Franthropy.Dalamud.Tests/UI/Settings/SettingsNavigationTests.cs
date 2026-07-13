using Franthropy.Dalamud.UI.Settings;

namespace Franthropy.Dalamud.Tests.UI.Settings;

public sealed class SettingsNavigationTests
{
    [Fact]
    public void SearchMatchesPathsLabelsAndDescriptionsAcrossMultipleTokens()
    {
        var catalog = CreateCatalog();

        var matches = catalog.GetVisiblePages("blue cleanup");

        var page = Assert.Single(matches);
        Assert.Equal("squire.policy", page.Id);
    }

    [Fact]
    public void HiddenPagesNeverResolveOrAppearInSearch()
    {
        var catalog = CreateCatalog(includePrivate: false);
        var state = new SettingsNavigationState("market.operation");

        Assert.DoesNotContain(catalog.GetVisiblePages("market"), page => page.Id == "market.operation");
        Assert.Equal("general.server", catalog.ResolveSelectedPage(state)!.Id);
    }

    [Fact]
    public void FilteringUsesEphemeralSelectionAndRestoresPreviousPageWhenCleared()
    {
        var catalog = CreateCatalog();
        var state = new SettingsNavigationState("general.server");

        state.SetFilter("cleanup");
        Assert.Equal("squire.policy", catalog.ResolveSelectedPage(state)!.Id);
        state.SelectPage("squire.exclusions");
        Assert.Equal("squire.exclusions", catalog.ResolveSelectedPage(state)!.Id);

        state.SetFilter(string.Empty);

        Assert.Equal("general.server", catalog.ResolveSelectedPage(state)!.Id);
    }

    [Fact]
    public void DuplicateStableIdsAndPathsAreRejected()
    {
        var page = Page("same", "General / Server");
        Assert.Throws<ArgumentException>(() => new SettingsNavigationCatalog([page, Page("same", "Squire / Policy")]));
        Assert.Throws<ArgumentException>(() => new SettingsNavigationCatalog([page, Page("other", "general/server")]));
    }

    [Fact]
    public void PageContextShowsAllControlsForAPathMatchAndNarrowsForAControlMatch()
    {
        var pathMatch = new SettingsPageContext("squire policy", "Squire / Cleanup Policy");
        Assert.True(pathMatch.Matches("Protect blue and purple gear"));

        var controlMatch = new SettingsPageContext("materia risk", "Squire / Cleanup Policy");
        Assert.True(controlMatch.Matches("Allow materia retrieval with loss risk"));
        Assert.False(controlMatch.Matches("Protect player-signed gear"));
    }

    [Fact]
    public void ExpandedFolderStateUsesStablePaths()
    {
        var state = new SettingsNavigationState(expandedFolderPaths: ["Squire"]);
        Assert.Contains("Squire", state.ExpandedFolderPaths);

        Assert.True(state.SetFolderExpanded("Advanced", true));
        Assert.True(state.SetFolderExpanded("Squire", false));

        Assert.Contains("Advanced", state.ExpandedFolderPaths);
        Assert.DoesNotContain("Squire", state.ExpandedFolderPaths);
    }

    private static SettingsNavigationCatalog CreateCatalog(bool includePrivate = true) => new(
    [
        Page("general.server", "General / Server Connection", 0, terms: ["receiver URL", "API key"]),
        Page("squire.policy", "Squire / Cleanup Policy", 10, terms: ["protect blue gear", "materia cleanup"]),
        Page("squire.exclusions", "Squire / Cleanup Exclusions", 11, terms: ["character blacklist cleanup"]),
        Page("market.operation", "Market Acquisition / Operation", 20, () => includePrivate, ["world travel"]),
    ]);

    private static SettingsPageDescriptor Page(
        string id,
        string path,
        int order = 0,
        Func<bool>? visible = null,
        IEnumerable<string>? terms = null) => new(id, path, _ => { }, order, visible, terms);
}
