using System;
using System.Collections.Generic;
using System.Linq;

namespace Franthropy.Dalamud.Automation.Trades;

public static class TradeInventoryReconciler
{
    public static bool Reconcile(
        IList<TradeItem> queue,
        IReadOnlyList<TradeInventoryStack> inventory)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(inventory);

        var observed = inventory
            .Where(stack => stack.ItemId > 0 && stack.Quantity > 0)
            .GroupBy(stack => (stack.ItemId, stack.IsHighQuality))
            .ToDictionary(
                group => group.Key,
                group => new ObservedItem(
                    group.First().ItemName,
                    Math.Min(int.MaxValue, group.Sum(stack => (long)stack.Quantity))));
        var reconciled = queue
            .Where(item => item.ItemId > 0 && item.Quantity > 0)
            .GroupBy(item => (item.ItemId, item.IsHighQuality))
            .Select(group =>
            {
                if (!observed.TryGetValue(group.Key, out var current))
                    return null;
                var requested = Math.Min(int.MaxValue, group.Sum(item => (long)item.Quantity));
                var quantity = checked((int)Math.Min(requested, current.Quantity));
                return quantity <= 0
                    ? null
                    : new TradeItem
                    {
                        ItemId = group.Key.ItemId,
                        IsHighQuality = group.Key.IsHighQuality,
                        ItemName = string.IsNullOrWhiteSpace(current.Name)
                            ? group.First().ItemName
                            : current.Name,
                        Quantity = quantity,
                    };
            })
            .Where(item => item is not null)
            .Cast<TradeItem>()
            .ToList();

        if (queue.Count == reconciled.Count &&
            queue.Zip(reconciled).All(pair =>
                pair.First.ItemId == pair.Second.ItemId &&
                pair.First.IsHighQuality == pair.Second.IsHighQuality &&
                pair.First.Quantity == pair.Second.Quantity &&
                string.Equals(pair.First.ItemName, pair.Second.ItemName, StringComparison.Ordinal)))
        {
            return false;
        }

        queue.Clear();
        foreach (var item in reconciled)
            queue.Add(item);
        return true;
    }

    private sealed record ObservedItem(string Name, long Quantity);
}
