using Franthropy.Dalamud.UI.Tables;

namespace Franthropy.Dalamud.Tests.UI.Tables;

public sealed class TableSelectionModelTests
{
    private static readonly int[] Rows = [10, 20, 30, 40, 50];

    [Fact]
    public void PlainClick_ReplacesSelectionAndSetsNewAnchor()
    {
        var selection = new TableSelectionModel<int>();
        selection.SetSelected(10, true);
        selection.SetSelected(20, true);

        selection.ApplyClick(Rows, 3, control: false, shift: false);

        Assert.Equal([40], selection.SelectedKeys);
    }

    [Fact]
    public void ControlClick_AddsWithoutClearingOtherRows()
    {
        var selection = new TableSelectionModel<int>();
        selection.ApplyClick(Rows, 1, control: false, shift: false);

        selection.ApplyClick(Rows, 3, control: true, shift: false);
        selection.ApplyClick(Rows, 1, control: true, shift: false);

        Assert.Equal([20, 40], selection.SelectedKeys.Order());
    }

    [Fact]
    public void AltClick_RemovesWithoutClearingOtherRows()
    {
        var selection = new TableSelectionModel<int>();
        selection.SetSelected(20, true);
        selection.SetSelected(40, true);

        selection.ApplyClick(Rows, 1, control: false, shift: false, alt: true);

        Assert.Equal([40], selection.SelectedKeys);
    }

    [Fact]
    public void AltDrag_RemovesContiguousRows()
    {
        var selection = new TableSelectionModel<int>();
        foreach (var row in Rows)
            selection.SetSelected(row, true);

        selection.ApplyClick(Rows, 1, control: false, shift: false, alt: true);
        selection.ApplyDrag(Rows, 3);

        Assert.Equal([10, 50], selection.SelectedKeys.Order());
    }

    [Fact]
    public void ShiftClick_SelectsContiguousRangeFromAnchor()
    {
        var selection = new TableSelectionModel<int>();
        selection.ApplyClick(Rows, 1, control: false, shift: false);

        selection.ApplyClick(Rows, 4, control: false, shift: true);

        Assert.Equal([20, 30, 40, 50], selection.SelectedKeys.Order());
    }

    [Fact]
    public void ModifierShiftClick_AddsOrRemovesAnchoredRange()
    {
        var selection = new TableSelectionModel<int>();
        selection.ApplyClick(Rows, 1, control: false, shift: false);
        selection.SetSelected(10, true);

        selection.ApplyClick(Rows, 3, control: true, shift: true);
        Assert.Equal([10, 20, 30, 40], selection.SelectedKeys.Order());

        selection.ApplyClick(Rows, 2, control: false, shift: true, alt: true);
        Assert.Equal([10, 40], selection.SelectedKeys.Order());
    }

    [Fact]
    public void Drag_SelectsContiguousRowsFromPressedRow()
    {
        var selection = new TableSelectionModel<int>();
        selection.ApplyClick(Rows, 3, control: false, shift: false);

        selection.ApplyDrag(Rows, 1);
        selection.EndDrag();

        Assert.Equal([20, 30, 40], selection.SelectedKeys.Order());
        Assert.False(selection.IsDragging);
    }

    [Fact]
    public void Single_mode_ignores_multi_select_modifiers_and_never_starts_a_drag()
    {
        var selection = new TableSelectionModel<int>();
        selection.SetSelected(10, true);
        selection.SetSelected(20, true);

        selection.ApplyClick(
            Rows,
            3,
            DalamudTableSelectionMode.Single,
            control: true,
            shift: true);

        Assert.Equal([40], selection.SelectedKeys);
        Assert.False(selection.IsDragging);
    }

    [Fact]
    public void None_mode_does_not_mutate_selection()
    {
        var selection = new TableSelectionModel<int>();
        selection.SetSelected(10, true);

        var changed = selection.ApplyClick(
            Rows,
            3,
            DalamudTableSelectionMode.None,
            control: false,
            shift: false);

        Assert.False(changed);
        Assert.Equal([10], selection.SelectedKeys);
    }

    [Fact]
    public void Table_selection_projects_row_keys_only_for_selection_operations()
    {
        var selection = new TableSelectionModel<int>();
        var tableSelection = DalamudTableSelection<TestRow>.Multi(selection, row => row.Id);
        TestRow[] rows = [new(10), new(20), new(30)];

        tableSelection.ApplyClick(rows, 0, control: false, shift: false, alt: false);
        tableSelection.ApplyClick(rows, 2, control: true, shift: false, alt: false);

        Assert.Equal(DalamudTableSelectionMode.Multi, tableSelection.Mode);
        Assert.True(tableSelection.IsSelected(rows[0]));
        Assert.True(tableSelection.IsSelected(rows[2]));
    }

    [Fact]
    public void Table_selection_excludes_ineligible_rows_from_anchored_ranges()
    {
        var selection = new TableSelectionModel<int>();
        var tableSelection = DalamudTableSelection<TestRow>.Multi(
            selection,
            row => row.Id,
            row => row.Id != 20);
        TestRow[] rows = [new(10), new(20), new(30), new(40)];

        tableSelection.ApplyClick(rows, 0, control: false, shift: false, alt: false);
        tableSelection.ApplyClick(rows, 3, control: false, shift: true, alt: false);

        Assert.Equal([10, 30, 40], selection.SelectedKeys.Order());
        Assert.False(tableSelection.IsSelectable(rows[1]));
    }

    [Fact]
    public void Retain_DropsRowsThatNoLongerExist()
    {
        var selection = new TableSelectionModel<int>();
        selection.SetSelected(10, true);
        selection.SetSelected(30, true);

        var changed = selection.Retain([20, 30, 40]);

        Assert.True(changed);
        Assert.Equal([30], selection.SelectedKeys);
    }

    private sealed record TestRow(int Id);
}
