using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Automation.Inventory;
using Plan = Franthropy.Dalamud.Automation.Inventory.DalamudInventoryMover.NativeOperation;

namespace Franthropy.Dalamud.Tests.Automation.Inventory;

// The native merge swaps when either stack is exactly full, and a capacity-limited
// merge moves min(S, C-D). A wrong plan here silently swaps a bag item into the
// saddlebag or moves the wrong quantity; the post-hoc verifier only reports it.
public sealed class SaddlebagRetrievalPlanTests
{
    private const uint Item = 5057;
    private const int Capacity = 999;

    [Fact]
    public void PlansNeverMergeFullStacksAndOnlyMergeExactTransfers()
    {
        var fullSource = Stack(InventoryType.SaddleBag1, 0, Capacity);
        var partialBag = Stack(InventoryType.Inventory2, 3, 500);
        var empty = Stack(InventoryType.Inventory3, 1, 0);
        var plan = DalamudInventoryMover.PlanSaddlebagRetrieval([fullSource, partialBag, empty], fullSource, Capacity, Capacity);
        Assert.Equal((Plan.Move, empty), (plan.Operation, plan.Destination));
        Assert.Equal(InventoryMoveOutcome.NoBagCapacity,
            DalamudInventoryMover.PlanSaddlebagRetrieval([fullSource, partialBag], fullSource, Capacity, Capacity).Refusal);
        // A part of a full source is staged, then the incomplete piece merges.
        var stagingSlot = Stack(InventoryType.SaddleBag2, 2, 0);
        plan = DalamudInventoryMover.PlanSaddlebagRetrieval([fullSource, partialBag, stagingSlot], fullSource, 120, Capacity);
        Assert.Equal((InventoryMoveOutcome.None, Plan.Split), (plan.Refusal, plan.Operation));

        // Whole stack: best-fit compatible incomplete stack that takes all of it.
        var source = Stack(InventoryType.SaddleBag2, 4, 300);
        var roomy = Stack(InventoryType.Inventory1, 0, 100);
        var tight = Stack(InventoryType.Inventory4, 9, 699);
        plan = DalamudInventoryMover.PlanSaddlebagRetrieval([source, roomy, tight, empty], source, 300, Capacity);
        Assert.Equal((Plan.Merge, tight, 300), (plan.Operation, plan.Destination, plan.Amount));

        // Partial: merge only when the room equals the request, otherwise stage a saddlebag split.
        plan = DalamudInventoryMover.PlanSaddlebagRetrieval([source, roomy, tight, empty], source, 300 - 1, Capacity);
        Assert.Equal(InventoryMoveOutcome.NoSaddlebagSplitSpace, plan.Refusal);
        var saddleEmpty = Stack(InventoryType.SaddleBag1, 7, 0);
        plan = DalamudInventoryMover.PlanSaddlebagRetrieval([source, roomy, empty, saddleEmpty], source, 120, Capacity);
        Assert.Equal((InventoryMoveOutcome.None, Plan.Split, (DalamudInventoryStack?)null), (plan.Refusal, plan.Operation, plan.Destination));
        var exactRoom = Stack(InventoryType.Inventory2, 0, Capacity - 120);
        plan = DalamudInventoryMover.PlanSaddlebagRetrieval([source, roomy, exactRoom, saddleEmpty], source, 120, Capacity);
        Assert.Equal((Plan.Merge, exactRoom), (plan.Operation, plan.Destination));
    }

    private static DalamudInventoryStack Stack(InventoryType container, int slot, int quantity) =>
        new(container, slot, quantity == 0 ? 0 : Item, quantity);
}
