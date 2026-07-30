using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Franthropy.Dalamud.Travel;

public enum TravelReadinessState
{
    Ready,
    Repairing,
    Waiting,
    Blocked,
}

public sealed record TravelReadinessResult(
    TravelReadinessState State,
    string Code,
    string Message)
{
    public bool IsReady => State == TravelReadinessState.Ready;
}

/// <summary>
/// Repairs caller-owned addon surfaces, waits through known transient travel blockers, and only
/// reports a terminal block when the remaining game state cannot be safely repaired generically.
/// Call once per framework tick; readiness is emitted only after consecutive stable observations.
/// </summary>
public sealed class DalamudTravelReadiness
{
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly IObjectTable objects;
    private readonly HashSet<string> ownedAddonNames;
    private readonly int stableObservationsRequired;
    private int stableObservations;

    public DalamudTravelReadiness(
        ICondition condition,
        IGameGui gameGui,
        IObjectTable objects,
        IEnumerable<string>? ownedAddonNames = null,
        int stableObservationsRequired = 3)
    {
        this.condition = condition;
        this.gameGui = gameGui;
        this.objects = objects;
        this.ownedAddonNames = new(
            ownedAddonNames ?? [],
            StringComparer.Ordinal);
        this.stableObservationsRequired = Math.Max(1, stableObservationsRequired);
    }

    public TravelReadinessResult Advance()
    {
        var closed = CloseOwnedAddons();
        if (closed.Count > 0)
        {
            stableObservations = 0;
            return Result(
                TravelReadinessState.Repairing,
                "OwnedUiClosing",
                $"Closing owned automation surface(s): {string.Join(", ", closed)}.");
        }

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            return Wait("AreaTransition", "Waiting for the area transition to finish.");
        if (condition[ConditionFlag.Casting])
            return Wait("Casting", "Waiting for the current cast to finish.");
        if (condition[ConditionFlag.OccupiedSummoningBell])
            return Wait("RetainerSessionReleasing", "Waiting for the summoning-bell session to release.");
        if (condition[ConditionFlag.OccupiedInEvent])
            return Wait("EventReleasing", "Waiting for the current game event to release.");
        if (objects.LocalPlayer is null)
            return Wait("PlayerUnavailable", "Waiting for the local player to become available.");

        if (condition[ConditionFlag.InCombat])
            return Block("InCombat", "Travel cannot start while the character is in combat.");
        if (condition[ConditionFlag.Unconscious])
            return Block("Unconscious", "Travel cannot start while the character is unconscious.");
        if (condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            condition[ConditionFlag.WatchingCutscene] ||
            condition[ConditionFlag.WatchingCutscene78])
            return Block("CutsceneOwnsUi", "A cutscene owns the game UI.");
        if (condition[ConditionFlag.Crafting] ||
            condition[ConditionFlag.PreparingToCraft] ||
            condition[ConditionFlag.ExecutingCraftingAction])
            return Block("Crafting", "Travel cannot start while the character is crafting.");
        if (condition[ConditionFlag.OccupiedInQuestEvent])
            return Block("UnknownUiOwner", "A quest or NPC interaction still owns the game UI after owned surfaces were released.");

        stableObservations++;
        return stableObservations >= stableObservationsRequired
            ? Result(TravelReadinessState.Ready, "Ready", "Travel readiness is stable.")
            : Result(TravelReadinessState.Waiting, "Stabilizing", "Waiting for stable travel readiness.");
    }

    public void Reset() => stableObservations = 0;

    private unsafe IReadOnlyList<string> CloseOwnedAddons()
    {
        var closed = new List<string>();
        foreach (var addonName in ownedAddonNames)
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
            if (addon is null || !addon->IsReady || !addon->IsVisible)
                continue;
            addon->Close(true);
            closed.Add(addonName);
        }
        return closed;
    }

    private TravelReadinessResult Wait(string code, string message)
    {
        stableObservations = 0;
        return Result(TravelReadinessState.Waiting, code, message);
    }

    private TravelReadinessResult Block(string code, string message)
    {
        stableObservations = 0;
        return Result(TravelReadinessState.Blocked, code, message);
    }

    private static TravelReadinessResult Result(
        TravelReadinessState state,
        string code,
        string message) =>
        new(state, code, message);
}
