using System.Diagnostics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Application.Network;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Network;
using ClientFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace Franthropy.Dalamud.Automation.Retainers;

/// <summary>
/// Passively observes one stock StartTalkEvent packet and any matching inbound
/// EventPlay for an exact live actor/event pair. Neither packet is altered.
/// </summary>
public sealed unsafe class DalamudTalkEventPacketTransport : IDisposable
{
    private const int PacketPayloadOffset = 0x20;
    private const int MinimumTalkPayloadLength = 12;
    private const int MaximumCapturedPayloadLength = 256;
    private const int MaximumCapturedSceneDataCount = 8;
    private const int MaximumCapturedEventPlaySamples = 8;
    private const int MaximumCapturedEventYieldSamples = 8;
    private const int MaximumCapturedActorControlSamples = 16;
    private const int MaximumCapturedRawInboundSamples = 32;
    private const double RawInboundCaptureWindowMilliseconds = 500;
    private const int MaximumFlightRecorderSamples = 512;
    private const int MaximumFlightRecorderPacketBytes = 512;
    private const int MaximumPlausiblePacketBytes = 64 * 1024;
    private const double FlightRecorderWindowMilliseconds = 30_000;

    private readonly Hook<ZoneClient.Delegates.SendPacket> sendPacketHook;
    private readonly Hook<PacketDispatcher.Delegates.OnReceivePacket> receivePacketHook;
    private readonly Hook<PacketDispatcher.Delegates.HandleEventPlayPacket> eventPlayHook;
    private readonly Hook<PacketDispatcher.Delegates.HandleEventYieldPacket> eventYieldHook;
    private readonly Hook<PacketDispatcher.Delegates.HandleActorControlPacket> actorControlHook;
    private readonly object observationGate = new();
    private CaptureTarget? activeTarget;
    private bool outboundObservationArmed;
    private bool flightRecorderArmed;
    private long outboundSentTimestamp;
    private int packetsObservedWhileArmed;
    private int sizeEligiblePacketsObserved;
    private TalkEventPacketTransportObservation observation =
        new(TalkEventPacketTransportState.Idle, 0, false, false, 0, 0, "The talk-event observer is idle.");
    private bool disposed;

    public DalamudTalkEventPacketTransport(IGameInteropProvider interopProvider)
    {
        sendPacketHook = interopProvider.HookFromAddress<ZoneClient.Delegates.SendPacket>(
            (nint)ZoneClient.MemberFunctionPointers.SendPacket,
            SendPacketDetour);
        sendPacketHook.Enable();

        eventPlayHook = interopProvider.HookFromAddress<PacketDispatcher.Delegates.HandleEventPlayPacket>(
            (nint)PacketDispatcher.MemberFunctionPointers.HandleEventPlayPacket,
            HandleEventPlayPacketDetour);
        eventPlayHook.Enable();

        eventYieldHook = interopProvider.HookFromAddress<PacketDispatcher.Delegates.HandleEventYieldPacket>(
            (nint)PacketDispatcher.MemberFunctionPointers.HandleEventYieldPacket,
            HandleEventYieldPacketDetour);
        eventYieldHook.Enable();

        actorControlHook = interopProvider.HookFromAddress<PacketDispatcher.Delegates.HandleActorControlPacket>(
            (nint)PacketDispatcher.MemberFunctionPointers.HandleActorControlPacket,
            HandleActorControlPacketDetour);
        actorControlHook.Enable();

        var framework = ClientFramework.Instance();
        var networkModuleProxy = framework == null
            ? null
            : framework->NetworkModuleProxy;
        var receiverCallback = networkModuleProxy == null
            ? null
            : networkModuleProxy->ReceiverCallback;
        if (receiverCallback == null)
            throw new InvalidOperationException("The zone packet receiver callback is unavailable.");

        var packetDispatcher = &receiverCallback->PacketDispatcher;
        var onReceivePacketAddress = (*(nint**)packetDispatcher)[1];
        receivePacketHook = interopProvider.HookFromAddress<PacketDispatcher.Delegates.OnReceivePacket>(
            onReceivePacketAddress,
            OnReceivePacketDetour);
        receivePacketHook.Enable();
    }

    public TalkEventPacketTransportObservation ArmPassThrough(ulong actorId, uint eventId) =>
        Arm(actorId, eventId, captureAllZoneTraffic: false);

