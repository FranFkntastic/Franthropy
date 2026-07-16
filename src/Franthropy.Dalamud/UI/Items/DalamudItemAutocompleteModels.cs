namespace Franthropy.Dalamud.UI.Items;

public sealed record DalamudItemOption(uint ItemId, string Name);

public sealed class DalamudItemAutocompleteState
{
    private IReadOnlyList<DalamudItemOption>? cachedOptions;
    private string cachedSearchBuffer = string.Empty;
    private uint? cachedSelectedItemId;
    private DalamudItemAutocompleteSnapshot? cachedSnapshot;

    public string SearchBuffer { get; set; } = string.Empty;
    public DalamudItemOption? SelectedItem { get; set; }

    public DalamudItemAutocompleteSnapshot Resolve(IReadOnlyList<DalamudItemOption> itemOptions)
    {
        ArgumentNullException.ThrowIfNull(itemOptions);
        if (ReferenceEquals(cachedOptions, itemOptions) &&
            string.Equals(cachedSearchBuffer, SearchBuffer, StringComparison.Ordinal) &&
            cachedSelectedItemId == SelectedItem?.ItemId &&
            cachedSnapshot is not null)
        {
            return cachedSnapshot;
        }

        var resolved = DalamudItemAutocompletePresenter.ResolveSelectedItem(itemOptions, SearchBuffer, SelectedItem);
        cachedOptions = itemOptions;
        cachedSearchBuffer = SearchBuffer;
        cachedSelectedItemId = SelectedItem?.ItemId;
        cachedSnapshot = new(
            resolved,
            resolved is null
                ? DalamudItemAutocompletePresenter.GetSearchResults(itemOptions, SearchBuffer)
                : []);
        return cachedSnapshot;
    }
}

public sealed record DalamudItemAutocompleteSnapshot(
    DalamudItemOption? ResolvedItem,
    IReadOnlyList<DalamudItemOption> SearchResults);

public static class DalamudItemAutocompletePresenter
{
    public static DalamudItemOption? ResolveSelectedItem(
        IReadOnlyList<DalamudItemOption> itemOptions,
        string searchBuffer,
        DalamudItemOption? selectedItem)
    {
        ArgumentNullException.ThrowIfNull(itemOptions);
        var search = searchBuffer.Trim();
        if (selectedItem is not null &&
            selectedItem.Name.Equals(search, StringComparison.OrdinalIgnoreCase))
        {
            return selectedItem;
        }

        if (search.Length == 0)
            return null;

        DalamudItemOption? exactMatch = null;
        foreach (var option in itemOptions)
        {
            if (!option.Name.Equals(search, StringComparison.OrdinalIgnoreCase))
                continue;
            if (exactMatch is not null)
                return null;
            exactMatch = option;
        }

        return exactMatch;
    }

    public static IReadOnlyList<DalamudItemOption> GetSearchResults(
        IReadOnlyList<DalamudItemOption> itemOptions,
        string searchBuffer,
        int limit = 10)
    {
        ArgumentNullException.ThrowIfNull(itemOptions);
        var search = searchBuffer.Trim();
        if (search.Length < 2 || limit <= 0)
            return [];

        return itemOptions
            .Where(item => item.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.Name.Length)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ItemId)
            .Take(limit)
            .ToList();
    }

    public static string FormatDisplayName(
        IReadOnlyList<DalamudItemOption> itemOptions,
        DalamudItemOption option)
    {
        ArgumentNullException.ThrowIfNull(itemOptions);
        ArgumentNullException.ThrowIfNull(option);
        var duplicates = itemOptions
            .Where(item => item.Name.Equals(option.Name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.ItemId)
            .ToArray();
        if (duplicates.Length <= 1)
            return option.Name;

        var ordinal = Array.FindIndex(duplicates, item => item.ItemId == option.ItemId);
        return ordinal < 0
            ? $"{option.Name} - duplicate"
            : $"{option.Name} - duplicate {ordinal + 1}";
    }
}
