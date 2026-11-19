using System.Collections.Generic;
using System.Linq;

namespace Franthropy.Dalamud.Automation.Trades;

public sealed record TradeOfferedItem(uint ItemId, bool IsHighQuality, int Quantity,
    uint? SourceContainerId = null, int? SourceSlotIndex = null);

// A readback of this trade, not a receipt and not an inference from dispatched callbacks.
public sealed record TradeOfferObservation(
    uint PartnerEntityId, int Gil, bool SyncPending, IReadOnlyList<TradeOfferedItem> Items)
{
    public string? Mismatch(TradePartner partner, TradeBatch batch, bool allowPartial = false)
    {
        if (PartnerEntityId != (uint)partner.GameObjectId)
            return "The live trade recipient changed; the planned offer was not confirmed.";
        if (Gil < 0 || Gil > batch.GilAmount || (!allowPartial && Gil != batch.GilAmount))
            return $"The live offer contains {Gil:N0} gil; expected {batch.GilAmount:N0}.";
        var expected = batch.Lines.GroupBy(x => new TradeInventoryKey(x.ItemId, x.IsHighQuality))
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
        var actual = Items.GroupBy(x => new TradeInventoryKey(x.ItemId, x.IsHighQuality))
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
        return actual.Any(x => x.Value <= 0 || x.Value > expected.GetValueOrDefault(x.Key)) ||
               (!allowPartial && expected.Any(x => actual.GetValueOrDefault(x.Key) != x.Value))
            ? "The live outgoing items or quantities differ from the planned batch." : null;
    }
}
