namespace Franthropy.Dalamud.Automation.Inventory;

public sealed record DalamudUiTransactionResult(bool Success, string Code, string Message)
{
    public static DalamudUiTransactionResult Pending(string message) => new(false, "Pending", message);
    public static DalamudUiTransactionResult Fail(string code, string message) => new(false, code, message);
    public static DalamudUiTransactionResult Completed(string code, string message) => new(true, code, message);
}