    public TalkEventPacketTransportObservation ArmFlightRecorder(ulong actorId, uint eventId) =>
        Arm(actorId, eventId, captureAllZoneTraffic: true);

    private TalkEventPacketTransportObservation Arm(
        ulong actorId,
        uint eventId,
        bool captureAllZoneTraffic)
    {
        if (disposed)
            return Failed("The talk-event packet observer has been disposed.");

        lock (observationGate)
        {
            if (activeTarget is not null)
                return Failed("A talk-event packet observation is already armed.");

            Interlocked.Exchange(ref packetsObservedWhileArmed, 0);
            Interlocked.Exchange(ref sizeEligiblePacketsObserved, 0);
            activeTarget = new(actorId, eventId);
            outboundObservationArmed = true;
            flightRecorderArmed = captureAllZoneTraffic;
            outboundSentTimestamp = 0;
            observation = new(
                TalkEventPacketTransportState.AwaitingBuilderPacket,
                0,
                false,
                false,
                0,
                0,
                captureAllZoneTraffic
                    ? "Armed the passive normal-bell flight recorder; waiting for the stock StartTalkEvent."
                    : "Armed passive observers for the stock StartTalkEvent and matching inbound EventPlay.",
                FlightRecorderArmed: captureAllZoneTraffic);
            return observation;
        }
    }

    private bool SendPacketDetour(
        ZoneClient* zoneClient,
        nint packet,
        uint argument3,
        uint argument4,
        bool argument5)
    {
        if (IsOutboundObservationArmed())
            Interlocked.Increment(ref packetsObservedWhileArmed);

        if (TryObserve(packet) is { } opcode)
        {
            var accepted = sendPacketHook.Original(zoneClient, packet, argument3, argument4, argument5);
            lock (observationGate)
            {
                outboundSentTimestamp = accepted ? Stopwatch.GetTimestamp() : 0;
                observation = observation with
                {
                    State = accepted
                        ? TalkEventPacketTransportState.StockPacketSent
                        : TalkEventPacketTransportState.Failed,
                    Opcode = opcode,
                    PacketsObservedWhileArmed = Volatile.Read(ref packetsObservedWhileArmed),
                    SizeEligiblePacketsObserved = Volatile.Read(ref sizeEligiblePacketsObserved),
                    Message = accepted
                        ? $"Observed and passed through one stock StartTalkEvent packet unchanged (opcode 0x{opcode:X})."
                        : $"Observed the stock StartTalkEvent packet, but the zone send primitive returned false (opcode 0x{opcode:X}).",
                };
                if (accepted)
                    AppendOutboundFlightRecorderSample(packet, argument3, argument4, argument5);
            }
            return accepted;
        }

        lock (observationGate)
            AppendOutboundFlightRecorderSample(packet, argument3, argument4, argument5);
        return sendPacketHook.Original(zoneClient, packet, argument3, argument4, argument5);
    }

    private uint? TryObserve(nint packet)
    {
        CaptureTarget? target;
        lock (observationGate)
        {
            if (!outboundObservationArmed || activeTarget is null)
                return null;
            target = activeTarget;
        }

        if (packet == 0)
            return null;

        var packetPointer = (byte*)packet;
        var encodedSize = *(ulong*)(packetPointer + 8);
        if (encodedSize < 0x10 + MinimumTalkPayloadLength ||
            encodedSize > 0x10 + MaximumCapturedPayloadLength)
        {
            return null;
        }

        Interlocked.Increment(ref sizeEligiblePacketsObserved);
        var payloadPointer = packetPointer + PacketPayloadOffset;
        if (*(ulong*)payloadPointer != target.ActorId ||
            *(uint*)(payloadPointer + 8) != target.EventId)
        {
            return null;
        }

        lock (observationGate)
        {
            if (!outboundObservationArmed || activeTarget != target)
                return null;
            outboundObservationArmed = false;
        }

        return *(uint*)packetPointer;
    }

