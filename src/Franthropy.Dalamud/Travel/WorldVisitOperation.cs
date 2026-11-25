using Dalamud.Plugin;
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
    private readonly Action? clearSubmitting;
    private readonly Func<string?> blocker;
    private readonly Func<bool?>? lifestreamBusy;
    private bool submitted;
    private DateTimeOffset? dispatchedAt;
    private bool travelStarted;
    private bool busyProbeUnknownWarned;
    private string? lastRefusal;
    private OperationResult result = new(OperationStatus.Pending, "AwaitingWorld", "Waiting for a current world.");

    /// <summary>How long after the world command Lifestream must be seen busy, or the character zoning, before the
    /// command is treated as not taken and sent again. Applies only when Lifestream's busy state is sampled, and is
    /// abandoned for a dispatch as soon as any sample is unknown (IPC failure): an unreadable probe never counts as
    /// evidence the command was not taken.</summary>
    public static readonly TimeSpan TravelStartWindow = TimeSpan.FromSeconds(5);

    public const string LifestreamIsBusyChannel = "Lifestream.IsBusy";

    public WorldVisitOperation(WorldCatalog worlds, string world, IPlayerState player, ICommandManager commands,
        IClientState client, ICondition conditions, DateTimeOffset deadline, ulong contentId, Action<string> warning, Action persistSubmitting, bool alreadySubmitted = false)
        : this(worlds, world, Observer(player, client, conditions),
            command => commands.ProcessCommand(command), deadline, contentId, warning, persistSubmitting, alreadySubmitted,
            () => DalamudPlayerActionReadiness.GetBlocker(flag => conditions[flag]), null, null) { }

    /// <summary>
    /// Also waits for Lifestream to be idle before sending the world command, re-sends the command when
    /// Lifestream never starts on it, and clears the durable marker through <paramref name="clearSubmitting"/>
    /// whenever the command was demonstrably not taken. If <c>Lifestream.IsBusy</c> cannot be read, the readiness gate
    /// treats Lifestream as idle (as the overload without the plugin interface does, so it never waits forever) and,
    /// after dispatch, the command is trusted: the marker is kept, nothing is re-sent, and the deadline reports
    /// Indeterminate.
    /// </summary>
    public WorldVisitOperation(WorldCatalog worlds, string world, IPlayerState player, ICommandManager commands,
        IClientState client, ICondition conditions, IDalamudPluginInterface pluginInterface, DateTimeOffset deadline, ulong contentId,
        Action<string> warning, Action persistSubmitting, Action clearSubmitting, bool alreadySubmitted = false)
        : this(worlds, world, Observer(player, client, conditions),
            command => commands.ProcessCommand(command), deadline, contentId, warning, persistSubmitting, alreadySubmitted,
            ReadinessWithLifestream(conditions, pluginInterface), LifestreamBusyProbe(pluginInterface), clearSubmitting) { }

    internal WorldVisitOperation(WorldCatalog worlds, string world, Func<(ulong Actor, uint World, bool Valid, bool Zoning)> observe,
        Func<string, bool> dispatch, DateTimeOffset deadline, ulong contentId, Action<string> warning, Action persistSubmitting, bool alreadySubmitted)
        : this(worlds, world, observe, dispatch, deadline, contentId, warning, persistSubmitting, alreadySubmitted, () => null, null, null) { }

    internal WorldVisitOperation(WorldCatalog worlds, string world, Func<(ulong Actor, uint World, bool Valid, bool Zoning)> observe,
        Func<string, bool> dispatch, DateTimeOffset deadline, ulong contentId, Action<string> warning, Action persistSubmitting, bool alreadySubmitted,
        Func<string?> blocker, Func<bool?>? lifestreamBusy, Action? clearSubmitting)
    {
        worlds.TryGetWorld(world, out target!);
        this.observe = observe;
        this.dispatch = dispatch;
        this.warning = warning;
        this.persistSubmitting = persistSubmitting;
        this.clearSubmitting = clearSubmitting;
        this.blocker = blocker;
        this.lifestreamBusy = lifestreamBusy;
        this.deadline = deadline;
        actor = contentId;
        submitted = alreadySubmitted;
    }

    public static WorldVisitOperation ReturnHome(WorldCatalog worlds, IPlayerState player, ICommandManager commands,
        IClientState client, ICondition conditions, DateTimeOffset deadline, Action<string> warning, Action persistSubmitting, bool alreadySubmitted = false) =>
        new(worlds, player.HomeWorld.ValueNullable?.Name.ToString() ?? "",
            player, commands, client, conditions, deadline, player.ContentId, warning, persistSubmitting, alreadySubmitted);

    /// <inheritdoc cref="WorldVisitOperation(WorldCatalog, string, IPlayerState, ICommandManager, IClientState, ICondition, IDalamudPluginInterface, DateTimeOffset, ulong, Action{string}, Action, Action, bool)"/>
    public static WorldVisitOperation ReturnHome(WorldCatalog worlds, IPlayerState player, ICommandManager commands,
        IClientState client, ICondition conditions, IDalamudPluginInterface pluginInterface, DateTimeOffset deadline,
        Action<string> warning, Action persistSubmitting, Action clearSubmitting, bool alreadySubmitted = false) =>
        new(worlds, player.HomeWorld.ValueNullable?.Name.ToString() ?? "",
            player, commands, client, conditions, pluginInterface, deadline, player.ContentId, warning, persistSubmitting, clearSubmitting, alreadySubmitted);

    private static Func<(ulong Actor, uint World, bool Valid, bool Zoning)> Observer(IPlayerState player, IClientState client, ICondition conditions) =>
        () => !client.IsLoggedIn || !player.IsLoaded ? default : (
            player.ContentId, player.CurrentWorld.RowId, player.CurrentWorld.IsValid,
            conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51]);

    /// <summary>True busy, false idle, null when the IPC call failed (channel missing or a hiccup).</summary>
    private static Func<bool?> LifestreamBusyProbe(IDalamudPluginInterface pluginInterface)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        var subscriber = pluginInterface.GetIpcSubscriber<bool>(LifestreamIsBusyChannel);
        return () =>
        {
            try { return subscriber.InvokeFunc(); }
            catch { return null; }
        };
    }

    private static Func<string?> ReadinessWithLifestream(ICondition conditions, IDalamudPluginInterface pluginInterface)
    {
        var busy = LifestreamBusyProbe(pluginInterface);
        // Unknown reads as idle here so the gate never blocks forever; the world command itself then reports a
        // missing Lifestream, and post-dispatch start detection ignores unknown samples (see Advance).
        return () => busy() == true ? "Lifestream is still finishing another task." : DalamudPlayerActionReadiness.GetBlocker(flag => conditions[flag]);
    }

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
        if (now >= deadline)
        {
            // With Lifestream's busy state sampled, a command it never started on left nothing in flight.
            var neverStarted = submitted && dispatchedAt is not null && !travelStarted;
            return result = new(submitted && !neverStarted ? OperationStatus.Indeterminate : OperationStatus.Failed,
                "WorldVisitDeadline", $"World visit deadline {deadline:O} expired for {target.Name}; observed world {current.World}." +
                (neverStarted ? " Lifestream never started travel." : "") +
                (lastRefusal is null ? "" : $" Last refusal: {lastRefusal}"));
        }
        if (current.Actor == 0) return result;
        if (current.Actor != actor) return result = new(submitted ? OperationStatus.Indeterminate : OperationStatus.Failed,
            "TravelerChanged", "Another character is logged in.");
        if (current.Zoning)
        {
            if (submitted) travelStarted = true;
            return result;
        }
        if (current.World == target.RowId && target.RowId != 0)
            return result = new(OperationStatus.Succeeded, "WorldObserved", $"Observed {target.Name}.");
        if (submitted && dispatchedAt is { } sentAt && !travelStarted)
        {
            var busy = lifestreamBusy?.Invoke();
            if (busy == true)
                travelStarted = true;
            else if (busy is null)
            {
                // An unreadable probe is not evidence the command was dropped: trust it as the probe-less path does.
                dispatchedAt = null;
                if (!busyProbeUnknownWarned)
                {
                    busyProbeUnknownWarned = true;
                    warning($"{target.Name}: LifestreamBusyProbeFailed: {LifestreamIsBusyChannel} could not be read after the world command; " +
                        "trusting the command (no re-send, marker kept).");
                }
            }
            else if (now - sentAt >= TravelStartWindow)
            {
                // Lifestream neither went busy nor moved the character: the command was not taken. Send it again.
                submitted = false;
                dispatchedAt = null;
                lastRefusal = "Lifestream did not start travel after the world command.";
                warning($"{target.Name}: TravelNotStarted: {lastRefusal} Sending it again.");
                try { clearSubmitting?.Invoke(); }
                catch (Exception ex) { warning($"{target.Name}: SubmissionMarkerNotCleared: {ex.Message}"); }
            }
        }
        if (submitted || !current.Valid) return result;
        if (blocker() is { } reason)
        {
            lastRefusal = reason;
            return result = new(OperationStatus.Pending, "VisitBlocked", reason);
        }
        try { persistSubmitting(); }
        catch (Exception ex) { return result = new(OperationStatus.Failed, "SubmissionNotPersisted", ex.Message); }
        submitted = true;
        try
        {
            var handled = dispatch($"/li {target.Name}");
            // Start detection needs Lifestream's busy state; without it the command is trusted as before.
            if (handled && lifestreamBusy is not null)
            {
                dispatchedAt = now;
                travelStarted = false;
            }
            return result = handled ?
                new(OperationStatus.Pending, "AwaitingArrival", $"Waiting for {target.Name}.") :
                new(OperationStatus.Failed, "TravelCommandRejected", "Lifestream did not handle the world command.");
        }
        catch (Exception ex) { return result = new(OperationStatus.Indeterminate, "TravelDispatchUncertain", ex.Message); }
    }

    public OperationResult Cancel() => result.IsTerminal ? result : result = new(
        submitted ? OperationStatus.Indeterminate : OperationStatus.Cancelled, "VisitStopped", "Stopped observing world travel.");
}
