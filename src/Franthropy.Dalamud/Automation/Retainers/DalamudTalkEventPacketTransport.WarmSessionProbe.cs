using System.Diagnostics;
using FFXIVClientStructs.FFXIV.Application.Network;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace Franthropy.Dalamud.Automation.Retainers;

public sealed unsafe partial class DalamudTalkEventPacketTransport
{
    private const int MaximumWarmPacketBytes = 512;
    private const int MaximumWarmEventPlaySamples = 16;
    private const int MaximumWarmEventYieldSamples = 16;
    private const int MaximumWarmActorControlSamples = 32;
    private const int MaximumWarmRawInboundSamples = 64;
    private const int MaximumWarmPostReplayContinuationSamples = 32;

    private WarmSessionProbeContext? activeWarmSessionProbe;
    private WarmSessionPacketTemplate? warmSelectionTemplate;
    private WarmSessionPacketTemplate? warmTeardownTemplate;
    private long warmReplayTimestamp;
    private WarmSessionRetentionProbeObservation warmSessionObservation =
        WarmSessionRetentionProbeObservation.Idle;

    public WarmSessionRetentionProbeObservation ArmWarmSessionRetention(
        ulong actorId,
        uint eventId)
    {
        lock (observationGate)
        {
            if (disposed)
                return RejectWarmSession("The warm-session packet observer has been disposed.");
            if (activeTarget is not null ||
                activeYieldProbe is not null ||
                activeWarmSessionProbe is not null)
            {
                return RejectWarmSession("Another event-packet observation is already armed.");
            }

            warmSelectionTemplate = null;
            warmTeardownTemplate = null;
            warmReplayTimestamp = 0;
            activeWarmSessionProbe = new(actorId, eventId);
            warmSessionObservation = new(
                WarmSessionRetentionProbeState.AwaitingSelection,
                actorId,
                eventId,
                0,
                0,
                0,
                false,
                false,
                false,
                false,
                false,
                false,
                null,
                null,
                0,
                null,
                0,
                null,
                0,
                null,
                0,
                null,
                0,
                null,
                "Waiting for the stock scene-0 retainer-selection packet.");
            return warmSessionObservation;
        }
    }

    public WarmSessionRetentionProbeObservation ReplayWarmSelection()
    {
        WarmSessionPacketTemplate selection;
        lock (observationGate)
        {
            if (activeWarmSessionProbe is null)
                return RejectWarmSession("The warm-session retention probe is not active.");
            if (warmSessionObservation.State != WarmSessionRetentionProbeState.TeardownSuppressed ||
                warmSelectionTemplate is null)
            {
                return RejectWarmSession("The final scene-1 teardown has not been suppressed.");
            }

            selection = warmSelectionTemplate;
            warmReplayTimestamp = Stopwatch.GetTimestamp();
            warmSessionObservation = warmSessionObservation with
            {
                State = WarmSessionRetentionProbeState.SendingReplay,
                Message = "Submitting one exact scene-0 selection replay inside the retained session.",
            };
        }

        bool accepted;
        fixed (byte* packet = selection.PacketBytes)
        {
            accepted = sendPacketHook.Original(
                (ZoneClient*)selection.ZoneClientAddress,
                (nint)packet,
                selection.Argument3,
                selection.Argument4,
                selection.Argument5);
        }

        lock (observationGate)
        {
            warmSessionObservation = warmSessionObservation with
            {
                State = accepted
                    ? WarmSessionRetentionProbeState.ReplaySent
                    : WarmSessionRetentionProbeState.Failed,
                ReplaySent = accepted,
                Message = accepted
                    ? $"Sent one exact scene-0 selection replay (opcode 0x{selection.Opcode:X}) after suppressing teardown."
                    : "The zone send primitive rejected the retained-session selection replay.",
            };
            return warmSessionObservation;
        }
    }

    public WarmSessionRetentionProbeObservation ReleaseWarmSession()
    {
        WarmSessionPacketTemplate teardown;
        lock (observationGate)
        {
            if (warmSessionObservation.TeardownReleaseSent)
                return warmSessionObservation;
            if (!warmSessionObservation.TeardownSuppressed ||
                warmTeardownTemplate is null)
            {
                return RejectWarmSession("No suppressed scene-1 teardown is available for cleanup.");
            }

            teardown = warmTeardownTemplate;
            warmSessionObservation = warmSessionObservation with
            {
                State = WarmSessionRetentionProbeState.SendingRelease,
                Message = "Sending the exact suppressed scene-1 teardown for cleanup.",
            };
        }

        bool accepted;
        fixed (byte* packet = teardown.PacketBytes)
        {
            accepted = sendPacketHook.Original(
                (ZoneClient*)teardown.ZoneClientAddress,
                (nint)packet,
                teardown.Argument3,
                teardown.Argument4,
                teardown.Argument5);
        }

        lock (observationGate)
        {
            warmSessionObservation = warmSessionObservation with
            {
                State = accepted
                    ? WarmSessionRetentionProbeState.ReleaseSent
                    : WarmSessionRetentionProbeState.Failed,
                TeardownReleaseSent = accepted,
                Message = accepted
                    ? "Sent the exact suppressed scene-1 teardown for cleanup."
                    : "The zone send primitive rejected the retained-session cleanup packet.",
            };
            return warmSessionObservation;
        }
    }