    private void HandleEventPlayPacketDetour(
        GameObjectId objectId,
        EventId eventId,
        short scene,
        ulong sceneFlags,
        uint* sceneData,
        byte sceneDataCount)
    {
        try
        {
            CaptureTarget? target;
            lock (observationGate)
                target = activeTarget;

            if (target is not null)
            {
                var capturedCount = Math.Min((int)sceneDataCount, MaximumCapturedSceneDataCount);
                var capturedSceneData = new uint[capturedCount];
                if (sceneData != null)
                {
                    for (var index = 0; index < capturedCount; index++)
                        capturedSceneData[index] = sceneData[index];
                }

                lock (observationGate)
                {
                    if (activeTarget == target)
                    {
                        var samples = observation.InboundEventPlaySamples ?? [];
                        var updatedSamples = samples.Length < MaximumCapturedEventPlaySamples
                            ? [.. samples, new InboundEventPlaySample(
                                GetMillisecondsAfterOutbound(),
                                objectId.Id,
                                eventId.Id,
                                scene,
                                sceneFlags,
                                sceneDataCount,
                                capturedSceneData)]
                            : samples;
                        observation = observation with
                        {
                            InboundEventPlayCount = observation.InboundEventPlayCount + 1,
                            InboundEventPlaySamples = updatedSamples,
                        };
                        if (objectId.Id == target.ActorId && eventId.Id == target.EventId)
                        {
                            observation = observation with
                            {
                                InboundEventPlayObserved = true,
                                InboundEventObjectId = objectId.Id,
                                InboundEventId = eventId.Id,
                                InboundScene = scene,
                                InboundSceneFlags = sceneFlags,
                                InboundSceneDataCount = sceneDataCount,
                                InboundSceneData = capturedSceneData,
                            };
                        }
                    }
                }
            }
        }
        catch
        {
            // Observation must never interfere with the client's inbound event path.
        }

        eventPlayHook.Original(objectId, eventId, scene, sceneFlags, sceneData, sceneDataCount);
    }

