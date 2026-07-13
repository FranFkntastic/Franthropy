namespace Franthropy.Dalamud.UI.Settings;

public sealed class SettingsPageDescriptor
{
    private readonly string searchText;

    public SettingsPageDescriptor(
        string id,
        string path,
        Action<SettingsPageContext> draw,
        int order = 0,
        Func<bool>? isVisible = null,
        IEnumerable<string>? searchTerms = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(draw);

        Id = id.Trim();
        Segments = path.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (Segments.Count == 0)
            throw new ArgumentException("A settings page path must contain at least one segment.", nameof(path));

        Path = string.Join(" / ", Segments);
        Draw = draw;
        Order = order;
        IsVisible = isVisible ?? (() => true);
        var terms = searchTerms?.Where(term => !string.IsNullOrWhiteSpace(term)).Select(term => term.Trim()) ?? [];
        searchText = string.Join('\n', terms.Prepend(Path));
    }

    public string Id { get; }
    public string Path { get; }
    public IReadOnlyList<string> Segments { get; }
    public string Name => Segments[^1];
    public int Order { get; }
    public Func<bool> IsVisible { get; }
    public Action<SettingsPageContext> Draw { get; }

    public bool Matches(string filter)
    {
        var tokens = SettingsNavigationCatalog.Tokenize(filter);
        return tokens.Count == 0 || tokens.All(token => searchText.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record SettingsPageContext(string Filter, string PagePath = "")
{
    public bool IsFiltering => !string.IsNullOrWhiteSpace(Filter);

    public bool Matches(params string[] text)
    {
        var tokens = SettingsNavigationCatalog.Tokenize(Filter);
        if (tokens.Count == 0)
            return true;
        if (tokens.All(token => PagePath.Contains(token, StringComparison.OrdinalIgnoreCase)))
            return true;
        var searchText = string.Join('\n', text.Where(value => !string.IsNullOrWhiteSpace(value)));
        return tokens.All(token => searchText.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class SettingsNavigationState
{
    private string? selectionBeforeFilter;
    private string? filteredPageId;

    public SettingsNavigationState(string? selectedPageId = null, IEnumerable<string>? expandedFolderPaths = null)
    {
        SelectedPageId = selectedPageId;
        ExpandedFolderPaths = new HashSet<string>(expandedFolderPaths ?? [], StringComparer.Ordinal);
    }

    public string? SelectedPageId { get; private set; }
    public string Filter { get; private set; } = string.Empty;
    public HashSet<string> ExpandedFolderPaths { get; }

    public void SetFilter(string filter)
    {
        filter ??= string.Empty;
        var wasFiltering = !string.IsNullOrWhiteSpace(Filter);
        var isFiltering = !string.IsNullOrWhiteSpace(filter);

        if (!wasFiltering && isFiltering)
            selectionBeforeFilter = SelectedPageId;
        else if (wasFiltering && !isFiltering)
        {
            SelectedPageId = selectionBeforeFilter ?? SelectedPageId;
            selectionBeforeFilter = null;
            filteredPageId = null;
        }

        Filter = filter;
    }

    public void SelectPage(string pageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        if (string.IsNullOrWhiteSpace(Filter))
            SelectedPageId = pageId;
        else
            filteredPageId = pageId;
    }

    public string? ResolveRequestedPageId() => string.IsNullOrWhiteSpace(Filter) ? SelectedPageId : filteredPageId ?? SelectedPageId;

    public bool SetFolderExpanded(string folderPath, bool expanded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        return expanded ? ExpandedFolderPaths.Add(folderPath) : ExpandedFolderPaths.Remove(folderPath);
    }
}

public sealed class SettingsNavigationCatalog
{
    private readonly SettingsPageDescriptor[] pages;

    public SettingsNavigationCatalog(IEnumerable<SettingsPageDescriptor> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        this.pages = pages.OrderBy(page => page.Order).ThenBy(page => page.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (this.pages.Length == 0)
            throw new ArgumentException("At least one settings page is required.", nameof(pages));

        var duplicateId = this.pages.GroupBy(page => page.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
            throw new ArgumentException($"Settings page ID '{duplicateId.Key}' is registered more than once.", nameof(pages));

        var duplicatePath = this.pages.GroupBy(page => page.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicatePath is not null)
            throw new ArgumentException($"Settings page path '{duplicatePath.Key}' is registered more than once.", nameof(pages));
    }

    public IReadOnlyList<SettingsPageDescriptor> GetVisiblePages(string? filter = null) => pages
        .Where(page => page.IsVisible())
        .Where(page => page.Matches(filter ?? string.Empty))
        .ToArray();

    public SettingsPageDescriptor? ResolveSelectedPage(SettingsNavigationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var visible = GetVisiblePages(state.Filter);
        if (visible.Count == 0)
            return null;

        var requestedId = state.ResolveRequestedPageId();
        return visible.FirstOrDefault(page => string.Equals(page.Id, requestedId, StringComparison.Ordinal)) ?? visible[0];
    }

    internal static IReadOnlyList<string> Tokenize(string? filter) => string.IsNullOrWhiteSpace(filter)
        ? []
        : filter.Split((char[]?)null, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
