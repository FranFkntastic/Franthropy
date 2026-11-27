using Dalamud.Game.ClientState.Conditions;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Franthropy.Dalamud.Travel;

/// <summary>
/// Point-in-time check that the local player can accept a new travel or interaction command.
/// Every reason it reports is transient: the caller should wait and check again within its own
/// deadline instead of submitting a request the game or a dependency would refuse.
/// Call from the framework thread.
/// </summary>
public static unsafe class DalamudPlayerActionReadiness
{
    private static readonly (ConditionFlag[] Flags, string Reason)[] TransientGuards =
    [
        ([ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51], "The character is between areas."),
        ([ConditionFlag.Casting], "The character is casting."),
        ([ConditionFlag.Jumping, ConditionFlag.Jumping61], "The character is jumping."),
        ([ConditionFlag.MountOrOrnamentTransition], "A mount transition is in progress."),
        ([ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78], "A cutscene is playing."),
        ([ConditionFlag.TradeOpen], "A trade window is open."),
        ([ConditionFlag.OccupiedSummoningBell], "A summoning-bell session is open."),
        ([ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent, ConditionFlag.Occupied,
          ConditionFlag.Occupied30, ConditionFlag.Occupied33, ConditionFlag.Occupied38, ConditionFlag.Occupied39],
            "The character is occupied by another game interaction."),
    ];

    private static readonly string[] LoadingScreenAddons = ["NowLoading", "FadeMiddle", "FadeBack"];

    // Flags a still-open summoning-bell session holds on its own. Only these are tolerated when the
    // caller is re-interacting with the bell to end that session; quest events, cutscenes, trades and
    // every other guard still block.
    private static readonly ConditionFlag[] SummoningBellSessionFlags =
    [
        ConditionFlag.OccupiedSummoningBell, ConditionFlag.OccupiedInEvent, ConditionFlag.Occupied,
        ConditionFlag.Occupied30, ConditionFlag.Occupied33, ConditionFlag.Occupied38, ConditionFlag.Occupied39,
    ];

    /// <summary>Returns why the local player cannot act right now, or null when it can.</summary>
    public static string? GetBlocker() => GetBlocker(tolerateSummoningBellSession: false);

    /// <summary>
    /// Returns why the local player cannot act right now, or null when it can. With
    /// <paramref name="tolerateSummoningBellSession"/>, a summoning-bell session that is still open does
    /// not block: the occupancy flags it holds are ignored while <see cref="ConditionFlag.OccupiedSummoningBell"/>
    /// is set. Use it only to re-interact with that bell in order to close the session.
    /// </summary>
    public static string? GetBlocker(bool tolerateSummoningBellSession)
    {
        try
        {
            var conditions = Conditions.Instance();
            if (conditions == null)
                return "Game conditions are unavailable.";
            bool Read(ConditionFlag flag) => *((bool*)conditions + (int)flag);
            var bellSession = tolerateSummoningBellSession && Read(ConditionFlag.OccupiedSummoningBell);
            var blocker = GetBlocker(flag => !(bellSession && SummoningBellSessionFlags.Contains(flag)) && Read(flag));
            if (blocker is not null)
                return blocker;

            var player = Control.GetLocalPlayer();
            if (player == null)
                return "The local player is unavailable.";
            if (!((NativeGameObject*)player)->GetIsTargetable())
                return "The local player is not interactable yet.";
            if (Player.IsAnimationLocked)
                return "The character is animation-locked.";
            if (IsLoadingScreenVisible())
                return "The screen is still loading.";
            return null;
        }
        catch (Exception ex)
        {
            return $"Player readiness could not be read: {ex.Message}";
        }
    }

    /// <summary>Returns the first transient condition guard that is set, or null.</summary>
    public static string? GetBlocker(Func<ConditionFlag, bool> isConditionSet)
    {
        ArgumentNullException.ThrowIfNull(isConditionSet);
        foreach (var (flags, reason) in TransientGuards)
        {
            foreach (var flag in flags)
            {
                if (isConditionSet(flag))
                    return reason;
            }
        }
        return null;
    }

    private static bool IsLoadingScreenVisible()
    {
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null)
            return false;
        foreach (var name in LoadingScreenAddons)
        {
            var addon = manager->GetAddonByName(name);
            if (addon != null && addon->IsVisible)
                return true;
        }
        return false;
    }
}
