namespace Franthropy.Dalamud.Automation.Trades;

public interface ITradeSuppression
{
    void SuppressTradeAutoConfirm();
    void RetainTradeSuppressionOnDispose();
    void RestoreTradeAutoConfirm();
}
