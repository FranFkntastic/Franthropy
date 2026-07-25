using System.Numerics;
using Dalamud.Data;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using NativeEventHandler = FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler;
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

public sealed record RemoteSummoningBellInteractionResult(
    SummoningBellInteractionState State,
    string Code,
    string Message,
    ulong BellGameObjectId,
    float Distance,
    float OrdinaryInteractionDistance,
    string Transport = "",
    uint PacketOpcode = 0,
    bool BuilderPacketSuppressed = false,
    bool ConstructedPacket = false,
    bool OutboundPacketObserved = false,
    uint BellEventId = 0,
    string BellEventIdSource = "",
    float OriginalHitboxRadius = 0,
    float TemporaryHitboxRadius = 0,
    float OriginalBellX = 0,
    float OriginalBellY = 0,
    float OriginalBellZ = 0,
    float TemporaryBellX = 0,
    float TemporaryBellY = 0,
    float TemporaryBellZ = 0,
    float OriginalDefaultBellX = 0,
    float OriginalDefaultBellY = 0,
    float OriginalDefaultBellZ = 0,
    int PacketsObservedWhileArmed = 0,
    int SizeEligiblePacketsObserved = 0)
{
    public bool Submitted => State == SummoningBellInteractionState.Submitted;
}

public sealed record RemoteSummoningBellObservation(
    bool Available,
    bool OutsideOrdinaryInteractionRange,
    string Code,
    string Message,
    ulong BellGameObjectId,
    float Distance,
    float OrdinaryInteractionDistance);

/// <summary>
/// Finds and interacts with a nearby summoning bell through the normal game-object interaction path.
/// Call this on the framework thread, then observe the retainer-list addon before continuing.
/// </summary>
public sealed class DalamudSummoningBellInteractor : IDisposable
{
    public const uint SummoningBellNameRowId = 2000401;
    public const float HousingBellInteractionDistance = 6.5f;
    public const float WorldBellInteractionDistance = 4.75f;

    private static readonly string[] KnownFallbackNames = ["Summoning Bell", "リテイナーベル"];

    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly IDataManager dataManager;
    private readonly DalamudTalkEventPacketTransport? talkPacketTransport;
    private RemoteGeometrySnapshot? remoteGeometrySnapshot;

