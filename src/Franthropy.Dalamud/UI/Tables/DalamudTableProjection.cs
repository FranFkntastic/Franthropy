using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Tables;

public enum DalamudTableCellAlignment
{
    Left,
    Right,
}

public sealed record DalamudTableColumn<TRow>(
    string Label,
    float Width,
    Func<TRow, string> Text,
    Func<TRow, IComparable>? SortKey = null,
    ImGuiTableColumnFlags Flags = ImGuiTableColumnFlags.WidthFixed,
    Action<TRow>? Draw = null,
    Func<TRow, Vector4?>? TextColor = null,
    DalamudTableCellAlignment Alignment = DalamudTableCellAlignment.Left);

public readonly record struct DalamudTableLayout(
    Vector2 Size,
    ImGuiTableFlags Flags,
    int FreezeColumns = 0,
    int FreezeRows = 0)
{
    public const ImGuiTableFlags DefaultFlags =
        ImGuiTableFlags.RowBg |
        ImGuiTableFlags.Borders |
        ImGuiTableFlags.Resizable |
        ImGuiTableFlags.Reorderable |
        ImGuiTableFlags.Hideable |
        ImGuiTableFlags.Sortable;

    public static DalamudTableLayout FitContent(ImGuiTableFlags flags) =>
        new(Vector2.Zero, flags);

    public static DalamudTableLayout Scrolling(
        float height,
        ImGuiTableFlags extraFlags = ImGuiTableFlags.None) =>
        new(
            new Vector2(0f, height),
            DefaultFlags |
            ImGuiTableFlags.ScrollY |
            extraFlags,
            FreezeColumns: 1,
            FreezeRows: 1);
}

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
        return Begin(id, DalamudTableLayout.Scrolling(height, extraFlags));
    }

    public bool Begin(string id, DalamudTableLayout layout)
    {
        if (!ImGui.BeginTable(id, columns.Count, layout.Flags, layout.Size))
            return false;
        foreach (var column in columns)
            ImGui.TableSetupColumn(column.Label, column.Flags, column.Width);
        if (layout.FreezeColumns > 0 || layout.FreezeRows > 0)
            ImGui.TableSetupScrollFreeze(layout.FreezeColumns, layout.FreezeRows);
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

    public void DrawRow(TRow row)
    {
        ImGui.TableNextRow();
        foreach (var column in columns)
        {
            ImGui.TableNextColumn();
            if (column.Draw is not null)
            {
                column.Draw(row);
                continue;
            }

            var text = column.Text(row);
            if (column.Alignment == DalamudTableCellAlignment.Right)
            {
                var width = ImGui.CalcTextSize(text).X;
                ImGui.SetCursorPosX(
                    Math.Max(
                        ImGui.GetCursorPosX(),
                        ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width));
            }

            if (column.TextColor?.Invoke(row) is { } color)
                ImGui.TextColored(color, text);
            else
                ImGui.TextUnformatted(text);
        }
    }

    public unsafe IReadOnlyList<TRow> Apply(IEnumerable<TRow> rows, ImGuiTableSortSpecsPtr sortSpecs)
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