    private void HandleEventYieldPacketDetour(
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
                if (activeTarget is { } target &&
                    observation.State == TalkEventPacketTransportState.StockPacketSent)
                {
                    var capturedCount = Math.Min((int)intParamCount, MaximumCapturedSceneDataCount);
                    var capturedParams = new int[capturedCount];
                    if (intParams != null)
                    {
                        for (var index = 0; index < capturedCount; index++)
                            capturedParams[index] = intParams[index];
                    }

                    var samples = observation.InboundEventYieldSamples ?? [];
                    var updatedSamples = samples.Length < MaximumCapturedEventYieldSamples
                        ? [.. samples, new InboundEventYieldSample(
                            GetMillisecondsAfterOutbound(),
                            eventId.Id,
                            scene,
                            yieldId,
                            intParamCount,
                            capturedParams)]
                        : samples;
                    observation = observation with
                    {
                        InboundEventYieldCount = observation.InboundEventYieldCount + 1,
                        InboundEventYieldSamples = updatedSamples,
                        MatchingInboundEventYieldObserved =
                            observation.MatchingInboundEventYieldObserved ||
                            eventId.Id == target.EventId,
                    };
                }
            }
        }
        catch
        {
            // Observation must never interfere with the client's inbound event path.
        }

        eventYieldHook.Original(eventId, scene, yieldId, intParams, intParamCount);
    }

    private void HandleActorControlPacketDetour(
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
                if (activeTarget is not null &&
                    observation.State == TalkEventPacketTransportState.StockPacketSent)
                {
                    var samples = observation.InboundActorControlSamples ?? [];
                    var updatedSamples = samples.Length < MaximumCapturedActorControlSamples
                        ? [.. samples, new InboundActorControlSample(
                            GetMillisecondsAfterOutbound(),
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
                        : samples;
                    observation = observation with
                    {
                        InboundActorControlCount = observation.InboundActorControlCount + 1,
                        InboundActorControlSamples = updatedSamples,
                    };
                }
            }
        }
        catch
        {
            // Observation must never interfere with the client's inbound control path.
        }

        actorControlHook.Original(
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
            targetId,
            isRecorded);
    }

    private void OnReceivePacketDetour(
        PacketDispatcher* packetDispatcher,
        uint targetId,
        nint packet)
    {
        try
        {
            lock (observationGate)
            {
                if (activeTarget is not null &&
                    observation.State == TalkEventPacketTransportState.StockPacketSent)
                {
                    var millisecondsAfterOutbound = GetMillisecondsAfterOutbound();
                    var samples = observation.InboundRawPacketSamples ?? [];
                    var word0 = packet == 0 ? 0 : *(uint*)packet;
                    var packetSize = (ushort)(word0 & 0xFFFF);
                    var opcode = (ushort)(word0 >> 16);
                    var updatedSamples =
                        millisecondsAfterOutbound <= RawInboundCaptureWindowMilliseconds &&
                        samples.Length < MaximumCapturedRawInboundSamples &&
                        packet != 0
                            ? [.. samples, new InboundRawPacketSample(
                                millisecondsAfterOutbound,
                                targetId,
                                *(uint*)packet,
                                *((uint*)packet + 1),
                                *((uint*)packet + 2),
                                *((uint*)packet + 3),
                                packetSize >= 20 ? *((uint*)packet + 4) : 0,
                                packetSize,
                                opcode)]
                            : samples;
                    observation = observation with
                    {
                        InboundRawPacketCount = observation.InboundRawPacketCount + 1,
                        InboundRawPacketSamples = updatedSamples,
                    };
                    AppendInboundFlightRecorderSample(targetId, packet);
                }
            }
        }
        catch
        {
            // Observation must never interfere with the client's generic receive path.
        }

        receivePacketHook.Original(packetDispatcher, targetId, packet);
    }

    public TalkEventPacketTransportObservation Observe()
    {
        lock (observationGate)
        {
            return observation with
            {
                PacketsObservedWhileArmed = Volatile.Read(ref packetsObservedWhileArmed),
                SizeEligiblePacketsObserved = Volatile.Read(ref sizeEligiblePacketsObserved),
            };
        }
    }

    public void CancelPending(string reason)
    {
        lock (observationGate)
        {
            var wasPending = observation.State == TalkEventPacketTransportState.AwaitingBuilderPacket;
            var wasRecording = flightRecorderArmed && observation.State == TalkEventPacketTransportState.StockPacketSent;
            activeTarget = null;
            outboundObservationArmed = false;
            flightRecorderArmed = false;
            if (wasPending)
            {
                observation = Failed(
                    $"{reason} Observed {Volatile.Read(ref packetsObservedWhileArmed)} outbound packet(s) while armed, " +
                    $"{Volatile.Read(ref sizeEligiblePacketsObserved)} with a compatible envelope size.");
            }
            else if (wasRecording)
            {
                observation = observation with
                {
                    FlightRecorderStopped = true,
                    Message = reason,
                };
            }
        }
    }

    private TalkEventPacketTransportObservation Failed(string message)
    {
        lock (observationGate)
        {
            observation = new(
                TalkEventPacketTransportState.Failed,
                0,
                false,
                false,
                Volatile.Read(ref packetsObservedWhileArmed),
                Volatile.Read(ref sizeEligiblePacketsObserved),
                message);
            return observation;
        }
    }

    private bool IsOutboundObservationArmed()
    {
        lock (observationGate)
            return outboundObservationArmed;
    }

    private double GetMillisecondsAfterOutbound()
    {
        var sentAt = outboundSentTimestamp;
        return sentAt == 0
            ? 0
            : Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds;
    }

    private void AppendOutboundFlightRecorderSample(
        nint packet,
        uint argument3,
        uint argument4,
        bool argument5)
    {
        if (!CanCaptureFlightRecorderPacket() || packet == 0)
            return;

        try
        {
            var encodedSize = *(ulong*)((byte*)packet + 8);
            if (encodedSize < 0x10 || encodedSize > MaximumPlausiblePacketBytes)
                return;

            var declaredSize = checked((int)encodedSize + 0x10);
            AppendFlightRecorderSample(new(
                GetMillisecondsAfterOutbound(),
                "outbound",
                *(uint*)packet,
                declaredSize,
                0,
                argument3,
                argument4,
                argument5,
                declaredSize > MaximumFlightRecorderPacketBytes,
                CaptureHex(packet, Math.Min(declaredSize, MaximumFlightRecorderPacketBytes))));
        }
        catch
        {
            // Observation must never interfere with the client's outbound path.
        }
    }

    private void AppendInboundFlightRecorderSample(uint targetId, nint packet)
    {
        if (!CanCaptureFlightRecorderPacket() || packet == 0)
            return;

        try
        {
            var word0 = *(uint*)packet;
            var declaredSize = (int)(ushort)(word0 & 0xFFFF);
            if (declaredSize < sizeof(uint) || declaredSize > MaximumPlausiblePacketBytes)
                return;

            AppendFlightRecorderSample(new(
                GetMillisecondsAfterOutbound(),
                "inbound",
                (ushort)(word0 >> 16),
                declaredSize,
                targetId,
                0,
                0,
                false,
                declaredSize > MaximumFlightRecorderPacketBytes,
                CaptureHex(packet, Math.Min(declaredSize, MaximumFlightRecorderPacketBytes))));
        }
        catch
        {
            // Observation must never interfere with the client's inbound path.
        }
    }

    private bool CanCaptureFlightRecorderPacket() =>
        flightRecorderArmed &&
        observation.State == TalkEventPacketTransportState.StockPacketSent &&
        observation.FlightRecorderSamples is not { Length: >= MaximumFlightRecorderSamples } &&
        GetMillisecondsAfterOutbound() <= FlightRecorderWindowMilliseconds;

    private void AppendFlightRecorderSample(ZonePacketFlightRecorderSample sample)
    {
        var samples = observation.FlightRecorderSamples ?? [];
        if (samples.Length >= MaximumFlightRecorderSamples)
            return;

        observation = observation with
        {
            FlightRecorderPacketCount = observation.FlightRecorderPacketCount + 1,
            FlightRecorderOutboundPacketCount =
                observation.FlightRecorderOutboundPacketCount + (sample.Direction == "outbound" ? 1 : 0),
            FlightRecorderInboundPacketCount =
                observation.FlightRecorderInboundPacketCount + (sample.Direction == "inbound" ? 1 : 0),
            FlightRecorderSamples = [.. samples, sample],
        };
    }

    private static string CaptureHex(nint packet, int length) =>
        length <= 0
            ? string.Empty
            : Convert.ToHexString(new ReadOnlySpan<byte>((void*)packet, length));

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        CancelPending("The talk-event packet observer was disposed.");
        receivePacketHook.Dispose();
        actorControlHook.Dispose();
        eventYieldHook.Dispose();
        eventPlayHook.Dispose();
        sendPacketHook.Dispose();
    }

    private sealed record CaptureTarget(ulong ActorId, uint EventId);
}