    public WarmSessionRetentionProbeObservation ObserveWarmSessionRetention()
    {
        lock (observationGate)
            return warmSessionObservation;
    }

    public void StopWarmSessionRetention(string reason)
    {
        lock (observationGate)
        {
            activeWarmSessionProbe = null;
            warmSessionObservation = warmSessionObservation with
            {
                State = warmSessionObservation.State == WarmSessionRetentionProbeState.Failed
                    ? WarmSessionRetentionProbeState.Failed
                    : warmSessionObservation.MatchingScene2Observed
                        ? WarmSessionRetentionProbeState.Scene2Observed
                        : WarmSessionRetentionProbeState.Stopped,
                Message = reason,
            };
        }
    }

    private bool TryHandleWarmSessionPacket(
        ZoneClient* zoneClient,
        nint packet,
        uint argument3,
        uint argument4,
        bool argument5,
        out bool sendResult)
    {
        sendResult = false;
        if (packet == 0)
            return false;

        WarmSessionProbeContext? context;
        lock (observationGate)
            context = activeWarmSessionProbe;
        if (context is null)
            return false;

        try
        {
            var packetPointer = (byte*)packet;
            var encodedSize = *(ulong*)(packetPointer + 8);
            if (encodedSize < 0x10 + YieldEventScene2PacketCodec.PayloadSize ||
                encodedSize > MaximumWarmPacketBytes - 0x10)
            {
                return false;
            }

            var declaredSize = checked((int)encodedSize + 0x10);
            var packetBytes = new byte[declaredSize];
            new ReadOnlySpan<byte>(packetPointer, declaredSize).CopyTo(packetBytes);
            if (!YieldEventScene2PacketCodec.TryDecodeEnvelope(
                    packetBytes,
                    context.EventId,
                    out var decoded))
            {
                return false;
            }

            var template = new WarmSessionPacketTemplate(
                decoded.Opcode,
                decoded.EventId,
                decoded.SceneId,
                decoded.YieldId,
                decoded.ResultCount,
                decoded.RetainerId,
                (nint)zoneClient,
                argument3,
                argument4,
                argument5,
                packetBytes,
                Convert.ToHexString(packetBytes));

            lock (observationGate)
            {
                if (activeWarmSessionProbe != context)
                    return false;

                if (warmSessionObservation.ReplaySent)
                {
                    var samples = warmSessionObservation.PostReplayContinuationSamples ?? [];
                    warmSessionObservation = warmSessionObservation with
                    {
                        PostReplayContinuationCount = warmSessionObservation.PostReplayContinuationCount + 1,
                        PostReplayContinuationSamples = samples.Length < MaximumWarmPostReplayContinuationSamples
                            ? [.. samples, new OutboundYieldEventSceneSample(
                                GetWarmMillisecondsAfterReplay(),
                                decoded.Opcode,
                                decoded.EventId,
                                decoded.SceneId,
                                decoded.YieldId,
                                decoded.ResultCount,
                                decoded.RetainerId)]
                            : samples,
                    };
                }

                if ((decoded.SceneId == 0 || decoded.SceneId == 1) &&
                    decoded.ResultCount == YieldEventScene2PacketCodec.ExpectedResultCount &&
                    (!warmSessionObservation.TeardownSuppressed &&
                     (warmSelectionTemplate is null || decoded.SceneId == 1)))
                {
                    warmSelectionTemplate = template;
                    warmSessionObservation = warmSessionObservation with
                    {
                        State = WarmSessionRetentionProbeState.AwaitingTeardown,
                        Opcode = decoded.Opcode,
                        SelectionSceneId = decoded.SceneId,
                        RetainerId = decoded.RetainerId,
                        SelectionCaptured = true,
                        SelectionPacketHex = template.PacketHex,
                        Message = decoded.SceneId == 1
                            ? "Captured the stock scene-1 selection; waiting for the final scene-1 teardown."
                            : "Captured the initial scene-0 selection. Quit, then select a retainer once more from the reopened list to learn the scene-1 selection.",
                    };
                    return false;
                }

                if (decoded.SceneId != 1 ||
                    decoded.ResultCount != 0 ||
                    warmSelectionTemplate is not { SceneId: 1 } ||
                    warmSessionObservation.TeardownSuppressed)
                {
                    return false;
                }

                warmTeardownTemplate = template;
                warmSessionObservation = warmSessionObservation with
                {
                    State = WarmSessionRetentionProbeState.TeardownSuppressed,
                    TeardownSuppressed = true,
                    TeardownPacketHex = template.PacketHex,
                    Message = "Suppressed exactly one final scene-1 teardown; the legitimate scene-1 server session should remain warm.",
                };
                sendResult = true;
                return true;
            }
        }
        catch
        {
            lock (observationGate)
            {
                warmSessionObservation = warmSessionObservation with
                {
                    State = WarmSessionRetentionProbeState.Failed,
                    Message = "Capturing the warm-session lifecycle packet failed.",
                };
            }
            return false;
        }
    }

