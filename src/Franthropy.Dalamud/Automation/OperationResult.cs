namespace Franthropy.Dalamud.Automation;

public enum OperationStatus { Pending, Succeeded, Failed, Cancelled, Indeterminate }
public sealed record OperationResult(OperationStatus Status, string Code, string Message)
{
    public bool IsTerminal => Status != OperationStatus.Pending;
}