    public DalamudSummoningBellInteractor(
        IObjectTable objectTable,
        ITargetManager targetManager,
        IDataManager dataManager,
        IGameInteropProvider? interopProvider = null,
        ISigScanner? sigScanner = null)
    {
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.dataManager = dataManager;
        if (interopProvider is not null)
            talkPacketTransport = new(interopProvider);
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

    /// <summary>
    /// Temporarily extends only the selected bell's hitbox and shadows its live/default
    /// positions to the player, invokes the stock interaction path, and passively
    /// observes the resulting StartTalkEvent.
    /// </summary>
    public unsafe RemoteSummoningBellInteractionResult TryOpenLoadedWithScopedHitboxRadius()
    {
        var nearest = FindLoadedBell();
        if (nearest is null)
            return objectTable.LocalPlayer is null
                ? RemoteUnavailable("PlayerUnavailable", "The local player is unavailable.")
                : RemoteUnavailable("NoLoadedSummoningBell", "No targetable summoning bell is loaded in the current object table.");

        if (!IsOutsideInteractionRange(nearest.Distance, nearest.Object.ObjectKind))
        {
            return new(
                SummoningBellInteractionState.Unavailable,
                "SummoningBellStillInRange",
                $"Move beyond the ordinary interaction range before running the scoped hitbox probe ({nearest.Distance:F1} yalms away; limit {nearest.InteractionDistance:F1}).",
                nearest.Object.GameObjectId,
                nearest.Distance,
                nearest.InteractionDistance);
        }

        if (talkPacketTransport is null)
        {
            return new(
                SummoningBellInteractionState.Unavailable,
                "TalkPacketTransportUnavailable",
                "The StartTalkEvent packet transport is unavailable.",
                nearest.Object.GameObjectId,
                nearest.Distance,
                nearest.InteractionDistance);
        }

        var nativeBell = (NativeGameObject*)nearest.Object.Address;
        var (eventId, eventIdSource) = ResolveEventId(nativeBell);
        if (eventId == 0)
        {
            return new(
                SummoningBellInteractionState.Unavailable,
                "SummoningBellEventIdUnavailable",
                "The loaded summoning bell has no live event ID.",
                nearest.Object.GameObjectId,
                nearest.Distance,
                nearest.InteractionDistance);
        }

        var armed = talkPacketTransport.ArmPassThrough(nearest.Object.GameObjectId, eventId);
        if (!armed.Pending)
        {
            return new(
                SummoningBellInteractionState.Unavailable,
                "TalkPacketBuilderArmFailed",
                armed.Message,
                nearest.Object.GameObjectId,
                nearest.Distance,
                nearest.InteractionDistance,
                "Scoped bell HitboxRadius, Position, and DefaultPosition plus stock TargetSystem.InteractWithObject",
                BellEventId: eventId,
                BellEventIdSource: eventIdSource);
        }

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
        {
            talkPacketTransport.CancelPending("The game target system was unavailable.");
            return new(
                SummoningBellInteractionState.Unavailable,
                "TargetSystemUnavailable",
                "The game target system is unavailable.",
                nearest.Object.GameObjectId,
                nearest.Distance,
                nearest.InteractionDistance,
                "Scoped bell HitboxRadius, Position, and DefaultPosition plus stock TargetSystem.InteractWithObject",
                BellEventId: eventId,
                BellEventIdSource: eventIdSource);
        }

        var originalHitboxRadius = nativeBell->HitboxRadius;
        var temporaryHitboxRadius = GetTemporaryHitboxRadius(originalHitboxRadius, nearest.Distance);
        var originalBellPosition = nativeBell->Position;
        var originalDefaultBellPosition = nativeBell->DefaultPosition;
        var playerPosition = objectTable.LocalPlayer!.Position;
        var temporaryBellPosition = originalBellPosition;
        temporaryBellPosition.X = playerPosition.X;
        temporaryBellPosition.Y = playerPosition.Y;
        temporaryBellPosition.Z = playerPosition.Z;
        remoteGeometrySnapshot = new(
            nearest.Object.GameObjectId,
            nearest.Object.Address,
            originalHitboxRadius,
            temporaryHitboxRadius,
            originalBellPosition,
            originalDefaultBellPosition,
            temporaryBellPosition);
        try
        {
            nativeBell->HitboxRadius = temporaryHitboxRadius;
            nativeBell->Position = temporaryBellPosition;
            nativeBell->DefaultPosition = temporaryBellPosition;
            targetManager.Target = nearest.Object;
            targetSystem->InteractWithObject(nativeBell, false);
        }
        catch (Exception exception)
        {
            talkPacketTransport.CancelPending("The stock interaction call failed.");
            RestoreRemoteProbeGeometry();
            return new(
                SummoningBellInteractionState.Unavailable,
                "StockBellInteractionFailed",
                $"The stock interaction call failed: {exception.Message}",
                nearest.Object.GameObjectId,
                nearest.Distance,
                nearest.InteractionDistance,
                "Scoped bell HitboxRadius, Position, and DefaultPosition plus stock TargetSystem.InteractWithObject",
                BellEventId: eventId,
                BellEventIdSource: eventIdSource,
                OriginalHitboxRadius: originalHitboxRadius,
                TemporaryHitboxRadius: temporaryHitboxRadius,
                OriginalBellX: originalBellPosition.X,
                OriginalBellY: originalBellPosition.Y,
                OriginalBellZ: originalBellPosition.Z,
                TemporaryBellX: temporaryBellPosition.X,
                TemporaryBellY: temporaryBellPosition.Y,
                TemporaryBellZ: temporaryBellPosition.Z,
                OriginalDefaultBellX: originalDefaultBellPosition.X,
                OriginalDefaultBellY: originalDefaultBellPosition.Y,
                OriginalDefaultBellZ: originalDefaultBellPosition.Z);
        }

        return new(
            SummoningBellInteractionState.Submitted,
            "StockBellInteractionSubmitted",
            $"Invoked the stock bell interaction with scoped radius {originalHitboxRadius:F1}->{temporaryHitboxRadius:F1} and live/default positions shadowed to the player; holding all shadows through the bounded response observation.",
            nearest.Object.GameObjectId,
            nearest.Distance,
            nearest.InteractionDistance,
            "Scoped bell HitboxRadius, Position, and DefaultPosition plus stock TargetSystem.InteractWithObject",
            BellEventId: eventId,
            BellEventIdSource: eventIdSource,
            OriginalHitboxRadius: originalHitboxRadius,
            TemporaryHitboxRadius: temporaryHitboxRadius,
            OriginalBellX: originalBellPosition.X,
            OriginalBellY: originalBellPosition.Y,
            OriginalBellZ: originalBellPosition.Z,
            TemporaryBellX: temporaryBellPosition.X,
            TemporaryBellY: temporaryBellPosition.Y,
            TemporaryBellZ: temporaryBellPosition.Z,
            OriginalDefaultBellX: originalDefaultBellPosition.X,
            OriginalDefaultBellY: originalDefaultBellPosition.Y,
            OriginalDefaultBellZ: originalDefaultBellPosition.Z);
    }

    public TalkEventPacketTransportObservation ObserveTalkPacketTransport() =>
        talkPacketTransport?.Observe() ??
        new(
            TalkEventPacketTransportState.Failed,
            0,
            false,
            false,
            0,
            0,
            "The StartTalkEvent packet transport is unavailable.");

    public void CancelTalkPacketTransport(string reason)
    {
        talkPacketTransport?.CancelPending(reason);
        RestoreRemoteProbeGeometry();
    }

    public unsafe void RestoreRemoteProbeGeometry()
    {
        if (remoteGeometrySnapshot is not { } snapshot)
            return;
        remoteGeometrySnapshot = null;

        var liveObject = objectTable.FirstOrDefault(value =>
            value.GameObjectId == snapshot.GameObjectId &&
            value.Address == snapshot.Address);
        if (liveObject is null)
            return;

        var nativeObject = (NativeGameObject*)snapshot.Address;
        if (PositionsMatch(nativeObject->Position, snapshot.TemporaryPosition))
            nativeObject->Position = snapshot.OriginalPosition;
        if (PositionsMatch(nativeObject->DefaultPosition, snapshot.TemporaryPosition))
            nativeObject->DefaultPosition = snapshot.OriginalDefaultPosition;
        if (MathF.Abs(nativeObject->HitboxRadius - snapshot.TemporaryRadius) < 0.001f)
            nativeObject->HitboxRadius = snapshot.OriginalRadius;
    }

    private static bool PositionsMatch(
        FFXIVClientStructs.FFXIV.Common.Math.Vector3 left,
        FFXIVClientStructs.FFXIV.Common.Math.Vector3 right) =>
        MathF.Abs(left.X - right.X) < 0.001f &&
        MathF.Abs(left.Y - right.Y) < 0.001f &&
        MathF.Abs(left.Z - right.Z) < 0.001f;

    public RemoteSummoningBellObservation ObserveLoadedBell()
    {
        var nearest = FindLoadedBell();
        if (nearest is null)
        {
            return objectTable.LocalPlayer is null
                ? new(false, false, "PlayerUnavailable", "The local player is unavailable.", 0, 0, 0)
                : new(false, false, "NoLoadedSummoningBell", "No targetable summoning bell is loaded in the current object table.", 0, 0, 0);
        }

        var outsideRange = IsOutsideInteractionRange(nearest.Distance, nearest.Object.ObjectKind);
        return new(
            true,
            outsideRange,
            outsideRange ? "ReadyForRemoteProbe" : "SummoningBellStillInRange",
            outsideRange
                ? $"Loaded bell {nearest.Object.GameObjectId:X} is {nearest.Distance:F1} yalms away, outside its ordinary {nearest.InteractionDistance:F1}-yalm range."
                : $"Loaded bell {nearest.Object.GameObjectId:X} is still inside ordinary interaction range ({nearest.Distance:F1}/{nearest.InteractionDistance:F1} yalms).",
            nearest.Object.GameObjectId,
            nearest.Distance,
            nearest.InteractionDistance);
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

    public static bool IsOutsideInteractionRange(float distance, ObjectKind objectKind) =>
        distance >= GetInteractionDistance(objectKind);

    public static float GetTemporaryHitboxRadius(float originalHitboxRadius, float distance) =>
        MathF.Max(originalHitboxRadius, distance + 1f);

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

    private static unsafe (uint EventId, string Source) ResolveEventId(NativeGameObject* gameObject)
    {
        if (gameObject->EventId.Id != 0)
            return (gameObject->EventId.Id, "GameObject.EventId");

        var directHandler = gameObject->EventHandler;
        if (directHandler != null)
        {
            var directEventId = directHandler->GetEventId().Id;
            if (directEventId != 0)
                return (directEventId, "GameObject.EventHandler.GetEventId");
        }

        NativeEventHandler** handlers = stackalloc NativeEventHandler*[32];
        var count = Math.Clamp(gameObject->GetEventHandlersImpl(handlers), 0, 32);
        for (var index = 0; index < count; index++)
        {
            var handler = handlers[index];
            if (handler is null)
                continue;

            var eventId = handler->GetEventId().Id;
            if (eventId != 0)
                return (eventId, $"GameObject.GetEventHandlersImpl[{index}].GetEventId");
        }

        return (0, "");
    }

    private LoadedBell? FindLoadedBell()
    {
        var player = objectTable.LocalPlayer;
        if (player is null)
            return null;

        var names = ResolveBellNames();
        return objectTable
            .Where(value =>
                IsSummoningBellObject(value.ObjectKind, value.Name.TextValue, names) &&
                value.IsTargetable &&
                value.Address != 0)
            .Select(value => new LoadedBell(
                value,
                Vector3.Distance(player.Position, value.Position),
                GetInteractionDistance(value.ObjectKind)))
            .OrderBy(value => value.Distance)
            .FirstOrDefault();
    }

    private static SummoningBellInteractionResult Unavailable(string code, string message) =>
        new(SummoningBellInteractionState.Unavailable, code, message);

    private static RemoteSummoningBellInteractionResult RemoteUnavailable(string code, string message) =>
        new(SummoningBellInteractionState.Unavailable, code, message, 0, 0, 0);

    public void Dispose()
    {
        RestoreRemoteProbeGeometry();
        talkPacketTransport?.Dispose();
    }

    private sealed record LoadedBell(IGameObject Object, float Distance, float InteractionDistance);

    private sealed record RemoteGeometrySnapshot(
        ulong GameObjectId,
        nint Address,
        float OriginalRadius,
        float TemporaryRadius,
        FFXIVClientStructs.FFXIV.Common.Math.Vector3 OriginalPosition,
        FFXIVClientStructs.FFXIV.Common.Math.Vector3 OriginalDefaultPosition,
        FFXIVClientStructs.FFXIV.Common.Math.Vector3 TemporaryPosition);
}
