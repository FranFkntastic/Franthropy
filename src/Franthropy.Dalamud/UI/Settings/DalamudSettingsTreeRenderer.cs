using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Franthropy.Dalamud.UI.Styling;

namespace Franthropy.Dalamud.UI.Settings;

public sealed class DalamudSettingsTreeRenderer
{
    private const float CompactWidthThreshold = 720f;
    private static readonly DalamudUiTheme DefaultTheme = DalamudUiTheme.Dark(new Vector4(0.25f, 0.65f, 0.95f, 1f));
    private readonly string id;
    private readonly List<SettingsPageControl> renderedPageControls = [];
    private readonly List<SettingsFolderControl> renderedFolderControls = [];

    public DalamudSettingsTreeRenderer(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        this.id = id;
    }

    public IReadOnlyList<SettingsPageControl> RenderedPageControls => renderedPageControls;
    public IReadOnlyList<SettingsFolderControl> RenderedFolderControls => renderedFolderControls;

    public void Draw(SettingsNavigationCatalog catalog, SettingsNavigationState state, Action? navigationChanged = null)
        => Draw(catalog, state, DefaultTheme, navigationChanged);

    public void Draw(
        SettingsNavigationCatalog catalog,
        SettingsNavigationState state,
        DalamudUiTheme theme,
        Action? navigationChanged = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(theme);
        renderedPageControls.Clear();
        renderedFolderControls.Clear();

        var available = ImGui.GetContentRegionAvail();
        if (available.X < CompactWidthThreshold)
            DrawCompact(catalog, state, theme, navigationChanged);
        else
            DrawSplit(catalog, state, theme, available, navigationChanged);
    }

    private void DrawSplit(
        SettingsNavigationCatalog catalog,
        SettingsNavigationState state,
        DalamudUiTheme theme,
        Vector2 available,
        Action? navigationChanged)
    {
        var navigationWidth = Math.Clamp(available.X * 0.24f, 210f, 300f);
        using var colors = ImRaii.PushColor(ImGuiCol.TableBorderStrong, theme.Palette.Border)
            .Push(ImGuiCol.TableBorderLight, DalamudUiPalette.WithAlpha(theme.Palette.Border, 0.58f));
        using var style = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, theme.Metrics.WindowPadding);
        if (!ImGui.BeginTable(
                $"##SettingsLayout{id}",
                2,
                ImGuiTableFlags.SizingStretchProp |
                ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.NoSavedSettings))
            return;

        ImGui.TableSetupColumn(
            "Navigation",
            ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize,
            navigationWidth);
        ImGui.TableSetupColumn("Settings", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.GetColorU32(theme.Palette.Surface));
        {
            DrawFilter(state, theme);
            ImGui.Separator();
            DrawTree(catalog, state, theme, navigationChanged);
        }

