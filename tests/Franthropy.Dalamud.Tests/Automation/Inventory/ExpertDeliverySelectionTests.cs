using Franthropy.Dalamud.Automation.Inventory;

namespace Franthropy.Dalamud.Tests.Automation.Inventory;

public sealed class ExpertDeliverySelectionTests
{
    [Fact]
    public void SelectExactItem_ReturnsOnlyMatchingRow()
    {
        var result = ExpertDeliverySelection.SelectExactItem(20, [new(10, 100, 0), new(20, 200, 1)], out var selected);
        Assert.True(result.Success);
        Assert.Equal(1, selected!.Index);
    }

    [Fact]
    public void SelectExactItem_FailsClosedForDuplicateRows()
    {
        var result = ExpertDeliverySelection.SelectExactItem(20, [new(20, 100, 0), new(20, 200, 1)], out var selected);
        Assert.False(result.Success);
        Assert.Equal("ExpertDeliveryItemAmbiguous", result.Code);
        Assert.Null(selected);
    }
}