public enum TalkEventPacketTransportState
{
    Idle,
    AwaitingBuilderPacket,
    StockPacketSent,
    Failed,
}

public sealed record TalkEventPacketTransportObservation(
    TalkEventPacketTransportState State,
    uint Opcode,
    bool BuilderPacketSuppressed,
    bool ConstructedPacket,
    int PacketsObservedWhileArmed,
    int SizeEligiblePacketsObserved,
    string Message,
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
    InboundRawPacketSample[]? InboundRawPacketSamples = null,
    bool FlightRecorderArmed = false,
    bool FlightRecorderStopped = false,
    int FlightRecorderPacketCount = 0,
    int FlightRecorderOutboundPacketCount = 0,
    int FlightRecorderInboundPacketCount = 0,
    ZonePacketFlightRecorderSample[]? FlightRecorderSamples = null)
{
    public bool Pending => State == TalkEventPacketTransportState.AwaitingBuilderPacket;
    public bool Sent => State == TalkEventPacketTransportState.StockPacketSent;
}

public sealed record InboundEventPlaySample(
    double MillisecondsAfterOutbound,
    ulong ObjectId,
    uint EventId,
    short Scene,
    ulong SceneFlags,
    byte SceneDataCount,
    uint[] SceneData);

public sealed record InboundEventYieldSample(
    double MillisecondsAfterOutbound,
    uint EventId,
    short Scene,
    byte YieldId,
    byte IntParamCount,
    int[] IntParams);

public sealed record InboundActorControlSample(
    double MillisecondsAfterOutbound,
    uint EntityId,
    uint Category,
    uint Arg1,
    uint Arg2,
    uint Arg3,
    uint Arg4,
    uint Arg5,
    uint Arg6,
    uint Arg7,
    uint Arg8,
    ulong TargetId,
    bool IsRecorded);

public sealed record InboundRawPacketSample(
    double MillisecondsAfterOutbound,
    uint TargetId,
    uint Word0,
    uint Word1,
    uint Word2,
    uint Word3,
    uint Word4,
    ushort PacketSize,
    ushort Opcode);

public sealed record ZonePacketFlightRecorderSample(
    double MillisecondsAfterOutbound,
    string Direction,
    uint Opcode,
    int DeclaredSize,
    uint TargetId,
    uint Argument3,
    uint Argument4,
    bool Argument5,
    bool Truncated,
    string PacketHex);
