namespace Franthropy.Dalamud.Automation.Trades;

/// <summary>Caller-owned stable mutable objects for this sender. Save must durably persist both together
/// or throw. Do not edit intent while a batch is in flight.</summary>
public interface ITradeIntentStore
{
    IList<TradeItem> Remaining { get; }
    TradeRecoveryState Recovery { get; }
    void Save();
}
