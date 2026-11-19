using Franthropy.Dalamud.Automation;

namespace Franthropy.Dalamud.Travel;

/// <summary>Framework-thread operation. Resume a journaled attempt in observation-only mode.</summary>
public sealed class CharacterSwitchOperation(DalamudLifestreamLogin login, LifestreamLoginRequest target,
    DateTimeOffset deadline, Action<string> warning, bool alreadySubmitted = false)
{
    private bool submitted = alreadySubmitted;
    private OperationResult result = new(OperationStatus.Pending, "AwaitingEligibility", "Checking switch eligibility.");
    public OperationResult Tick(CharacterSwitchGameState observed, DateTimeOffset now)
    {
        if (result.IsTerminal)
        {
            if (!LateIdentityObserved && result.Status == OperationStatus.Indeterminate &&
                observed.LoggedIn && observed.Name.Equals(target.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                observed.HomeWorld.Equals(target.HomeWorld, StringComparison.OrdinalIgnoreCase))
            {
                LateIdentityObserved = true;
                warning($"Late identity {observed.Name}@{observed.HomeWorld} observed at {now:O} after {result.Code}; no replay or credit.");
            }
            return result;
        }
        var next = Advance(observed, now);
        if (next.Status is OperationStatus.Failed or OperationStatus.Indeterminate)
            warning($"{target.CharacterName}@{target.HomeWorld}: {next.Code}: {next.Message}");
        return next;
    }

    public bool LateIdentityObserved { get; private set; }

    private OperationResult Advance(CharacterSwitchGameState observed, DateTimeOffset now)
    {
        if (result.IsTerminal) return result;
        if (now >= deadline) return result = new(submitted ? OperationStatus.Indeterminate : OperationStatus.Failed,
            "CharacterSwitchDeadline", $"Character switch deadline {deadline:O} expired. {result.Message} Observed {observed.Name}@{observed.HomeWorld}.");
        if (!LifestreamLoginRequest.TryCreate(target.CharacterName, target.HomeWorld, out var request, out var error))
            return result = new(OperationStatus.Failed, "InvalidIdentity", error);
        if (observed.LoggedIn && string.Equals(observed.Name, request!.CharacterName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(observed.HomeWorld, request.HomeWorld, StringComparison.OrdinalIgnoreCase))
            return result = new(OperationStatus.Succeeded, "IdentityObserved", $"Observed {observed.Name}@{observed.HomeWorld}.");
        if (submitted) return result = new(OperationStatus.Pending, "AwaitingIdentity", "Waiting for the exact logged-in identity.");
        if (CharacterSwitchPolicy.GetRefusal(observed) is { } reason)
            return result = new(OperationStatus.Pending, "SwitchBlocked", reason);
        submitted = true;
        var response = login.TryChangeCharacter(request!);
        if (response.Code == "NotReady") submitted = false; // provider explicitly rejected, no work accepted
        return result = new(response.Success || response.Code == "NotReady" ? OperationStatus.Pending :
            response.Code == "IpcFailure" ? OperationStatus.Indeterminate : OperationStatus.Failed, response.Code, response.Message);
    }

    public OperationResult Cancel() => result.IsTerminal ? result : result = new(
        submitted ? OperationStatus.Indeterminate : OperationStatus.Cancelled, "SwitchStopped", "Stopped observing the character switch.");
}
