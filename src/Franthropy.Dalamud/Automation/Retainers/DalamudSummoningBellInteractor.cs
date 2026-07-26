using System.Numerics;
using Dalamud.Data;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
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
    int SizeEligiblePacketsObserved = 0,
    bool InboundEventPlayObserved = false,
    ulong InboundEventObjectId = 0,
    uint InboundEventId = 0,
    short InboundScene = 0,
    ulong InboundSceneFlags = 0,
    byte InboundSceneDataCount = 0,
    uint[]? InboundSceneData = null,
    int InboundEventPlayCount = 0,
    InboundEventPlaySample[]? InboundEventPlaySamples = null,
    bool MatchingInboundEventYieldObserved = false,
    int InboundEventYieldCount = 0,
    InboundEventYieldSample[]? InboundEventYieldSamples = null,
    int InboundActorControlCount = 0,
    InboundActorControlSample[]? InboundActorControlSamples = null,
    int InboundRawPacketCount = 0,
    InboundRawPacketSample[]? InboundRawPacketSamples = null)
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

public sealed record NormalSummoningBellCaptureArmResult(
    bool Armed,
    string Code,
    string Message,
    ulong BellGameObjectId,
    uint BellEventId,
    string BellEventIdSource,
    float Distance,
    float OrdinaryInteractionDistance);

public sealed record YieldEventSceneControlArmResult(
    bool Armed,
    string Code,
    string Message,
    ulong BellGameObjectId,
    uint BellEventId,
    string BellEventIdSource,
    float Distance,
    float OrdinaryInteractionDistance);

public sealed record WarmSessionRetentionArmResult(
    bool Armed,
    string Code,
    string Message,
    ulong BellGameObjectId,
    uint BellEventId,
    string BellEventIdSource,
    float Distance,
    float OrdinaryInteractionDistance);

