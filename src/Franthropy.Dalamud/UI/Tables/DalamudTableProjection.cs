using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Tables;

public sealed record DalamudTableColumn<TRow>(
    string Label,
    float Width,
    Func<TRow, string> Text,
    Func<TRow, IComparable>? SortKey = null,
    ImGuiTableColumnFlags Flags = ImGuiTableColumnFlags.WidthFixed);

public sealed class DalamudTableProjection<TRow>
{
    private readonly IReadOnlyList<DalamudTableColumn<TRow>> columns;
    private readonly string[] filters;

    public DalamudTableProjection(IReadOnlyList<DalamudTableColumn<TRow>> columns)
    {
        if (columns.Count == 0)
            throw new ArgumentException("A table projection requires at least one column.", nameof(columns));
        this.columns = columns;
        filters = new string[columns.Count];
        Array.Fill(filters, string.Empty);
    }

    public int ColumnCount => columns.Count;

    public IReadOnlyList<string> Filters => filters;

    public bool Begin(string id, float height, ImGuiTableFlags extraFlags = ImGuiTableFlags.None)
    {
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable |
                    ImGuiTableFlags.Sortable | extraFlags;
        if (!ImGui.BeginTable(id, columns.Count, flags, new Vector2(0, height)))
            return false;
        foreach (var column in columns)
            ImGui.TableSetupColumn(column.Label, column.Flags, column.Width);
        ImGui.TableSetupScrollFreeze(1, 1);
        ImGui.TableHeadersRow();
        return true;
    }

    public void End() => ImGui.EndTable();

    public void DrawFilterRow()
    {
        for (var index = 0; index < columns.Count; index++)
        {
            ImGui.TableNextColumn();
            var current = filters[index];
            if (ImGui.InputTextWithHint($"##filter{index}", columns[index].Label, ref current, 64))
                filters[index] = current;
        }
    }

    public IReadOnlyList<TRow> Apply(IEnumerable<TRow> rows, ImGuiTableSortSpecsPtr sortSpecs)
    {
        var filtered = rows.Where(MatchesAllFilters).ToArray();
        if (sortSpecs.Handle == null || sortSpecs.SpecsCount == 0)
            return filtered;

        var spec = sortSpecs.Specs;
        var columnIndex = (int)spec.ColumnIndex;
        if (columnIndex < 0 || columnIndex >= columns.Count)
            return filtered;

        var key = columns[columnIndex].SortKey ?? (row => columns[columnIndex].Text(row));
        var sorted = spec.SortDirection == ImGuiSortDirection.Descending
            ? filtered.OrderByDescending(row => key(row))
            : filtered.OrderBy(row => key(row));
        return sorted.ToArray();
    }

    private bool MatchesAllFilters(TRow row)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            var filter = filters[index];
            if (!string.IsNullOrWhiteSpace(filter) &&
                !columns[index].Text(row).Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