        ImGui.TableNextColumn();
        DrawSelectedPage(catalog, state, theme);
        ImGui.EndTable();
    }

    private void DrawCompact(
        SettingsNavigationCatalog catalog,
        SettingsNavigationState state,
        DalamudUiTheme theme,
        Action? navigationChanged)
    {
        DrawFilter(state, theme);
        var visible = catalog.GetVisiblePages(state.Filter);
        var selected = catalog.ResolveSelectedPage(state);
        ImGui.SetNextItemWidth(-1);
        using var input = DalamudUiChrome.PushInput(theme);
        if (ImGui.BeginCombo($"##SettingsPageSelector{id}", selected?.Path ?? "No matching settings page"))
        {
            foreach (var page in visible)
            {
                var isSelected = string.Equals(page.Id, selected?.Id, StringComparison.Ordinal);
                if (ImGui.Selectable($"{page.Path}##SettingsPageCompact{id}{page.Id}", isSelected))
                {
                    state.SelectPage(page.Id);
                    navigationChanged?.Invoke();
                }

                RecordControl(page, isSelected);
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.Separator();
        DrawSelectedPage(catalog, state, theme);
    }

    private static void DrawFilter(SettingsNavigationState state, DalamudUiTheme theme)
    {
        var filter = state.Filter;
        ImGui.SetNextItemWidth(-1);
        using var input = DalamudUiChrome.PushInput(theme);
        if (ImGui.InputTextWithHint("##SettingsFilter", "Search settings...", ref filter, 256))
            state.SetFilter(filter);
    }

    private void DrawTree(
        SettingsNavigationCatalog catalog,
        SettingsNavigationState state,
        DalamudUiTheme theme,
        Action? navigationChanged)
    {
        var visible = catalog.GetVisiblePages(state.Filter);
        if (visible.Count == 0)
        {
            ImGui.TextWrapped("No settings match this search.");
            return;
        }

        var roots = BuildFolders(visible);
        var selected = catalog.ResolveSelectedPage(state);
        foreach (var folder in roots)
            DrawFolder(folder, selected, state, theme, navigationChanged);
    }

    private void DrawFolder(
        Folder folder,
        SettingsPageDescriptor? selected,
        SettingsNavigationState state,
        DalamudUiTheme theme,
        Action? navigationChanged)
    {
        var forceOpen = !string.IsNullOrWhiteSpace(state.Filter);
        var expanded = forceOpen || state.ExpandedFolderPaths.Contains(folder.Path);
        ImGui.SetNextItemOpen(expanded, ImGuiCond.Always);
        var open = ImGui.TreeNodeEx($"{folder.Name}##SettingsFolder{id}{folder.Path}", ImGuiTreeNodeFlags.SpanAvailWidth);
        renderedFolderControls.Add(new SettingsFolderControl(
            folder.Path,
            folder.Name,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            open));
        if (!forceOpen && ImGui.IsItemToggledOpen() && state.SetFolderExpanded(folder.Path, open))
            navigationChanged?.Invoke();

        if (!open)
            return;

        foreach (var child in folder.Children)
            DrawFolder(child, selected, state, theme, navigationChanged);

        foreach (var page in folder.Pages)
        {
            var isSelected = string.Equals(page.Id, selected?.Id, StringComparison.Ordinal);
            if (DalamudUiControls.SegmentedOption(
                    $"{page.Name}##SettingsPage{id}{page.Id}",
                    isSelected,
                    theme,
                    new Vector2(-1f, 0f)))
            {
                state.SelectPage(page.Id);
                navigationChanged?.Invoke();
            }
            RecordControl(page, isSelected);
        }

        ImGui.TreePop();
    }

    private static void DrawSelectedPage(
        SettingsNavigationCatalog catalog,
        SettingsNavigationState state,
        DalamudUiTheme theme)
    {
        var selected = catalog.ResolveSelectedPage(state);
        if (selected is null)
        {
            ImGui.TextWrapped("No visible settings page matches the current search.");
            return;
        }

        var parentPath = selected.Segments.Count > 1
            ? string.Join(" / ", selected.Segments.Take(selected.Segments.Count - 1))
            : null;
        DalamudUiChrome.DrawSectionHeading(
            selected.Name,
            parentPath,
            theme.Palette);
        selected.Draw(new SettingsPageContext(state.Filter, selected.Path));
    }

    private void RecordControl(SettingsPageDescriptor page, bool selected) => renderedPageControls.Add(new SettingsPageControl(
        page.Id,
        page.Path,
        ImGui.GetItemRectMin(),
        ImGui.GetItemRectMax(),
        selected));

    private static IReadOnlyList<Folder> BuildFolders(IReadOnlyList<SettingsPageDescriptor> pages)
    {
        var roots = new Dictionary<string, MutableFolder>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        {
            if (page.Segments.Count == 1)
            {
                var root = GetOrAdd(roots, "General", "General", page.Order);
                root.Pages.Add(page);
                continue;
            }

            IDictionary<string, MutableFolder> current = roots;
            MutableFolder? folder = null;
            var path = string.Empty;
            for (var index = 0; index < page.Segments.Count - 1; index++)
            {
                var segment = page.Segments[index];
                path = string.IsNullOrEmpty(path) ? segment : $"{path} / {segment}";
                folder = GetOrAdd(current, segment, path, page.Order);
                folder.Order = Math.Min(folder.Order, page.Order);
                current = folder.Children;
            }
            folder!.Pages.Add(page);
        }

        return roots.Values
            .OrderBy(folder => folder.Order)
            .ThenBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
            .Select(Freeze)
            .ToArray();
    }

    private static MutableFolder GetOrAdd(IDictionary<string, MutableFolder> folders, string name, string path, int order)
    {
        if (!folders.TryGetValue(name, out var folder))
            folders[name] = folder = new MutableFolder(name, path, order);
        return folder;
    }

    private static Folder Freeze(MutableFolder folder) => new(
        folder.Name,
        folder.Path,
        folder.Order,
        folder.Children.Values
            .OrderBy(child => child.Order)
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .Select(Freeze)
            .ToArray(),
        folder.Pages.OrderBy(page => page.Order).ThenBy(page => page.Name, StringComparer.OrdinalIgnoreCase).ToArray());

    private sealed class MutableFolder(string name, string path, int order)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public int Order { get; set; } = order;
        public Dictionary<string, MutableFolder> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<SettingsPageDescriptor> Pages { get; } = [];
    }

    private sealed record Folder(
        string Name,
        string Path,
        int Order,
        IReadOnlyList<Folder> Children,
        IReadOnlyList<SettingsPageDescriptor> Pages);
}

public sealed record SettingsPageControl(string Id, string Label, Vector2 Min, Vector2 Max, bool Selected);
public sealed record SettingsFolderControl(string Path, string Label, Vector2 Min, Vector2 Max, bool Expanded);
