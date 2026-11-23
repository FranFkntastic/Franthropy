namespace Franthropy.Dalamud.Automation.Trades;

public interface ITradeSuppression
{
    void SuppressTradeAutoConfirm();
    void RetainTradeSuppressionOnDispose();
    void RestoreTradeAutoConfirm();

    /// <summary>
    /// Heartbeat from <see cref="TradeRunner.Tick"/> on every tick while the runner holds trade suppression.
    /// Implementations backed by a heartbeat-leased hold renew it here; the default does nothing.
    /// </summary>
    void RenewTradeSuppression() { }
}
