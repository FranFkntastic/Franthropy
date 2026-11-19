namespace Franthropy.Dalamud.Automation.Trades;

/// <summary>Exact carried counts from the two sides of a single observed inventory transition on this client.</summary>
public sealed record TradeDeltaEvidence(TradePartner Partner, DateTimeOffset ObservedAt,
    IReadOnlyDictionary<TradeInventoryKey, int> Before, IReadOnlyDictionary<TradeInventoryKey, int> After);
