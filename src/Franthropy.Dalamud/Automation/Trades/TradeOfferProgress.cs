using System;
using System.Collections.Generic;
using System.Linq;

namespace Franthropy.Dalamud.Automation.Trades;

// Intent is item/quality/quantity; source slots are disposable execution plans.
internal static class TradeOfferProgress
{
    public static TradeBatchLine? NextLine(TradeBatch batch, TradeOfferObservation offer,
        IReadOnlyList<TradeInventoryStack> inventory)
    {
        if (offer.Items.Count >= TradePlanner.MaximumTradeSlots)
            return null;
        foreach (var desired in batch.Lines.GroupBy(line => new TradeInventoryKey(line.ItemId, line.IsHighQuality)))
        {
            var offered = offer.Items.Where(item => item.ItemId == desired.Key.ItemId &&
                                                  item.IsHighQuality == desired.Key.IsHighQuality).Sum(item => item.Quantity);
            var remaining = desired.Sum(line => line.Quantity) - offered;
            if (remaining <= 0)
                continue;
            // Without source identity we cannot know whether another native
            // offer would reuse an occupied stack. Let the runner rebuild it.
            if (offer.Items.Any(item => item.ItemId == desired.Key.ItemId &&
                item.IsHighQuality == desired.Key.IsHighQuality &&
                (item.SourceContainerId == null || item.SourceSlotIndex == null)))
                return null;
            var source = inventory.FirstOrDefault(stack => stack.ItemId == desired.Key.ItemId &&
                stack.IsHighQuality == desired.Key.IsHighQuality && stack.Quantity > 0 &&
                !offer.Items.Any(item => item.SourceContainerId == stack.ContainerId && item.SourceSlotIndex == stack.SlotIndex));
            if (source != null)
                return new(source.ContainerId, source.SlotIndex, source.ItemId, source.ItemName,
                    source.IsHighQuality, Math.Min(remaining, source.Quantity), source.Quantity);
        }
        return null;
    }
}
