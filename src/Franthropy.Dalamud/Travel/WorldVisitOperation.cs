using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation;
using Franthropy.Dalamud.Worlds;

namespace Franthropy.Dalamud.Travel;

/// <summary>Use a catalog of public worlds. Never append an aetheryte or market-board route.</summary>
public sealed class WorldVisitOperation
{
    private readonly WorldInfo? target;
    private readonly ulong actor;
    private readonly IPlayerState player;
    private readonly IClientState client;
    private readonly Action<string> warning;
    private readonly ICommandManager commands;
    private readonly DateTimeOffset deadline;
    private bool submitted;
    private OperationResult result = new(OperationStatus.Pending, "AwaitingWorld", "Waiting for a current world.");

    public WorldVisitOperation(WorldCatalog worlds, string world, IPlayerState player, ICommandManager commands,
        IClientState client, DateTimeOffset deadline, ulong contentId, Action<string> warning, bool alreadySubmitted = false)
    {
        worlds.TryGetWorld(world, out target!);
        this.player = player;
        this.client = client;
        this.warning = warning;
        this.commands = commands;
        this.deadline = deadline;
        actor = contentId;
        submitted = alreadySubmitted;
    }

    public static WorldVisitOperation ReturnHome(WorldCatalog worlds, IPlayerState player, ICommandManager commands,
        IClientState client, DateTimeOffset deadline, Action<string> warning) =>
        new(worlds, player.HomeWorld.ValueNullable?.Name.ToString() ?? "",
            player, commands, client, deadline, player.ContentId, warning);

    public OperationResult Tick(DateTimeOffset now)
    {
        if (result.IsTerminal)
        {
            if (!LateArrivalObserved && result.Status == OperationStatus.Indeterminate && client.IsLoggedIn &&
                player.IsLoaded && player.ContentId == actor && target != null && target.RowId == player.CurrentWorld.RowId)
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
        if (now >= deadline) return result = new(submitted ? OperationStatus.Indeterminate : OperationStatus.Failed,
            "WorldVisitDeadline", $"World visit deadline {deadline:O} expired for {target.Name}; observed world {player.CurrentWorld.RowId}.");
        if (!client.IsLoggedIn || !player.IsLoaded || player.ContentId == 0) return result;
        if (player.ContentId != actor) return result = new(submitted ? OperationStatus.Indeterminate : OperationStatus.Failed,
            "TravelerChanged", "Another character is logged in.");
        if (player.CurrentWorld.RowId == target.RowId && target.RowId != 0)
            return result = new(OperationStatus.Succeeded, "WorldObserved", $"Observed {target.Name}.");
        if (submitted || !player.CurrentWorld.IsValid) return result;
        submitted = true;
        try
        {
            return result = commands.ProcessCommand($"/li {target.Name}") ?
                new(OperationStatus.Pending, "AwaitingArrival", $"Waiting for {target.Name}.") :
                new(OperationStatus.Failed, "TravelCommandRejected", "Lifestream did not handle the world command.");
        }
        catch (Exception ex) { return result = new(OperationStatus.Indeterminate, "TravelDispatchUncertain", ex.Message); }
    }

    public OperationResult Cancel() => result.IsTerminal ? result : result = new(
        submitted ? OperationStatus.Indeterminate : OperationStatus.Cancelled, "VisitStopped", "Stopped observing world travel.");
}
