using System;
using System.Collections.Generic;
using System.Linq;

namespace Franthropy.Dalamud.Automation.Trades;

public static class TradePlanner
{
    public const uint GilItemId = 1;
    public const int MaximumTradeSlots = 5;
    public const int MaximumGilPerTrade = 1_000_000;

    public static TradeValidationResult Validate(
        IReadOnlyList<TradeItem> queue,
        IReadOnlyList<TradeInventoryStack> inventory)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(inventory);

        if (queue.Count == 0)
            return new(false, TradeValidationCode.Empty, "Trade is empty.");

        var requested = new Dictionary<TradeItemKey, int>();
        foreach (var item in queue)
        {
            if (item.ItemId == 0 || item.Quantity <= 0)
                return new(false, TradeValidationCode.InvalidQuantity, $"{Display(item)} has an invalid quantity.");

            var key = new TradeItemKey(item.ItemId, item.IsHighQuality);
            requested[key] = checked(requested.GetValueOrDefault(key) + item.Quantity);
        }

        var available = CountCombinedInventory(inventory);
        foreach (var pair in requested)
        {
            var held = available.GetValueOrDefault(pair.Key);
            if (held < pair.Value)
            {
                var item = queue.First(value => value.ItemId == pair.Key.ItemId);
                return new(
                    false,
                    TradeValidationCode.InsufficientInventory,
                    $"{Display(item)} needs {pair.Value:N0}, but only {held:N0} tradeable units are available.");
            }
        }

