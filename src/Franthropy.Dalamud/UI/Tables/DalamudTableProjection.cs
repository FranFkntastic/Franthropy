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
    DalamudTableCellAlignment Alignment = DalamudTableCellAlignment.Left,
    Action<TRow>? DrawContextMenu = null);

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

    public void DrawRow(
        TRow row,
        Vector4? background = null,
        float minimumHeight = 0f,
        string? id = null)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.None, minimumHeight);
        if (background is { } rowBackground)
        {
            ImGui.TableSetBgColor(
                ImGuiTableBgTarget.RowBg0,
                ImGui.GetColorU32(rowBackground));
        }
        DrawCells(row, id);
    }

    public void DrawMessageRow(
        string message,
        int columnIndex = 0,
        Vector4? textColor = null,
        float minimumHeight = 0f)
    {
        ArgumentNullException.ThrowIfNull(message);
        if ((uint)columnIndex >= (uint)columns.Count)
            throw new ArgumentOutOfRangeException(nameof(columnIndex));

        ImGui.TableNextRow(ImGuiTableRowFlags.None, minimumHeight);
        for (var index = 0; index < columns.Count; index++)
        {
            ImGui.TableNextColumn();
            if (index != columnIndex)
                continue;
            if (textColor is { } color)
                ImGui.TextColored(color, message);
            else
                ImGui.TextDisabled(message);
        }
    }

    public bool DrawSelectableRow<TKey>(
        TRow row,
        TableSelectionModel<TKey> selection,
        IReadOnlyList<TKey> orderedKeys,
        int rowIndex,
        string id,
        Vector4? background = null,
        float minimumHeight = 0f,
        bool enabled = true)
        where TKey : notnull
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.None, minimumHeight);
        if (background is { } rowBackground)
        {
            ImGui.TableSetBgColor(
                ImGuiTableBgTarget.RowBg0,
                ImGui.GetColorU32(rowBackground));
        }

        ImGui.TableNextColumn();
        var cellCursor = ImGui.GetCursorPos();
        DalamudTableSelectionRenderer.DrawRow(
            selection,
            orderedKeys,
            rowIndex,
            id,
            new Vector2(0, Math.Max(minimumHeight, ImGui.GetTextLineHeightWithSpacing())),
            enabled);
        var clicked = enabled && ImGui.IsItemClicked(ImGuiMouseButton.Left);
        ImGui.SetCursorPos(cellCursor);
        DrawCell(columns[0], row, $"{id}:cell:0");

        for (var index = 1; index < columns.Count; index++)
        {
            ImGui.TableNextColumn();
            DrawCell(columns[index], row, $"{id}:cell:{index}");
        }
        return clicked;
    }

    private void DrawCells(TRow row, string? id)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            ImGui.TableNextColumn();
            DrawCell(columns[index], row, id is null ? null : $"{id}:cell:{index}");
        }
    }

    private static void DrawCell(
        DalamudTableColumn<TRow> column,
        TRow row,
        string? id)
    {
        if (column.DrawContextMenu is null || id is null)
        {
            DrawCellContent(column, row);
            return;
        }

        var cursor = ImGui.GetCursorPos();
        ImGui.InvisibleButton(
            $"{id}:context-target",
            new Vector2(
                Math.Max(1, ImGui.GetContentRegionAvail().X),
                ImGui.GetTextLineHeightWithSpacing()),
            ImGuiButtonFlags.MouseButtonRight);
        if (ImGui.IsItemHovered())
            ImGui.TableSetBgColor(
                ImGuiTableBgTarget.CellBg,
                ImGui.GetColorU32(ImGuiCol.HeaderHovered));

        if (ImGui.BeginPopupContextItem($"{id}:context-menu"))
        {
            column.DrawContextMenu(row);
            ImGui.EndPopup();
        }

        ImGui.SetCursorPos(cursor);
        DrawCellContent(column, row);
    }

    private static void DrawCellContent(DalamudTableColumn<TRow> column, TRow row)
    {
        if (column.Draw is not null)
        {
            column.Draw(row);
            return;
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
