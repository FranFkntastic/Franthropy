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
    public void ControlClick_TogglesWithoutClearingOtherRows()
    {
        var selection = new TableSelectionModel<int>();
        selection.ApplyClick(Rows, 1, control: false, shift: false);

        selection.ApplyClick(Rows, 3, control: true, shift: false);
        selection.ApplyClick(Rows, 1, control: true, shift: false);

        Assert.Equal([40], selection.SelectedKeys);
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
    public void Retain_DropsRowsThatNoLongerExist()
    {
        var selection = new TableSelectionModel<int>();
        selection.SetSelected(10, true);
        selection.SetSelected(30, true);

        var changed = selection.Retain([20, 30, 40]);

        Assert.True(changed);
        Assert.Equal([30], selection.SelectedKeys);
    }
}
