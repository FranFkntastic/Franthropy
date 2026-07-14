using System.Numerics;
using Dalamud.Data;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using Lumina.Excel.Sheets;

namespace Franthropy.Dalamud.Automation.Retainers;

public enum SummoningBellInteractionState
{
    Targeting,
    Submitted,
    Unavailable,
}

public sealed record SummoningBellInteractionResult(
    SummoningBellInteractionState State,
    string Code,
    string Message)
{
    public bool Submitted => State == SummoningBellInteractionState.Submitted;
}

/// <summary>
/// Finds and interacts with a nearby summoning bell through the normal game-object interaction path.
/// Call this on the framework thread, then observe the retainer-list addon before continuing.
/// </summary>
public sealed class DalamudSummoningBellInteractor
{
    public const uint SummoningBellNameRowId = 2000401;
    public const float HousingBellInteractionDistance = 6.5f;
    public const float WorldBellInteractionDistance = 4.75f;

    private static readonly string[] KnownFallbackNames = ["Summoning Bell", "リテイナーベル"];

    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly IDataManager dataManager;

    public DalamudSummoningBellInteractor(
        IObjectTable objectTable,
        ITargetManager targetManager,
        IDataManager dataManager)
    {
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.dataManager = dataManager;
    }

    public unsafe SummoningBellInteractionResult TryInteract()
    {
        var player = objectTable.LocalPlayer;
        if (player is null)
            return Unavailable("PlayerUnavailable", "The local player is unavailable.");

        var names = ResolveBellNames();
        var bells = objectTable
            .Where(value => IsSummoningBellObject(value.ObjectKind, value.Name.TextValue, names))
            .Select(value => new
            {
                Object = value,
                Distance = Vector3.Distance(player.Position, value.Position),
                InteractionDistance = GetInteractionDistance(value.ObjectKind),
            })
            .OrderBy(value => value.Distance)
            .ToArray();
        if (bells.Length == 0)
            return Unavailable("NoNearbySummoningBell", "No summoning bell is loaded nearby.");

        var reachable = bells.FirstOrDefault(value =>
            value.Object.IsTargetable &&
            value.Object.Address != 0 &&
            value.Distance < value.InteractionDistance);
        if (reachable is null)
        {
            var nearest = bells[0];
            return Unavailable(
                "NoInteractableSummoningBell",
                $"The nearest summoning bell is not interactable from the current position ({nearest.Distance:F1} yalms away; limit {nearest.InteractionDistance:F1}).");
        }

        var bell = reachable.Object;
        var distance = reachable.Distance;

        if (targetManager.Target?.Address != bell.Address)
        {
            targetManager.Target = bell;
            return new(
                SummoningBellInteractionState.Targeting,
                "SummoningBellTargeted",
                $"Targeted the nearby summoning bell ({distance:F1} yalms away).");
        }

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return Unavailable("TargetSystemUnavailable", "The game target system is unavailable.");

        targetSystem->InteractWithObject((NativeGameObject*)bell.Address, false);
        return new(
            SummoningBellInteractionState.Submitted,
            "SummoningBellInteractionSubmitted",
            $"Interacted with the nearby summoning bell ({distance:F1} yalms away).");
    }

    public static bool IsSummoningBellObject(
        ObjectKind objectKind,
        string? objectName,
        IEnumerable<string> recognizedNames)
    {
        if (objectKind is not (ObjectKind.EventObj or ObjectKind.HousingEventObject) || string.IsNullOrWhiteSpace(objectName))
            return false;

        return recognizedNames.Any(name =>
            !string.IsNullOrWhiteSpace(name) &&
            string.Equals(name.Trim(), objectName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static float GetInteractionDistance(ObjectKind objectKind) =>
        objectKind == ObjectKind.HousingEventObject
            ? HousingBellInteractionDistance
            : WorldBellInteractionDistance;

    private IReadOnlyList<string> ResolveBellNames()
    {
        var localizedName = dataManager.GetExcelSheet<EObjName>()?
            .GetRowOrDefault(SummoningBellNameRowId)?
            .Singular.ToString();
        return KnownFallbackNames
            .Append(localizedName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    private static SummoningBellInteractionResult Unavailable(string code, string message) =>
        new(SummoningBellInteractionState.Unavailable, code, message);
}
