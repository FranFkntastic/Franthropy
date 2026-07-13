namespace Franthropy.Dalamud.Automation.Coordination;

public interface IInterruptibleAutomationAdapter
{
    string DisplayName { get; }
    bool IsAvailable { get; }
    bool TryGetRunning(out bool isRunning, out string? error);
    bool TryInterrupt(out string? error);
}

public sealed record ExternalAutomationInterruptResult(
    bool Success,
    bool WasRunning,
    bool Interrupted,
    string Code,
    string Message);

public sealed class ExternalAutomationInterlock
{
    private readonly IInterruptibleAutomationAdapter adapter;

    public ExternalAutomationInterlock(IInterruptibleAutomationAdapter adapter)
    {
        this.adapter = adapter;
    }

    public ExternalAutomationInterruptResult InterruptIfRunning()
    {
        if (!adapter.IsAvailable)
            return new(true, false, false, "Unavailable", $"{adapter.DisplayName} is not loaded; no interruption was required.");

        if (!adapter.TryGetRunning(out var isRunning, out var queryError))
            return new(false, false, false, "StateUnavailable", queryError ?? $"{adapter.DisplayName} did not expose its running state.");

        if (!isRunning)
            return new(true, false, false, "AlreadyIdle", $"{adapter.DisplayName} is idle; no interruption was required.");

        if (!adapter.TryInterrupt(out var interruptError))
            return new(false, true, false, "InterruptFailed", interruptError ?? $"{adapter.DisplayName} rejected the interruption request.");

        if (!adapter.TryGetRunning(out var stillRunning, out var verificationError))
            return new(false, true, false, "InterruptVerificationUnavailable", verificationError ?? $"{adapter.DisplayName} interruption could not be verified.");

        return stillRunning
            ? new(false, true, false, "InterruptNotObserved", $"{adapter.DisplayName} still reports that it is running after the interruption request.")
            : new(true, true, true, "Interrupted", $"{adapter.DisplayName} was running and has been stopped before this automation began.");
    }
}
