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
    public void ResolveNext_PrefersOneCoveringStackOverMultipleSmallerConversions()
    {
        var result = ItemQualityLoweringPlanner.ResolveNext(
            [new(100, "Cobalt Joint Plate", 600)],
            [
                Stack(0, 100, 480, highQuality: false),
                Stack(1, 100, 100, highQuality: true),
                Stack(2, 100, 999, highQuality: true),
            ]);

        Assert.True(result.Success);
        Assert.Equal(999, result.Stack!.Quantity);
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

    [Fact]
    public void Preview_ReportsWholeStackConversionAndExcessUnits()
    {
        var preview = ItemQualityLoweringPlanner.Preview(
            [new(100, "Cobalt Joint Plate", 600)],
            [
                Stack(0, 100, 480, highQuality: false),
                Stack(1, 100, 100, highQuality: true),
                Stack(2, 100, 999, highQuality: true),
            ]);

        Assert.True(preview.Success);
        var step = Assert.Single(preview.Steps);
        Assert.Equal(2, step.SlotIndex);
        Assert.Equal(999, step.ConvertedUnits);
        Assert.Equal(120, step.RequiredUnits);
        Assert.Equal(879, step.ExcessConvertedUnits);
        Assert.Equal(879, preview.ExcessConvertedUnits);
    }

    [Fact]
    public void Preview_ListsEveryStackInExecutionOrderAcrossMergedRequirements()
    {
        var preview = ItemQualityLoweringPlanner.Preview(
            [
                new(100, "Cobalt Joint Plate", 200),
                new(100, "Cobalt Joint Plate", 100),
                new(200, "Darksteel Nugget", 5),
            ],
            [
                Stack(0, 100, 50, highQuality: false),
                Stack(1, 100, 150, highQuality: true),
                Stack(2, 100, 120, highQuality: true),
                Stack(3, 200, 5, highQuality: false),
            ]);

        Assert.True(preview.Success);
        Assert.Collection(
            preview.Steps,
            first =>
            {
                Assert.Equal(1, first.SlotIndex);
                Assert.Equal(150, first.ConvertedUnits);
                Assert.Equal(150, first.RequiredUnits);
            },
            second =>
            {
                Assert.Equal(2, second.SlotIndex);
                Assert.Equal(120, second.ConvertedUnits);
                Assert.Equal(100, second.RequiredUnits);
                Assert.Equal(20, second.ExcessConvertedUnits);
            });
        Assert.Equal(270, preview.ConvertedUnits);
    }

    [Fact]
    public void Preview_IsEmptyWhenNormalQualityAlreadyCoversRequirements()
    {
        var preview = ItemQualityLoweringPlanner.Preview(
            [new(100, "Cobalt Joint Plate", 600)],
            [
                Stack(0, 100, 600, highQuality: false),
                Stack(1, 100, 120, highQuality: true),
            ]);

        Assert.True(preview.Success);
        Assert.False(preview.RequiresLowering);
    }

    [Fact]
    public void Preview_FailsWithPlannerMessageWhenStockIsShort()
    {
        var preview = ItemQualityLoweringPlanner.Preview(
            [new(100, "Cobalt Joint Plate", 600)],
            [Stack(0, 100, 100, highQuality: true)]);

        Assert.False(preview.Success);
        Assert.Contains("100 combined", preview.Message);
    }

    private static DalamudInventoryStack Stack(
        int slot,
        uint itemId,
        int quantity,
        bool highQuality) =>
        new(default, slot, itemId, quantity, highQuality);
}
