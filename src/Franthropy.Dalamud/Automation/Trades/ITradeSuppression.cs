namespace Franthropy.Dalamud.Automation.Trades;

public interface ITradeSuppression
{
    /// <summary>Pauses trade auto-confirm. Must be idempotent; the runner may call it again at load.</summary>
    void SuppressTradeAutoConfirm();
    void RetainTradeSuppressionOnDispose();

    /// <summary>
    /// Resumes trade auto-confirm. Must remove this plugin's owner entry from the shared stop-request sets
    /// unconditionally, never gated on an in-memory "I suppressed" flag: <see cref="TradeRunner"/> calls it at load,
    /// when no trade window is open, to clear a pause a previous instance retained across unload. Must be idempotent.
    /// </summary>
    void RestoreTradeAutoConfirm();

    /// <summary>
    /// Heartbeat from <see cref="TradeRunner.Tick"/> on every tick while the runner holds trade suppression.
    /// Implementations backed by a heartbeat-leased hold renew it here; the default does nothing.
    /// </summary>
    void RenewTradeSuppression() { }
}