public sealed record NativeRetainerVerbSubmission(
    bool Submitted,
    string Code,
    string Message,
    NativeRetainerVerb Verb,
    ulong BellGameObjectId,
    uint BellEventId,
    string BellEventIdSource,
    short HandlerScene,
    float Distance,
    float OrdinaryInteractionDistance,
    ulong RetainerId,
    YieldEventSceneProbeObservation Transport);

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
            talkPacketTransport = new(interopProvider, sigScanner);
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

    public PositionFrameShadowObservation ArmPositionFrameShadow(
        Vector3 expectedPosition,
        Vector3 hypotheticalPosition,
        uint expectedOpcode = 0x2C6) =>
        talkPacketTransport?.ArmPositionFrameShadow(
            expectedPosition,
            hypotheticalPosition,
            expectedOpcode) ??
        new(
            PositionFrameShadowState.Cancelled,
            expectedOpcode,
            0,
            0,
            PositionFrameShadowVector.From(expectedPosition),
            PositionFrameShadowVector.From(hypotheticalPosition),
            0,
            0,
            false,
            false,
            "The position-frame shadow transport is unavailable.");

    public PositionFrameShadowObservation ObservePositionFrameShadow() =>
        talkPacketTransport?.ObservePositionFrameShadow() ??
        new(
            PositionFrameShadowState.Cancelled,
            0,
            0,
            0,
            new(0, 0, 0),
            new(0, 0, 0),
            0,
            0,
            false,
            false,
            "The position-frame shadow transport is unavailable.");

    public unsafe NormalSummoningBellCaptureArmResult TryArmLoadedBellFlightRecorder(
        bool captureCompleteLifecycle = false)
    {
        var nearest = FindLoadedBell();
        if (nearest is null)
        {
            return new(
                false,
                objectTable.LocalPlayer is null ? "PlayerUnavailable" : "NoLoadedSummoningBell",
                objectTable.LocalPlayer is null
                    ? "The local player is unavailable."
                    : "No targetable summoning bell is loaded in the current object table.",
                0,
                0,
                string.Empty,
                0,
                0);
        }

        if (IsOutsideInteractionRange(nearest.Distance, nearest.Object.ObjectKind))
        {
            return new(
                false,
                "SummoningBellOutOfRange",
                $"Move inside ordinary interaction range before arming the normal capture ({nearest.Distance:F1} yalms away; limit {nearest.InteractionDistance:F1}).",
                nearest.Object.GameObjectId,
                0,
                string.Empty,
                nearest.Distance,
                nearest.InteractionDistance);
        }

        if (talkPacketTransport is null)
        {
            return new(
                false,
                "TalkPacketTransportUnavailable",
                "The normal-bell packet flight recorder is unavailable.",
                nearest.Object.GameObjectId,
                0,
                string.Empty,
                nearest.Distance,
                nearest.InteractionDistance);
        }

        var nativeBell = (NativeGameObject*)nearest.Object.Address;
        var (eventId, eventIdSource) = ResolveEventId(nativeBell);
        if (eventId == 0)
        {
            return new(
                false,
                "SummoningBellEventIdUnavailable",
                "The loaded summoning bell has no live event ID.",
                nearest.Object.GameObjectId,
                0,
                string.Empty,
                nearest.Distance,
                nearest.InteractionDistance);
        }

        var armed = captureCompleteLifecycle
            ? talkPacketTransport.ArmLifecycleRecorder(nearest.Object.GameObjectId, eventId)
            : talkPacketTransport.ArmFlightRecorder(nearest.Object.GameObjectId, eventId);
        return new(
            armed.Pending,
            armed.Pending
                ? captureCompleteLifecycle
                    ? "NormalBellLifecycleRecorderArmed"
                    : "NormalBellFlightRecorderArmed"
                : "NormalBellFlightRecorderArmFailed",
            armed.Message,
            nearest.Object.GameObjectId,
            eventId,
            eventIdSource,
            nearest.Distance,
            nearest.InteractionDistance);
    }

    public unsafe YieldEventSceneControlArmResult TryArmYieldEventSceneControl()
    {
        var nearest = FindLoadedBell();
        if (nearest is null)
        {
            return new(
                false,
                objectTable.LocalPlayer is null ? "PlayerUnavailable" : "NoLoadedSummoningBell",
                objectTable.LocalPlayer is null
                    ? "The local player is unavailable."
                    : "No targetable summoning bell is loaded in the current object table.",
                0,
                0,
                string.Empty,
                0,
                0);
        }

        if (IsOutsideInteractionRange(nearest.Distance, nearest.Object.ObjectKind))
        {
            return new(
                false,
                "SummoningBellOutOfRange",
                $"Move inside ordinary interaction range before arming the yield control ({nearest.Distance:F1} yalms away; limit {nearest.InteractionDistance:F1}).",
                nearest.Object.GameObjectId,
                0,
                string.Empty,
                nearest.Distance,
                nearest.InteractionDistance);
        }

        if (talkPacketTransport is null)
        {
            return new(
                false,
                "YieldPacketTransportUnavailable",
                "The YieldEventScene2 packet transport is unavailable.",
                nearest.Object.GameObjectId,
                0,
                string.Empty,
                nearest.Distance,
                nearest.InteractionDistance);
        }

        var nativeBell = (NativeGameObject*)nearest.Object.Address;
        var (eventId, eventIdSource) = ResolveEventId(nativeBell);
        if (eventId == 0)
        {
            return new(
                false,
                "SummoningBellEventIdUnavailable",
                "The loaded summoning bell has no live event ID.",
                nearest.Object.GameObjectId,
                0,
                string.Empty,
                nearest.Distance,
                nearest.InteractionDistance);
        }

        var armed = talkPacketTransport.ArmYieldControl(nearest.Object.GameObjectId, eventId);
        return new(
            armed.State == YieldEventSceneProbeState.AwaitingControlPacket,
            armed.State == YieldEventSceneProbeState.AwaitingControlPacket
                ? "YieldEventSceneControlArmed"
                : "YieldEventSceneControlArmFailed",
            armed.Message,
            nearest.Object.GameObjectId,
            eventId,
            eventIdSource,
            nearest.Distance,
            nearest.InteractionDistance);
    }

    public YieldEventSceneProbeObservation ReplayCapturedYieldEventScene() =>
        talkPacketTransport?.ReplayCapturedYield() ??
        YieldEventSceneProbeObservation.Idle with
        {
            State = YieldEventSceneProbeState.Failed,
            Message = "The YieldEventScene2 packet transport is unavailable.",
        };

    public unsafe NativeRetainerVerbSubmission TryInvokeNativeRetainerVerb(
        NativeRetainerVerb verb,
        ulong verifiedRetainerId = 0)
    {
        var nearest = FindLoadedBell();
        if (nearest is null)
        {
            return NativeVerbUnavailable(
                verb,
                objectTable.LocalPlayer is null ? "PlayerUnavailable" : "NoLoadedSummoningBell",
                objectTable.LocalPlayer is null
                    ? "The local player is unavailable."
                    : "No targetable summoning bell is loaded in the current object table.");
        }

        if (talkPacketTransport is null)
        {
            return NativeVerbUnavailable(
                verb,
                "NativeEventYieldTransportUnavailable",
                "The native event-yield transport is unavailable.",
                nearest);
        }

        var nativeBell = (NativeGameObject*)nearest.Object.Address;
        var handler = ResolveEventHandler(nativeBell, out var eventId, out var eventIdSource);
        if (handler == null || eventId == 0)
        {
            return NativeVerbUnavailable(
                verb,
                "SummoningBellEventHandlerUnavailable",
                "The loaded summoning bell has no live native event handler.",
                nearest);
        }

        var retainerId = verifiedRetainerId;
        if (verb == NativeRetainerVerb.CallRetainer)
        {
            if (retainerId == 0)
            {
                var manager = RetainerManager.Instance();
                if (manager == null)
                {
                    return NativeVerbUnavailable(
                        verb,
                        "RetainerManagerUnavailable",
                        "The native retainer manager is unavailable.",
                        nearest,
                        eventId,
                        eventIdSource,
                        handler->Scene);
                }

                for (var index = 0U; index < manager->GetRetainerCount(); index++)
                {
                    var retainer = manager->GetRetainerBySortedIndex(index);
                    if (retainer == null || retainer->RetainerId == 0)
                        continue;
                    retainerId = retainer->RetainerId;
                    break;
                }
            }

            if (retainerId == 0)
            {
                return NativeVerbUnavailable(
                    verb,
                    "RetainerIdentityUnavailable",
                    "No live retainer identity is available to the native CallRetainer verb.",
                    nearest,
                    eventId,
                    eventIdSource,
                    handler->Scene);
            }
        }

        var transport = talkPacketTransport.InvokeNativeRetainerVerb(
            handler,
            nearest.Object.GameObjectId,
            eventId,
            retainerId,
            verb);
        return new(
            transport.Sent,
            transport.Sent ? "NativeRetainerVerbSubmitted" : "NativeRetainerVerbNotSubmitted",
            transport.Message,
            verb,
            nearest.Object.GameObjectId,
            eventId,
            eventIdSource,
            handler->Scene,
            nearest.Distance,
            nearest.InteractionDistance,
            retainerId,
            transport);
    }

    public YieldEventSceneProbeObservation ObserveYieldEventSceneProbe() =>
        talkPacketTransport?.ObserveYieldProbe() ??
        YieldEventSceneProbeObservation.Idle with
        {
            State = YieldEventSceneProbeState.Failed,
            Message = "The YieldEventScene2 packet transport is unavailable.",
        };

    public void CancelYieldEventSceneProbe(string reason) =>
        talkPacketTransport?.CancelYieldProbe(reason);

    public void DiscardYieldEventSceneTemplate(string reason) =>
        talkPacketTransport?.DiscardYieldTemplate(reason);

    public unsafe WarmSessionRetentionArmResult TryArmWarmSessionRetention()
    {
        var nearest = FindLoadedBell();
        if (nearest is null)
        {
            return new(
                false,
                objectTable.LocalPlayer is null ? "PlayerUnavailable" : "NoLoadedSummoningBell",
                objectTable.LocalPlayer is null
                    ? "The local player is unavailable."
                    : "No targetable summoning bell is loaded in the current object table.",
                0,
                0,
                string.Empty,
                0,
                0);
        }

        if (IsOutsideInteractionRange(nearest.Distance, nearest.Object.ObjectKind))
        {
            return new(
                false,
                "SummoningBellOutOfRange",
                $"Move inside ordinary interaction range before arming warm-session retention ({nearest.Distance:F1} yalms away; limit {nearest.InteractionDistance:F1}).",
                nearest.Object.GameObjectId,
                0,
                string.Empty,
                nearest.Distance,
                nearest.InteractionDistance);
        }

        if (talkPacketTransport is null)
        {
            return new(
                false,
                "WarmSessionPacketTransportUnavailable",
                "The warm-session packet transport is unavailable.",
                nearest.Object.GameObjectId,
                0,
                string.Empty,
                nearest.Distance,
                nearest.InteractionDistance);
        }

        var nativeBell = (NativeGameObject*)nearest.Object.Address;
        var (eventId, eventIdSource) = ResolveEventId(nativeBell);
        if (eventId == 0)
        {
            return new(
                false,
                "SummoningBellEventIdUnavailable",
                "The loaded summoning bell has no live event ID.",
                nearest.Object.GameObjectId,
                0,
                string.Empty,
                nearest.Distance,
                nearest.InteractionDistance);
        }

        var armed = talkPacketTransport.ArmWarmSessionRetention(
            nearest.Object.GameObjectId,
            eventId);
        return new(
            armed.State == WarmSessionRetentionProbeState.AwaitingSelection,
            armed.State == WarmSessionRetentionProbeState.AwaitingSelection
                ? "WarmSessionRetentionArmed"
                : "WarmSessionRetentionArmFailed",
            armed.Message,
            nearest.Object.GameObjectId,
            eventId,
            eventIdSource,
            nearest.Distance,
            nearest.InteractionDistance);
    }

    public WarmSessionRetentionProbeObservation ReplayWarmSessionSelection() =>
        talkPacketTransport?.ReplayWarmSelection() ??
        WarmSessionRetentionProbeObservation.Idle with
        {
            State = WarmSessionRetentionProbeState.Failed,
            Message = "The warm-session packet transport is unavailable.",
        };

    public WarmSessionRetentionProbeObservation ReleaseWarmSession() =>
        talkPacketTransport?.ReleaseWarmSession() ??
        WarmSessionRetentionProbeObservation.Idle with
        {
            State = WarmSessionRetentionProbeState.Failed,
            Message = "The warm-session packet transport is unavailable.",
        };

    public WarmSessionRetentionProbeObservation ObserveWarmSessionRetention() =>
        talkPacketTransport?.ObserveWarmSessionRetention() ??
        WarmSessionRetentionProbeObservation.Idle with
        {
            State = WarmSessionRetentionProbeState.Failed,
            Message = "The warm-session packet transport is unavailable.",
        };

    public void StopWarmSessionRetention(string reason) =>
        talkPacketTransport?.StopWarmSessionRetention(reason);

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

    private static unsafe NativeEventHandler* ResolveEventHandler(
        NativeGameObject* gameObject,
        out uint eventId,
        out string source)
    {
        var directHandler = gameObject->EventHandler;
        if (directHandler != null)
        {
            var directEventId = directHandler->GetEventId().Id;
            if (directEventId != 0)
            {
                eventId = directEventId;
                source = "GameObject.EventHandler.GetEventId";
                return directHandler;
            }
        }

        NativeEventHandler** handlers = stackalloc NativeEventHandler*[32];
        var count = Math.Clamp(gameObject->GetEventHandlersImpl(handlers), 0, 32);
        for (var index = 0; index < count; index++)
        {
            var handler = handlers[index];
            if (handler == null)
                continue;

            var candidateEventId = handler->GetEventId().Id;
            if (candidateEventId == 0)
                continue;

            eventId = candidateEventId;
            source = $"GameObject.GetEventHandlersImpl[{index}].GetEventId";
            return handler;
        }

        eventId = 0;
        source = "";
        return null;
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

    private static NativeRetainerVerbSubmission NativeVerbUnavailable(
        NativeRetainerVerb verb,
        string code,
        string message,
        LoadedBell? bell = null,
        uint eventId = 0,
        string eventIdSource = "",
        short handlerScene = -1) =>
        new(
            false,
            code,
            message,
            verb,
            bell?.Object.GameObjectId ?? 0,
            eventId,
            eventIdSource,
            handlerScene,
            bell?.Distance ?? 0,
            bell?.InteractionDistance ?? 0,
            0,
            YieldEventSceneProbeObservation.Idle with
            {
                State = YieldEventSceneProbeState.Failed,
                Message = message,
            });

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
