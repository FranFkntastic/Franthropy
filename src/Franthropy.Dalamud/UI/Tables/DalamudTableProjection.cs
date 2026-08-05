using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    private readonly DalamudTableSelection<TRow> selection;
    private readonly string[] filters;
    private IEnumerable<TRow>? appliedSource;
    private IReadOnlyList<TRow> appliedRows = [];
    private string[] appliedFilters = [];
    private int appliedSortColumn = -1;
    private ImGuiSortDirection appliedSortDirection = ImGuiSortDirection.None;

    public DalamudTableProjection(
        IReadOnlyList<DalamudTableColumn<TRow>> columns,
        DalamudTableSelection<TRow>? selection = null)
    {
        if (columns.Count == 0)
            throw new ArgumentException("A table projection requires at least one column.", nameof(columns));
        this.columns = columns;
        this.selection = selection ?? DalamudTableSelection<TRow>.None;
        filters = new string[columns.Count];
        Array.Fill(filters, string.Empty);
    }

    public int ColumnCount => columns.Count;
    public int ApplyCount { get; private set; }
    public DalamudTableSelectionMode SelectionMode => selection.Mode;

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

    public void End()
    {
        if (selection.IsDragging && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            selection.EndDrag();
        ImGui.EndTable();
    }

    public void DrawFilterRow()
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (!ImGui.TableNextColumn())
                continue;
            var current = filters[index];
            if (ImGui.InputTextWithHint($"##filter{index}", columns[index].Label, ref current, 64))
                filters[index] = current;
        }
    }

    [Obsolete("DrawRow bypasses table-level selection. Configure DalamudTableSelection and use DrawSaneRow.")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void DrawRow(
        TRow row,
        Vector4? background = null,
        float minimumHeight = 0f,
        string? id = null)
    {
        DalamudTableLegacyUsage.Warn(nameof(DrawRow), Assembly.GetCallingAssembly());
        ImGui.TableNextRow(ImGuiTableRowFlags.None, minimumHeight);
        if (background is { } rowBackground)
        {
            ImGui.TableSetBgColor(
                ImGuiTableBgTarget.RowBg0,
                ImGui.GetColorU32(rowBackground));
        }
        DrawCells(row, id);
    }

    public bool DrawSaneRow(
        IReadOnlyList<TRow> orderedRows,
        int rowIndex,
        string id,
        Vector4? background = null,
        float minimumHeight = 0f,
        bool selectable = true,
        bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(orderedRows);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if ((uint)rowIndex >= (uint)orderedRows.Count)
            throw new ArgumentOutOfRangeException(nameof(rowIndex));
        var row = orderedRows[rowIndex];

        ImGui.TableNextRow(ImGuiTableRowFlags.None, minimumHeight);
        if (background is { } rowBackground)
        {
            ImGui.TableSetBgColor(
                ImGuiTableBgTarget.RowBg0,
                ImGui.GetColorU32(rowBackground));
        }

        var activated = false;
        var usesSelection = selectable && selection.Mode != DalamudTableSelectionMode.None;
        if (ImGui.TableNextColumn())
        {
            if (usesSelection)
            {
                var cellCursor = ImGui.GetCursorPos();
                var interaction = DalamudTableSelectionRenderer.DrawRow(
                    id,
                    selection.IsSelected(row),
                    new Vector2(0, Math.Max(minimumHeight, ImGui.GetTextLineHeightWithSpacing())),
                    enabled);
                activated = interaction.Activated;
                if (interaction.Activated)
                {
                    var io = ImGui.GetIO();
                    selection.ApplyClick(orderedRows, rowIndex, io.KeyCtrl, io.KeyShift);
                }
                if (enabled &&
                    selection.IsDragging &&
                    interaction.Hovered &&
                    ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                {
                    selection.ApplyDrag(orderedRows, rowIndex);
                }
                ImGui.SetCursorPos(cellCursor);
            }

            DrawCell(columns[0], row, $"{id}:cell:0");
        }

        for (var index = 1; index < columns.Count; index++)
        {
            if (ImGui.TableNextColumn())
                DrawCell(columns[index], row, $"{id}:cell:{index}");
        }
        return activated;
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
            var shouldDraw = ImGui.TableNextColumn();
            if (!shouldDraw || index != columnIndex)
                continue;
            if (textColor is { } color)
                ImGui.TextColored(color, message);
            else
                ImGui.TextDisabled(message);
        }
    }

    [Obsolete("DrawSelectableRow duplicates row behavior. Configure DalamudTableSelection and use DrawSaneRow.")]
    [MethodImpl(MethodImplOptions.NoInlining)]
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
        DalamudTableLegacyUsage.Warn(nameof(DrawSelectableRow), Assembly.GetCallingAssembly());
        ImGui.TableNextRow(ImGuiTableRowFlags.None, minimumHeight);
        if (background is { } rowBackground)
        {
            ImGui.TableSetBgColor(
                ImGuiTableBgTarget.RowBg0,
                ImGui.GetColorU32(rowBackground));
        }

        var clicked = false;
        if (ImGui.TableNextColumn())
        {
            var cellCursor = ImGui.GetCursorPos();
            DalamudTableSelectionRenderer.DrawRow(
                selection,
                orderedKeys,
                rowIndex,
                id,
                new Vector2(0, Math.Max(minimumHeight, ImGui.GetTextLineHeightWithSpacing())),
                enabled);
            clicked = enabled && ImGui.IsItemClicked(ImGuiMouseButton.Left);
            ImGui.SetCursorPos(cellCursor);
            DrawCell(columns[0], row, $"{id}:cell:0");
        }

        for (var index = 1; index < columns.Count; index++)
        {
            if (ImGui.TableNextColumn())
                DrawCell(columns[index], row, $"{id}:cell:{index}");
        }
        return clicked;
    }

    public unsafe int DrawClippedRows(
        IReadOnlyList<TRow> rows,
        Action<TRow, int> drawRow,
        float rowHeight = -1f)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(drawRow);
        if (rows.Count == 0)
            return 0;

        var rendered = 0;
        var clipper = ImGui.ImGuiListClipper();
        try
        {
            clipper.Begin(rows.Count, rowHeight);
            while (clipper.Step())
            {
                for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                {
                    drawRow(rows[index], index);
                    rendered++;
                }
            }
        }
        finally
        {
            clipper.Destroy();
        }

        return rendered;
    }

    private void DrawCells(TRow row, string? id)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (ImGui.TableNextColumn())
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

    public IReadOnlyList<TRow> Apply(IEnumerable<TRow> rows)
    {
        unsafe
        {
            return Apply(rows, ImGuiTableSortSpecsPtr.Null);
        }
    }

    public unsafe IReadOnlyList<TRow> Apply(IEnumerable<TRow> rows, ImGuiTableSortSpecsPtr sortSpecs)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var hasSort = sortSpecs.Handle != null && sortSpecs.SpecsCount > 0;
        var sortColumn = hasSort ? (int)sortSpecs.Specs.ColumnIndex : -1;
        var sortDirection = hasSort ? sortSpecs.Specs.SortDirection : ImGuiSortDirection.None;
        if (ReferenceEquals(rows, appliedSource) &&
            sortColumn == appliedSortColumn &&
            sortDirection == appliedSortDirection &&
            filters.SequenceEqual(appliedFilters, StringComparer.Ordinal))
        {
            if (sortSpecs.Handle != null)
                sortSpecs.SpecsDirty = false;
            return appliedRows;
        }

        var filtered = rows.Where(MatchesAllFilters).ToArray();
        IReadOnlyList<TRow> result = filtered;
        if (sortColumn >= 0 && sortColumn < columns.Count)
        {
            var key = columns[sortColumn].SortKey ?? (row => columns[sortColumn].Text(row));
            result = sortDirection == ImGuiSortDirection.Descending
                ? filtered.OrderByDescending(row => key(row)).ToArray()
                : filtered.OrderBy(row => key(row)).ToArray();
        }

        appliedSource = rows;
        appliedRows = result;
        appliedFilters = [.. filters];
        appliedSortColumn = sortColumn;
        appliedSortDirection = sortDirection;
        ApplyCount++;
        if (sortSpecs.Handle != null)
            sortSpecs.SpecsDirty = false;
        return result;
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