    private void ObserveWarmSessionEventPlay(
        GameObjectId objectId,
        EventId eventId,
        short scene,
        ulong sceneFlags,
        uint* sceneData,
        byte sceneDataCount)
    {
        try
        {
            lock (observationGate)
            {
                if (activeWarmSessionProbe is not { } context)
                    return;

                var capturedCount = Math.Min((int)sceneDataCount, MaximumCapturedSceneDataCount);
                var capturedSceneData = new uint[capturedCount];
                if (sceneData != null)
                {
                    for (var index = 0; index < capturedCount; index++)
                        capturedSceneData[index] = sceneData[index];
                }

                var samples = warmSessionObservation.InboundEventPlaySamples ?? [];
                var updatedSamples = samples.Length < MaximumWarmEventPlaySamples
                    ? [.. samples, new InboundEventPlaySample(
                        GetWarmMillisecondsAfterReplay(),
                        objectId.Id,
                        eventId.Id,
                        scene,
                        sceneFlags,
                        sceneDataCount,
                        capturedSceneData)]
                    : samples;
                var matchingScene2 =
                    warmSessionObservation.ReplaySent &&
                    objectId.Id == context.ActorId &&
                    eventId.Id == context.EventId &&
                    scene == 2;
                warmSessionObservation = warmSessionObservation with
                {
                    State = matchingScene2
                        ? WarmSessionRetentionProbeState.Scene2Observed
                        : warmSessionObservation.State,
                    MatchingScene2Observed =
                        warmSessionObservation.MatchingScene2Observed || matchingScene2,
                    InboundEventPlayCount = warmSessionObservation.InboundEventPlayCount + 1,
                    InboundEventPlaySamples = updatedSamples,
                    Message = matchingScene2
                        ? "The retained server session accepted the replay and returned matching scene 2."
                        : warmSessionObservation.Message,
                };
            }
        }
        catch
        {
            // Observation must never interfere with the client's inbound event path.
        }
    }

    private void ObserveWarmSessionEventYield(
        EventId eventId,
        short scene,
        byte yieldId,
        int* intParams,
        byte intParamCount)
    {
        try
        {
            lock (observationGate)
            {
                if (activeWarmSessionProbe is null)
                    return;

                var capturedCount = Math.Min((int)intParamCount, MaximumCapturedSceneDataCount);
                var capturedParams = new int[capturedCount];
                if (intParams != null)
                {
                    for (var index = 0; index < capturedCount; index++)
                        capturedParams[index] = intParams[index];
                }

                var samples = warmSessionObservation.InboundEventYieldSamples ?? [];
                warmSessionObservation = warmSessionObservation with
                {
                    InboundEventYieldCount = warmSessionObservation.InboundEventYieldCount + 1,
                    InboundEventYieldSamples = samples.Length < MaximumWarmEventYieldSamples
                        ? [.. samples, new InboundEventYieldSample(
                            GetWarmMillisecondsAfterReplay(),
                            eventId.Id,
                            scene,
                            yieldId,
                            intParamCount,
                            capturedParams)]
                        : samples,
                };
            }
        }
        catch
        {
            // Observation must never interfere with the client's inbound event path.
        }
    }

    private void ObserveWarmSessionActorControl(
        uint entityId,
        uint category,
        uint arg1,
        uint arg2,
        uint arg3,
        uint arg4,
        uint arg5,
        uint arg6,
        uint arg7,
        uint arg8,
        GameObjectId targetId,
        bool isRecorded)
    {
        try
        {
            lock (observationGate)
            {
                if (activeWarmSessionProbe is null)
                    return;

                var samples = warmSessionObservation.InboundActorControlSamples ?? [];
                warmSessionObservation = warmSessionObservation with
                {
                    InboundActorControlCount = warmSessionObservation.InboundActorControlCount + 1,
                    InboundActorControlSamples = samples.Length < MaximumWarmActorControlSamples
                        ? [.. samples, new InboundActorControlSample(
                            GetWarmMillisecondsAfterReplay(),
                            entityId,
                            category,
                            arg1,
                            arg2,
                            arg3,
                            arg4,
                            arg5,
                            arg6,
                            arg7,
                            arg8,
                            targetId.Id,
                            isRecorded)]
                        : samples,
                };
            }
        }
        catch
        {
            // Observation must never interfere with the client's inbound control path.
        }
    }

