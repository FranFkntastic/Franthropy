using System;
using Dalamud.Game.ClientState.Conditions;

namespace Franthropy.Dalamud.Travel;

/// <summary>Game and caller state sampled at the moment a relog is requested.</summary>
public sealed record CharacterSwitchGameState
{
    public static CharacterSwitchGameState Observe(global::Dalamud.Plugin.Services.IClientState client,
        global::Dalamud.Plugin.Services.IObjectTable objects, global::Dalamud.Plugin.Services.ICondition conditions,
        bool retainerSessionOpen, string? automationBusyReason)
    {
        var p = objects.LocalPlayer;
        return new()
        {
            LoggedIn = client.IsLoggedIn && p != null,
            Name = p?.Name.TextValue ?? "",
            HomeWorld = p?.HomeWorld.ValueNullable?.Name.ToString() ?? "",
            HomeDataCenterId = p?.HomeWorld.ValueNullable?.DataCenter.RowId,
            CurrentDataCenterId = p?.CurrentWorld.ValueNullable?.DataCenter.RowId,
            IsConditionSet = flag => conditions[flag],
            RetainerSessionOpen = retainerSessionOpen,
            AutomationBusyReason = automationBusyReason
        };
    }

    public bool LoggedIn { get; init; }
    public string Name { get; init; } = string.Empty;
    public string HomeWorld { get; init; } = string.Empty;
    public uint? CurrentDataCenterId { get; init; }
    public uint? HomeDataCenterId { get; init; }
    public required Func<ConditionFlag, bool> IsConditionSet { get; init; }
    public bool RetainerSessionOpen { get; init; }

    /// <summary>Why the caller's automation cannot be interrupted, or null when it is idle.</summary>
    public string? AutomationBusyReason { get; init; }
}

/// <summary>Decides whether a user-requested relog may start. Pure so it is testable without a client.</summary>
public static class CharacterSwitchPolicy
{
    internal const string WrongDataCenterReason = "Return to your home data center to relog.";

    private static readonly (ConditionFlag[] Flags, string Reason)[] ConditionGuards =
    [
        ([ConditionFlag.TradeOpen], "A trade is open."),
        ([ConditionFlag.LoggingOut], "The character is already logging out."),
        ([ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51], "The character is between areas."),
        ([ConditionFlag.InCombat], "The character is in combat."),
        ([ConditionFlag.BoundByDuty, ConditionFlag.BoundByDuty56, ConditionFlag.BoundByDuty95], "The character is bound by a duty."),
        ([ConditionFlag.Crafting, ConditionFlag.PreparingToCraft, ConditionFlag.ExecutingCraftingAction], "The character is crafting."),
        ([ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78], "A cutscene is playing."),
        ([ConditionFlag.OccupiedSummoningBell], "A retainer session is open."),
        ([ConditionFlag.OccupiedInQuestEvent], "The character is in a quest event."),
        ([ConditionFlag.OccupiedInEvent], "The character is occupied in an event."),
    ];

    /// <summary>Returns why a relog must be refused right now, or null when it may start.</summary>
    public static string? GetRefusal(
        CharacterSwitchGameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.LoggedIn || string.IsNullOrWhiteSpace(state.Name) || string.IsNullOrWhiteSpace(state.HomeWorld))
            return "No logged-in character is available to relog.";
        if (!string.IsNullOrWhiteSpace(state.AutomationBusyReason))
            return state.AutomationBusyReason;

        foreach (var (flags, reason) in ConditionGuards)
        {
            foreach (var flag in flags)
            {
                if (state.IsConditionSet(flag))
                    return reason;
            }
        }

        if (state.RetainerSessionOpen)
            return "A retainer session is open.";

        // The relog returns the character to its home world; refusing off the home data center avoids an
        // unexpected data-center transfer. An unknown data center is refused too rather than guessed.
        if (state.CurrentDataCenterId is not { } current || state.HomeDataCenterId is not { } home)
            return "The current or home data center is unknown.";

        if (current == 0 || home == 0) return "The current or home data center is unknown.";
        return current == home ? null : WrongDataCenterReason;
    }
}
