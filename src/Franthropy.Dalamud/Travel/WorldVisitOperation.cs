using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Conditions;
using Franthropy.Dalamud.Automation;
using Franthropy.Dalamud.Worlds;

namespace Franthropy.Dalamud.Travel;

/// <summary>Use a catalog of public worlds. Never append an aetheryte or market-board route.</summary>
public sealed class WorldVisitOperation
{
    private readonly WorldInfo? target;
    private readonly ulong actor;
    private readonly Func<(ulong Actor, uint World, bool Valid, bool Zoning)> observe;
    private readonly Func<string, bool> dispatch;
    private readonly Action<string> warning;
    private readonly DateTimeOffset deadline;
    private readonly Action persistSubmitting;
    private bool submitted;
    private OperationResult result = new(OperationStatus.Pending, "AwaitingWorld", "Waiting for a current world.");

    public WorldVisitOperation(WorldCatalog worlds, string world, IPlayerState player, ICommandManager commands,
        IClientState client, ICondition conditions, DateTimeOffset deadline, ulong contentId, Action<string> warning, Action persistSubmitting, bool alreadySubmitted = false)
        : this(worlds, world, () => !client.IsLoggedIn || !player.IsLoaded ? default : (
            player.ContentId, player.CurrentWorld.RowId, player.CurrentWorld.IsValid,
            conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51]),
            command => commands.ProcessCommand(command), deadline, contentId, warning, persistSubmitting, alreadySubmitted) { }

    internal WorldVisitOperation(WorldCatalog worlds, string world, Func<(ulong Actor, uint World, bool Valid, bool Zoning)> observe,
        Func<string, bool> dispatch, DateTimeOffset deadline, ulong contentId, Action<string> warning, Action persistSubmitting, bool alreadySubmitted)
    {
        worlds.TryGetWorld(world, out target!);
        this.observe = observe;
        this.dispatch = dispatch;
        this.warning = warning;
        this.persistSubmitting = persistSubmitting;
        this.deadline = deadline;
        actor = contentId;
        submitted = alreadySubmitted;
    }

    public static WorldVisitOperation ReturnHome(WorldCatalog worlds, IPlayerState player, ICommandManager commands,
        IClientState client, ICondition conditions, DateTimeOffset deadline, Action<string> warning, Action persistSubmitting, bool alreadySubmitted = false) =>
        new(worlds, player.HomeWorld.ValueNullable?.Name.ToString() ?? "",
            player, commands, client, conditions, deadline, player.ContentId, warning, persistSubmitting, alreadySubmitted);

    public OperationResult Tick(DateTimeOffset now)
    {
        if (result.IsTerminal)
        {
            if (!LateArrivalObserved && result.Status == OperationStatus.Indeterminate &&
                observe() is { Zoning: false } late && late.Actor == actor && target != null && target.RowId == late.World)
            {
                LateArrivalObserved = true;
                warning($"Late arrival at {target.Name} observed at {now:O} after {result.Code}; no replay or credit.");
            }
            return result;
        }
        var next = Advance(now);
        if (next.Status is OperationStatus.Failed or OperationStatus.Indeterminate)
            warning($"{target?.Name}: {next.Code}: {next.Message}");
        return next;
    }

    public bool LateArrivalObserved { get; private set; }

    private OperationResult Advance(DateTimeOffset now)
    {
        if (result.IsTerminal) return result;
        if (target == null || actor == 0) return result = new(OperationStatus.Failed, "InvalidDestinationOrActor", "A catalog world and exact traveler are required.");
        var current = observe();
        if (now >= deadline) return result = new(submitted ? OperationStatus.Indeterminate : OperationStatus.Failed,
            "WorldVisitDeadline", $"World visit deadline {deadline:O} expired for {target.Name}; observed world {current.World}.");
        if (current.Actor == 0) return result;
        if (current.Actor != actor) return result = new(submitted ? OperationStatus.Indeterminate : OperationStatus.Failed,
            "TravelerChanged", "Another character is logged in.");
        if (current.Zoning) return result;
        if (current.World == target.RowId && target.RowId != 0)
            return result = new(OperationStatus.Succeeded, "WorldObserved", $"Observed {target.Name}.");
        if (submitted || !current.Valid) return result;
        try { persistSubmitting(); }
        catch (Exception ex) { return result = new(OperationStatus.Failed, "SubmissionNotPersisted", ex.Message); }
        submitted = true;
        try
        {
            return result = dispatch($"/li {target.Name}") ?
                new(OperationStatus.Pending, "AwaitingArrival", $"Waiting for {target.Name}.") :
                new(OperationStatus.Failed, "TravelCommandRejected", "Lifestream did not handle the world command.");
        }
        catch (Exception ex) { return result = new(OperationStatus.Indeterminate, "TravelDispatchUncertain", ex.Message); }
    }

    public OperationResult Cancel() => result.IsTerminal ? result : result = new(
        submitted ? OperationStatus.Indeterminate : OperationStatus.Cancelled, "VisitStopped", "Stopped observing world travel.");
}