    private void ObserveWarmSessionRawPacket(uint targetId, nint packet)
    {
        try
        {
            lock (observationGate)
            {
                if (activeWarmSessionProbe is not { } context || packet == 0)
                    return;

                var samples = warmSessionObservation.InboundRawPacketSamples ?? [];
                var word0 = *(uint*)packet;
                var packetSize = (ushort)(word0 & 0xFFFF);
                var word4 = packetSize >= 20 ? *((uint*)packet + 4) : 0;
                var cleanupAcknowledged =
                    warmSessionObservation.TeardownReleaseSent &&
                    word4 == context.EventId;
                warmSessionObservation = warmSessionObservation with
                {
                    CleanupAcknowledged =
                        warmSessionObservation.CleanupAcknowledged || cleanupAcknowledged,
                    InboundRawPacketCount = warmSessionObservation.InboundRawPacketCount + 1,
                    InboundRawPacketSamples = samples.Length < MaximumWarmRawInboundSamples
                        ? [.. samples, new InboundRawPacketSample(
                            GetWarmMillisecondsAfterReplay(),
                            targetId,
                            word0,
                            *((uint*)packet + 1),
                            *((uint*)packet + 2),
                            *((uint*)packet + 3),
                            word4,
                            packetSize,
                            (ushort)(word0 >> 16))]
                        : samples,
                    Message = cleanupAcknowledged
                        ? "Observed the server acknowledgement for the released scene-1 teardown."
                        : warmSessionObservation.Message,
                };
            }
        }
        catch
        {
            // Observation must never interfere with the client's generic receive path.
        }
    }

    private double GetWarmMillisecondsAfterReplay() =>
        warmReplayTimestamp == 0
            ? 0
            : Stopwatch.GetElapsedTime(warmReplayTimestamp).TotalMilliseconds;

    private WarmSessionRetentionProbeObservation RejectWarmSession(string message) =>
        WarmSessionRetentionProbeObservation.Idle with
        {
            State = WarmSessionRetentionProbeState.Failed,
            Message = message,
        };

    private sealed record WarmSessionProbeContext(ulong ActorId, uint EventId);

    private sealed record WarmSessionPacketTemplate(
        uint Opcode,
        uint EventId,
        ushort SceneId,
        byte YieldId,
        byte ResultCount,
        ulong RetainerId,
        nint ZoneClientAddress,
        uint Argument3,
        uint Argument4,
        bool Argument5,
        byte[] PacketBytes,
        string PacketHex);
}

public enum WarmSessionRetentionProbeState
{
    Idle,
    AwaitingSelection,
    AwaitingTeardown,
    TeardownSuppressed,
    SendingReplay,
    ReplaySent,
    Scene2Observed,
    SendingRelease,
    ReleaseSent,
    Stopped,
    Failed,
}

public sealed record WarmSessionRetentionProbeObservation(
    WarmSessionRetentionProbeState State,
    ulong ActorId,
    uint EventId,
    uint Opcode,
    ushort SelectionSceneId,
    ulong RetainerId,
    bool SelectionCaptured,
    bool TeardownSuppressed,
    bool ReplaySent,
    bool MatchingScene2Observed,
    bool TeardownReleaseSent,
    bool CleanupAcknowledged,
    string? SelectionPacketHex,
    string? TeardownPacketHex,
    int InboundEventPlayCount,
    InboundEventPlaySample[]? InboundEventPlaySamples,
    int InboundEventYieldCount,
    InboundEventYieldSample[]? InboundEventYieldSamples,
    int InboundActorControlCount,
    InboundActorControlSample[]? InboundActorControlSamples,
    int InboundRawPacketCount,
    InboundRawPacketSample[]? InboundRawPacketSamples,
    int PostReplayContinuationCount,
    OutboundYieldEventSceneSample[]? PostReplayContinuationSamples,
    string Message)
{
    public static WarmSessionRetentionProbeObservation Idle { get; } = new(
        WarmSessionRetentionProbeState.Idle,
        0,
        0,
        0,
        0,
        0,
        false,
        false,
        false,
        false,
        false,
        false,
        null,
        null,
        0,
        null,
        0,
        null,
        0,
        null,
        0,
        null,
        0,
        null,
        "The warm-session retention observer is idle.");
}

public sealed record OutboundYieldEventSceneSample(
    double MillisecondsAfterReplay,
    uint Opcode,
    uint EventId,
    ushort SceneId,
    byte YieldId,
    byte ResultCount,
    ulong RetainerId);
