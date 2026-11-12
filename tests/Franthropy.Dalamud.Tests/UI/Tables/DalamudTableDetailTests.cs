using Franthropy.Dalamud.UI.Tables;

namespace Franthropy.Dalamud.Tests.UI.Tables;

public sealed class DalamudTableDetailTests
{
    [Theory]
    [InlineData(1u, 1u, 2u, 2u, true)]
    [InlineData(1u, 3u, 2u, 2u, false)]
    [InlineData(1u, 0u, 2u, 2u, false)]
    [InlineData(1u, 1u, 2u, 3u, false)]
    [InlineData(0u, 0u, 2u, 2u, false)]
    public void Restore_only_the_same_table_and_window(uint expectedTable, uint currentTable,
        uint expectedWindow, uint currentWindow, bool expected) =>
        Assert.Equal(expected, DalamudTableDetailRow.CanRestoreScope(expectedTable, currentTable, expectedWindow, currentWindow));

    [Theory]
    [InlineData(100, 640, 4, 104, 636)]
    [InlineData(100, 102, 4, 101, 101)]
    [InlineData(100, 100, 4, 100, 100)]
    public void Detail_width_uses_viewport_not_individual_column_width(
        float left, float right, float padding, float expectedMin, float expectedMax)
    {
        Assert.Equal((expectedMin, expectedMax), DalamudTableDetailRow.ContentBounds(left, right, padding));
    }

    [Fact]
    public void Caller_owned_expansion_follows_identity_after_sort_and_toggles_without_a_second_model()
    {
        int? expanded = 20;
        var selection = DalamudTableSelection<int>.Single(r => expanded == r,
            r => expanded = expanded == r ? null : r);
        int[] sorted = [30, 20, 10];
        Assert.True(selection.IsSelected(sorted[1]));
        selection.ApplyClick(sorted, 2, false, false, false);
        Assert.Equal(10, expanded);
        Assert.False(selection.IsSelected(20));
        selection.ApplyClick(sorted, 2, true, true, true);
        Assert.Null(expanded);
        Assert.False(selection.IsDragging);
    }
}