        return new(true, TradeValidationCode.Ready, "Trade is ready.");
    }

    public static TradeBatch BuildNextBatch(
        IReadOnlyList<TradeItem> queue,
        IReadOnlyList<TradeInventoryStack> inventory,
        int maximumSlots = MaximumTradeSlots,
        bool allowHighQualityItems = true)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(inventory);
        if (maximumSlots is <= 0 or > MaximumTradeSlots)
            throw new ArgumentOutOfRangeException(nameof(maximumSlots));

        var validation = Validate(queue, inventory);
        if (!validation.Success)
            throw new InvalidOperationException(validation.Message);

        var remainingByKey = queue
            .GroupBy(item => new TradeItemKey(item.ItemId, item.IsHighQuality))
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));
        var orderedKeys = queue
            .Select(item => new TradeItemKey(item.ItemId, item.IsHighQuality))
            .Distinct()
            .ToArray();
        var lines = new List<TradeBatchLine>(maximumSlots);
        var gilKey = new TradeItemKey(GilItemId);
        var gilAmount = remainingByKey.TryGetValue(gilKey, out var requestedGil)
            ? Math.Min(requestedGil, MaximumGilPerTrade)
            : 0;

        foreach (var key in orderedKeys.Where(key => key != gilKey))
        {
            var remaining = remainingByKey[key];
            var unused = inventory
                .Where(value =>
                    value.ItemId == key.ItemId && value.IsHighQuality == key.IsHighQuality &&
                    (allowHighQualityItems || !value.IsHighQuality) && value.Quantity > 0)
                .ToList();
            while (remaining > 0 && lines.Count < maximumSlots &&
                   SelectSourceStack(unused, remaining) is { } stack)
            {
                unused.Remove(stack);
                var quantity = Math.Min(remaining, stack.Quantity);
                lines.Add(new(
                    stack.ContainerId,
                    stack.SlotIndex,
                    stack.ItemId,
                    stack.ItemName,
                    stack.IsHighQuality,
                    quantity,
                    stack.Quantity));
                remaining -= quantity;
            }

            if (lines.Count == maximumSlots)
                break;

            if (remaining > 0)
            {
                throw new InvalidOperationException(
                    $"{queue.First(item => item.ItemId == key.ItemId).ItemName} still needs " +
                    (allowHighQualityItems
                        ? $"{remaining:N0} units after assigning all eligible inventory stacks."
                        : $"{remaining:N0} normal-quality units after quality normalization."));
            }
        }

        if (lines.Count == 0 && gilAmount == 0)
            throw new InvalidOperationException("No tradeable inventory stacks could be assigned to the next batch.");

        return new(lines, gilAmount, CountExactInventory(inventory));
    }

    // Picks the next source stack for `remaining` units so trades split as few stacks
    // and use as few slots as possible: an exact match, else the largest stack that is
    // consumed whole, else (a split is unavoidable) the smallest stack that covers it.
    // Container/slot order breaks ties so the choice stays deterministic.
    internal static TradeInventoryStack? SelectSourceStack(
        IEnumerable<TradeInventoryStack> candidates,
        int remaining)
    {
        if (remaining <= 0)
            return null;
        var eligible = candidates.Where(stack => stack.Quantity > 0).ToList();
        return eligible
                   .Where(stack => stack.Quantity == remaining)
                   .OrderBy(stack => stack.ContainerId)
                   .ThenBy(stack => stack.SlotIndex)
                   .FirstOrDefault()
               ?? eligible
                   .Where(stack => stack.Quantity < remaining)
                   .OrderByDescending(stack => stack.Quantity)
                   .ThenBy(stack => stack.ContainerId)
                   .ThenBy(stack => stack.SlotIndex)
                   .FirstOrDefault()
               ?? eligible
                   .OrderBy(stack => stack.Quantity)
                   .ThenBy(stack => stack.ContainerId)
                   .ThenBy(stack => stack.SlotIndex)
                   .FirstOrDefault();
    }

    public static bool HasExpectedInventoryDelta(
        TradeBatch batch,
        IReadOnlyList<TradeInventoryStack> inventoryAfter,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(inventoryAfter);
        var after = CountExactInventory(inventoryAfter);
        var expectedDelta = batch.Lines
            .GroupBy(line => new TradeInventoryKey(line.ItemId, line.IsHighQuality))
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));
        if (batch.GilAmount > 0)
            expectedDelta[new(GilItemId, false)] = batch.GilAmount;

        foreach (var key in after.Keys.Concat(batch.ExpectedInventoryBefore.Keys).Distinct())
            expectedDelta.TryAdd(key, 0);
        foreach (var pair in expectedDelta)
        {
            var before = batch.ExpectedInventoryBefore.GetValueOrDefault(pair.Key);
            var current = after.GetValueOrDefault(pair.Key);
            var observed = before - current;
            if (observed != pair.Value)
            {
                diagnostic =
                    $"Expected item {pair.Key.ItemId} {(pair.Key.IsHighQuality ? "HQ" : "NQ")} to decrease by {pair.Value:N0}, but observed {observed:N0} ({before:N0}->{current:N0}).";
                return false;
            }
        }

        var parts = new List<string>();
        if (batch.ItemUnitCount > 0)
            parts.Add($"{batch.ItemUnitCount:N0} item units across {batch.SlotCount:N0} slots");
        if (batch.GilAmount > 0)
            parts.Add($"{batch.GilAmount:N0} gil");
        diagnostic = $"Verified {string.Join(" and ", parts)}.";
        return true;
    }

    public static void ApplyCompletedBatch(IList<TradeItem> queue, TradeBatch batch)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(batch);

        var completed = batch.Lines
            .GroupBy(line => new TradeItemKey(line.ItemId, line.IsHighQuality))
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));
        if (batch.GilAmount > 0)
            completed[new(GilItemId)] = batch.GilAmount;

        foreach (var grouped in completed)
        {
            var remaining = grouped.Value;
            for (var index = 0; index < queue.Count && remaining > 0; index++)
            {
                var item = queue[index];
                if (item.ItemId != grouped.Key.ItemId || item.IsHighQuality != grouped.Key.IsHighQuality)
                    continue;

                var consumed = Math.Min(item.Quantity, remaining);
                item.Quantity -= consumed;
                remaining -= consumed;
                if (item.Quantity <= 0)
                {
                    queue.RemoveAt(index);
                    index--;
                }
            }

            if (remaining != 0)
                throw new InvalidOperationException($"Completed batch exceeded queued quantity for item {grouped.Key.ItemId}.");
        }
    }

    public static IReadOnlyDictionary<TradeInventoryKey, int> CountExactInventory(
        IReadOnlyList<TradeInventoryStack> inventory) =>
        inventory
            .GroupBy(stack => new TradeInventoryKey(stack.ItemId, stack.IsHighQuality))
            .ToDictionary(group => group.Key, group => group.Sum(stack => stack.Quantity));

    public static IReadOnlyDictionary<TradeItemKey, int> CountCombinedInventory(
        IReadOnlyList<TradeInventoryStack> inventory) =>
        inventory
            .GroupBy(stack => new TradeItemKey(stack.ItemId, stack.IsHighQuality))
            .ToDictionary(group => group.Key, group => group.Sum(stack => stack.Quantity));

    private static string Display(TradeItem item) =>
        item.ItemId == GilItemId ? "Gil" : item.ItemName;
}
