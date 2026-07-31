using Franthropy.Dalamud.Automation.Inventory;

namespace Franthropy.Dalamud.Tests.Automation.Inventory;

public sealed class ItemQualityLoweringPlannerTests
{
    [Fact]
    public void ResolveNext_CountsCombinedStockAndSelectsSmallestRequiredHqStack()
    {
        var result = ItemQualityLoweringPlanner.ResolveNext(
            [new(100, "Cobalt Joint Plate", 600)],
            [
                Stack(0, 100, 480, highQuality: false),
                Stack(1, 100, 999, highQuality: true),
                Stack(2, 100, 120, highQuality: true),
            ]);

        Assert.True(result.Success);
        Assert.False(result.Completed);
        Assert.Equal(120, result.RemainingHighQualityUnits);
        Assert.Equal(2, result.Stack!.SlotIndex);
        Assert.Equal(120, result.Stack.Quantity);
    }

    [Fact]
    public void ResolveNext_CompletesWhenNqAlreadyCoversRequirement()
    {
        var result = ItemQualityLoweringPlanner.ResolveNext(
            [new(100, "Cobalt Joint Plate", 600)],
            [
                Stack(0, 100, 600, highQuality: false),
                Stack(1, 100, 120, highQuality: true),
            ]);

        Assert.True(result.Success);
        Assert.True(result.Completed);
        Assert.Null(result.Stack);
    }

    [Fact]
    public void ResolveNext_FailsWhenCombinedStockCannotCoverRequirement()
    {
        var result = ItemQualityLoweringPlanner.ResolveNext(
            [new(100, "Cobalt Joint Plate", 600)],
            [
                Stack(0, 100, 480, highQuality: false),
                Stack(1, 100, 100, highQuality: true),
            ]);

        Assert.False(result.Success);
        Assert.Contains("580 combined", result.Message);
    }

    private static DalamudInventoryStack Stack(
        int slot,
        uint itemId,
        int quantity,
        bool highQuality) =>
        new(default, slot, itemId, quantity, highQuality);
}
