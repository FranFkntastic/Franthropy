namespace Franthropy.Dalamud.Automation.Trades;

/// <summary>Caller-owned stable mutable objects for this sender. Save must durably persist both together
/// atomically (including Recovery.AwaitingVerification); a failed save must leave the previous durable snapshot intact.
/// Do not edit intent while a batch is active or awaiting explicit settlement.</summary>
public interface ITradeIntentStore
{
    IList<TradeItem> Remaining { get; }
    TradeRecoveryState Recovery { get; }
    void Save();
}
