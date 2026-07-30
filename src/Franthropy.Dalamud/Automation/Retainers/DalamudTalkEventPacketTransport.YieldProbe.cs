using System.Diagnostics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Application.Network;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using NativeEventHandler = FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler;

namespace Franthropy.Dalamud.Automation.Retainers;

public sealed unsafe partial class DalamudTalkEventPacketTransport
{
    private const int MaximumYieldPacketBytes = 512;
    private const int MaximumYieldEventPlaySamples = 8;
    private const int MaximumYieldEventYieldSamples = 8;
    private const int MaximumYieldActorControlSamples = 16;
    private const int MaximumYieldRawInboundSamples = 32;
    private const string NativeEventYieldSignature =
        "40 53 48 83 EC 30 F6 81 90 00 00 00 01 48 8B D9 75 ?? 44 88 4C 24 20 4D 8B C8 44 0F B6 C2 0F B7 51 78 8B 49 20 E8 ?? ?? ?? ?? 80 8B 90 00 00 00 01";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeEventYieldDelegate(
        NativeEventHandler* handler,
        byte yieldId,
        uint* results,
        byte resultCount);

    private YieldProbeContext? activeYieldProbe;
    private YieldEventScenePacketTemplate? cachedYieldTemplate;
    private NativeEventYieldDelegate? nativeEventYield;
    private long yieldSentTimestamp;
    private YieldEventSceneProbeObservation yieldObservation = YieldEventSceneProbeObservation.Idle;

    public YieldEventSceneProbeObservation ArmYieldControl(ulong actorId, uint eventId)
    {
        if (disposed)
            return FailYield("The yield-event packet observer has been disposed.");

        lock (observationGate)
        {
            if (activeTarget is not null ||
                activeYieldProbe is not null ||
                activeWarmSessionProbe is not null)
                return FailYield("Another event-packet observation is already armed.");

            cachedYieldTemplate = null;
            yieldSentTimestamp = 0;
            activeYieldProbe = new(actorId, eventId, YieldEventSceneProbeMode.InSessionControl);
            yieldObservation = new(
                YieldEventSceneProbeState.AwaitingControlPacket,
                YieldEventSceneProbeMode.InSessionControl,
                actorId,
                eventId,
                0,
                0,
                0,
                0,
                0,
                false,
                false,
                false,
                false,
                0,
                0,
                0,
                0,
                0,
                null,
                0,
                null,
                0,
                null,
                null,
                null,
                "Armed for one stock YieldEventScene2. The stock frame will be replaced with an exact cloned packet.");
            return yieldObservation;
        }
    }

    public YieldEventSceneProbeObservation ReplayCapturedYield()
    {
        YieldEventScenePacketTemplate template;
        lock (observationGate)
        {
            if (disposed)
                return FailYield("The yield-event packet observer has been disposed.");
            if (activeTarget is not null ||
                activeYieldProbe is not null ||
                activeWarmSessionProbe is not null)
                return FailYield("Another event-packet observation is already active.");
            if (cachedYieldTemplate is null)
                return FailYield("No confirmed current-build YieldEventScene2 template is cached.");

            template = cachedYieldTemplate;
            activeYieldProbe = new(
                template.ActorId,
                template.EventId,
                YieldEventSceneProbeMode.SessionFreeReplay);
            yieldSentTimestamp = Stopwatch.GetTimestamp();
            yieldObservation = new(
                YieldEventSceneProbeState.Sending,
                YieldEventSceneProbeMode.SessionFreeReplay,
                template.ActorId,
                template.EventId,
                template.Opcode,
                template.SceneId,
                template.YieldId,
                template.Result0,
                template.Result1,
                false,
                false,
                false,
                true,
                template.RetainerId,
                0,
                0,
                0,
                0,
                null,
                0,
                null,
                0,
                null,
                null,
                template.PacketHex,
                "Submitting one cached YieldEventScene2 outside an accepted event session.");
        }

        bool accepted;
        fixed (byte* packet = template.PacketBytes)
        {
            accepted = sendPacketHook.Original(
                (ZoneClient*)template.ZoneClientAddress,
                (nint)packet,
                template.Argument3,
                template.Argument4,
                template.Argument5);
        }

        lock (observationGate)
        {
            if (activeYieldProbe is not { Mode: YieldEventSceneProbeMode.SessionFreeReplay })
                return yieldObservation;

            yieldObservation = yieldObservation with
            {
                State = accepted
                    ? YieldEventSceneProbeState.PacketSent
                    : YieldEventSceneProbeState.Failed,
                Sent = accepted,
                Message = accepted
                    ? $"Submitted one cached YieldEventScene2 (opcode 0x{template.Opcode:X}) without a bell session."
                    : "The zone send primitive rejected the cached YieldEventScene2.",
            };
            if (!accepted)
                activeYieldProbe = null;
            return yieldObservation;
        }
    }

    public YieldEventSceneProbeObservation InvokeNativeRetainerVerb(
        NativeEventHandler* handler,
        ulong actorId,
        uint eventId,
        ulong retainerId,
        NativeRetainerVerb verb)
    {
        if (disposed)
            return FailYield("The native retainer-verb observer has been disposed.");
        if (handler == null)
            return FailYield("The loaded bell has no native event handler.");
        if (nativeEventYield is null)
            return FailYield("The current-build native event-yield signature could not be resolved.");
        if (verb == NativeRetainerVerb.CallRetainer && retainerId == 0)
            return FailYield("CallRetainer requires a live retainer ID.");

        var mode = verb == NativeRetainerVerb.CallRetainer
            ? YieldEventSceneProbeMode.NativeCallRetainer
            : YieldEventSceneProbeMode.NativeSelectRetainer;
        var yieldId = verb == NativeRetainerVerb.CallRetainer ? (byte)7 : (byte)13;
        var resultCount = verb == NativeRetainerVerb.CallRetainer ? (byte)2 : (byte)0;
        var result0 = verb == NativeRetainerVerb.CallRetainer ? (uint)(retainerId >> 32) : 0;
        var result1 = verb == NativeRetainerVerb.CallRetainer ? (uint)retainerId : 0;
        var scene = unchecked((ushort)handler->Scene);

        lock (observationGate)
        {
            if (activeTarget is not null ||
                activeYieldProbe is not null ||
                activeWarmSessionProbe is not null)
            {
                return FailYield("Another event-packet observation is already active.");
            }

            activeYieldProbe = new(actorId, eventId, mode);
            yieldSentTimestamp = Stopwatch.GetTimestamp();
            yieldObservation = new(
                YieldEventSceneProbeState.Sending,
                mode,
                actorId,
                eventId,
                0,
                scene,
                yieldId,
                result0,
                result1,
                false,
                false,
                false,
                cachedYieldTemplate is not null,
                retainerId,
                0,
                0,
                0,
                0,
                null,
                0,
                null,
                0,
                null,
                null,
                null,
                $"Invoking the signature-resolved {verb} event verb once (scene {handler->Scene}, yield {yieldId}, results {resultCount}).");
        }

        var originalYieldFlags = *((byte*)handler + 0x90);
        try
        {
            if (resultCount == 0)
            {
                nativeEventYield(handler, yieldId, null, 0);
            }
            else
            {
                var results = stackalloc uint[2];
                results[0] = result0;
                results[1] = result1;
                nativeEventYield(handler, yieldId, results, resultCount);
            }
        }
        catch (Exception ex)
        {
            return FailYield($"The signature-resolved {verb} invocation failed: {ex.Message}");
        }
        finally
        {
            // The native builder marks the handler as having yielded. These cold probes do
            // not own an accepted Lua event lifecycle, so restore the exact pre-call byte.
            *((byte*)handler + 0x90) = originalYieldFlags;
        }

        lock (observationGate)
        {
            if (activeYieldProbe is { Mode: var activeMode } &&
                activeMode == mode &&
                yieldObservation.State == YieldEventSceneProbeState.Sending)
            {
                activeYieldProbe = null;
                yieldObservation = yieldObservation with
                {
                    State = YieldEventSceneProbeState.Failed,
                    Message =
                        $"The native {verb} builder returned without submitting an event-yield packet. " +
                        $"Handler scene was {handler->Scene}.",
                };
            }

            return yieldObservation;
        }
    }

    public YieldEventSceneProbeObservation ObserveYieldProbe()
    {
        lock (observationGate)
        {
            return yieldObservation with
            {
                CachedTemplateAvailable = cachedYieldTemplate is not null,
            };
        }
    }

    public void CancelYieldProbe(string reason)
    {
        lock (observationGate)
        {
            if (activeYieldProbe is null)
                return;

            activeYieldProbe = null;
            yieldObservation = yieldObservation with
            {
                State = yieldObservation.State == YieldEventSceneProbeState.Failed
                    ? YieldEventSceneProbeState.Failed
                    : YieldEventSceneProbeState.Stopped,
                Message = reason,
                CachedTemplateAvailable = cachedYieldTemplate is not null,
            };
        }
    }

    public void DiscardYieldTemplate(string reason)
    {
        lock (observationGate)
        {
            activeYieldProbe = null;
            cachedYieldTemplate = null;
            yieldSentTimestamp = 0;
            yieldObservation = YieldEventSceneProbeObservation.Idle with
            {
                Message = reason,
            };
        }
    }

    private bool TryReplaceYieldControlPacket(
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

        YieldProbeContext? context;
        lock (observationGate)
            context = activeYieldProbe;
        if (context is not { Mode: YieldEventSceneProbeMode.InSessionControl } ||
            yieldObservation.State != YieldEventSceneProbeState.AwaitingControlPacket)
        {
            return false;
        }

        try
        {
            var packetPointer = (byte*)packet;
            var encodedSize = *(ulong*)(packetPointer + 8);
            if (encodedSize < 0x10 + 16 ||
                encodedSize > MaximumYieldPacketBytes - 0x10)
            {
                return false;
            }

            var declaredSize = checked((int)encodedSize + 0x10);
            var packetBytes = new byte[declaredSize];
            new ReadOnlySpan<byte>(packetPointer, declaredSize).CopyTo(packetBytes);
            if (!YieldEventScene2PacketCodec.TryDecode(
                    packetBytes,
                    context.EventId,
                    out var decoded))
            {
                return false;
            }

            var template = new YieldEventScenePacketTemplate(
                context.ActorId,
                decoded.EventId,
                decoded.Opcode,
                decoded.SceneId,
                decoded.YieldId,
                decoded.Result0,
                decoded.Result1,
                decoded.RetainerId,
                (nint)zoneClient,
                argument3,
                argument4,
                argument5,
                packetBytes,
                Convert.ToHexString(packetBytes));

            lock (observationGate)
            {
                if (activeYieldProbe != context ||
                    yieldObservation.State != YieldEventSceneProbeState.AwaitingControlPacket)
                {
                    return false;
                }

                yieldSentTimestamp = Stopwatch.GetTimestamp();
                yieldObservation = yieldObservation with
                {
                    State = YieldEventSceneProbeState.Sending,
                    Opcode = template.Opcode,
                    SceneId = decoded.SceneId,
                    YieldId = decoded.YieldId,
                    Result0 = decoded.Result0,
                    Result1 = decoded.Result1,
                    RetainerId = decoded.RetainerId,
                    ReplacedOriginal = true,
                    OutboundPacketHex = template.PacketHex,
                    Message = "Captured the stock YieldEventScene2 and replaced it with an exact cloned packet.",
                };
            }

            fixed (byte* clonedPacket = packetBytes)
            {
                sendResult = sendPacketHook.Original(
                    zoneClient,
                    (nint)clonedPacket,
                    argument3,
                    argument4,
                    argument5);
            }

            lock (observationGate)
            {
                if (activeYieldProbe == context)
                {
                    if (sendResult)
                        cachedYieldTemplate = template;
                    yieldObservation = yieldObservation with
                    {
                        State = sendResult
                            ? YieldEventSceneProbeState.PacketSent
                            : YieldEventSceneProbeState.Failed,
                        Sent = sendResult,
                        CachedTemplateAvailable = sendResult,
                        Message = sendResult
                            ? $"Sent one exact cloned YieldEventScene2 (opcode 0x{template.Opcode:X})."
                            : "The zone send primitive rejected the cloned YieldEventScene2.",
                    };
                    if (!sendResult)
                        activeYieldProbe = null;
                }
            }

            return true;
        }
        catch
        {
            lock (observationGate)
            {
                if (activeYieldProbe == context)
                {
                    yieldObservation = yieldObservation with
                    {
                        State = YieldEventSceneProbeState.Failed,
                        Message = "Capturing or cloning the stock YieldEventScene2 failed.",
                    };
                    activeYieldProbe = null;
                }
            }
            return false;
        }
    }

    private bool TryObserveNativeRetainerVerbOutbound(nint packet)
    {
        if (packet == 0)
            return false;

        YieldProbeContext? context;
        lock (observationGate)
            context = activeYieldProbe;
        if (context is not
            {
                Mode: YieldEventSceneProbeMode.NativeCallRetainer or
                    YieldEventSceneProbeMode.NativeSelectRetainer,
            } ||
            yieldObservation.State != YieldEventSceneProbeState.Sending)
        {
            return false;
        }

        try
        {
            var packetPointer = (byte*)packet;
            var encodedSize = *(ulong*)(packetPointer + 8);
            if (encodedSize < 0x10 + YieldEventScene2PacketCodec.PayloadSize ||
                encodedSize > MaximumYieldPacketBytes - 0x10)
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

            var expectedYieldId = context.Mode == YieldEventSceneProbeMode.NativeCallRetainer
                ? (byte)7
                : (byte)13;
            var expectedResultCount = context.Mode == YieldEventSceneProbeMode.NativeCallRetainer
                ? (byte)2
                : (byte)0;
            if (decoded.YieldId != expectedYieldId ||
                decoded.ResultCount != expectedResultCount)
            {
                return false;
            }

            lock (observationGate)
            {
                if (activeYieldProbe != context ||
                    yieldObservation.State != YieldEventSceneProbeState.Sending)
                {
                    return false;
                }

                yieldObservation = yieldObservation with
                {
                    Opcode = decoded.Opcode,
                    SceneId = decoded.SceneId,
                    YieldId = decoded.YieldId,
                    Result0 = decoded.Result0,
                    Result1 = decoded.Result1,
                    RetainerId = expectedResultCount == 2 ? decoded.RetainerId : 0,
                    OutboundPacketHex = Convert.ToHexString(packetBytes),
                    Message =
                        $"The native {context.Mode} builder produced opcode 0x{decoded.Opcode:X}, " +
                        $"scene {decoded.SceneId}, yield {decoded.YieldId}, results {decoded.ResultCount}.",
                };
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void CompleteNativeRetainerVerbOutbound(bool accepted)
    {
        lock (observationGate)
        {
            if (activeYieldProbe is not
                {
                    Mode: YieldEventSceneProbeMode.NativeCallRetainer or
                        YieldEventSceneProbeMode.NativeSelectRetainer,
                })
            {
                return;
            }

            yieldObservation = yieldObservation with
            {
                State = accepted
                    ? YieldEventSceneProbeState.PacketSent
                    : YieldEventSceneProbeState.Failed,
                Sent = accepted,
                Message = accepted
                    ? $"{yieldObservation.Message} The zone send primitive accepted the one native-built packet."
                    : $"{yieldObservation.Message} The zone send primitive rejected the native-built packet.",
            };
            if (!accepted)
                activeYieldProbe = null;
        }
    }

    private void ObserveYieldEventPlay(
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
                if (activeYieldProbe is not { } context ||
                    yieldObservation.State != YieldEventSceneProbeState.PacketSent)
                {
                    return;
                }

                var capturedCount = Math.Min((int)sceneDataCount, MaximumCapturedSceneDataCount);
                var capturedSceneData = new uint[capturedCount];
                if (sceneData != null)
                {
                    for (var index = 0; index < capturedCount; index++)
                        capturedSceneData[index] = sceneData[index];
                }

                var samples = yieldObservation.InboundEventPlaySamples ?? [];
                var updatedSamples = samples.Length < MaximumYieldEventPlaySamples
                    ? [.. samples, new InboundEventPlaySample(
                        GetYieldMillisecondsAfterOutbound(),
                        objectId.Id,
                        eventId.Id,
                        scene,
                        sceneFlags,
                        sceneDataCount,
                        capturedSceneData)]
                    : samples;
                var matchingScene = context.Mode is
                    YieldEventSceneProbeMode.NativeCallRetainer or
                    YieldEventSceneProbeMode.NativeSelectRetainer
                        ? scene >= 0
                        : scene == 2;
                var matching = objectId.Id == context.ActorId &&
                               eventId.Id == context.EventId &&
                               matchingScene;
                yieldObservation = yieldObservation with
                {
                    MatchingEventPlayObserved =
                        yieldObservation.MatchingEventPlayObserved || matching,
                    InboundEventObjectId = matching
                        ? objectId.Id
                        : yieldObservation.InboundEventObjectId,
                    InboundScene = matching ? scene : yieldObservation.InboundScene,
                    InboundSceneFlags = matching
                        ? sceneFlags
                        : yieldObservation.InboundSceneFlags,
                    InboundSceneData = matching
                        ? capturedSceneData
                        : yieldObservation.InboundSceneData,
                    InboundEventPlayCount = yieldObservation.InboundEventPlayCount + 1,
                    InboundEventPlaySamples = updatedSamples,
                };
            }
        }
        catch
        {
            // Observation must never interfere with the client's inbound event path.
        }
    }

    private void ObserveYieldEventYield(
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
                if (activeYieldProbe is null ||
                    yieldObservation.State != YieldEventSceneProbeState.PacketSent)
                {
                    return;
                }

                var capturedCount = Math.Min((int)intParamCount, MaximumCapturedSceneDataCount);
                var capturedParams = new int[capturedCount];
                if (intParams != null)
                {
                    for (var index = 0; index < capturedCount; index++)
                        capturedParams[index] = intParams[index];
                }

                var samples = yieldObservation.InboundEventYieldSamples ?? [];
                var updatedSamples = samples.Length < MaximumYieldEventYieldSamples
                    ? [.. samples, new InboundEventYieldSample(
                        GetYieldMillisecondsAfterOutbound(),
                        eventId.Id,
                        scene,
                        yieldId,
                        intParamCount,
                        capturedParams)]
                    : samples;
                yieldObservation = yieldObservation with
                {
                    InboundEventYieldCount = yieldObservation.InboundEventYieldCount + 1,
                    InboundEventYieldSamples = updatedSamples,
                };
            }
        }
        catch
        {
            // Observation must never interfere with the client's inbound event path.
        }
    }

    private void ObserveYieldActorControl(
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
                if (activeYieldProbe is null ||
                    yieldObservation.State != YieldEventSceneProbeState.PacketSent)
                {
                    return;
                }

                var samples = yieldObservation.InboundActorControlSamples ?? [];
                var updatedSamples = samples.Length < MaximumYieldActorControlSamples
                    ? [.. samples, new InboundActorControlSample(
                        GetYieldMillisecondsAfterOutbound(),
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
                yieldObservation = yieldObservation with
                {
                    InboundActorControlCount = yieldObservation.InboundActorControlCount + 1,
                    InboundActorControlSamples = updatedSamples,
                };
            }
        }
        catch
        {
            // Observation must never interfere with the client's inbound control path.
        }
    }

    private void ObserveYieldRawPacket(uint targetId, nint packet)
    {
        try
        {
            lock (observationGate)
            {
                if (activeYieldProbe is null ||
                    yieldObservation.State != YieldEventSceneProbeState.PacketSent ||
                    packet == 0)
                {
                    return;
                }

                var samples = yieldObservation.InboundRawPacketSamples ?? [];
                var word0 = *(uint*)packet;
                var packetSize = (ushort)(word0 & 0xFFFF);
                var updatedSamples = samples.Length < MaximumYieldRawInboundSamples
                    ? [.. samples, new InboundRawPacketSample(
                        GetYieldMillisecondsAfterOutbound(),
                        targetId,
                        word0,
                        *((uint*)packet + 1),
                        *((uint*)packet + 2),
                        *((uint*)packet + 3),
                        packetSize >= 20 ? *((uint*)packet + 4) : 0,
                        packetSize,
                        (ushort)(word0 >> 16))]
                    : samples;
                yieldObservation = yieldObservation with
                {
                    InboundRawPacketCount = yieldObservation.InboundRawPacketCount + 1,
                    InboundRawPacketSamples = updatedSamples,
                };
            }
        }
        catch
        {
            // Observation must never interfere with the client's generic receive path.
        }
    }

    private double GetYieldMillisecondsAfterOutbound() =>
        yieldSentTimestamp == 0
            ? 0
            : Stopwatch.GetElapsedTime(yieldSentTimestamp).TotalMilliseconds;

    private YieldEventSceneProbeObservation FailYield(string message)
    {
        lock (observationGate)
        {
            activeYieldProbe = null;
            yieldObservation = YieldEventSceneProbeObservation.Idle with
            {
                State = YieldEventSceneProbeState.Failed,
                CachedTemplateAvailable = cachedYieldTemplate is not null,
                Message = message,
            };
            return yieldObservation;
        }
    }

    private sealed record YieldProbeContext(
        ulong ActorId,
        uint EventId,
        YieldEventSceneProbeMode Mode);

    private sealed record YieldEventScenePacketTemplate(
        ulong ActorId,
        uint EventId,
        uint Opcode,
        ushort SceneId,
        byte YieldId,
        uint Result0,
        uint Result1,
        ulong RetainerId,
        nint ZoneClientAddress,
        uint Argument3,
        uint Argument4,
        bool Argument5,
        byte[] PacketBytes,
        string PacketHex);
}

public enum YieldEventSceneProbeMode
{
    None,
    InSessionControl,
    SessionFreeReplay,
    NativeCallRetainer,
    NativeSelectRetainer,
}

public enum NativeRetainerVerb
{
    CallRetainer,
    SelectRetainer,
}

public enum YieldEventSceneProbeState
{
    Idle,
    AwaitingControlPacket,
    Sending,
    PacketSent,
    Stopped,
    Failed,
}

public sealed record YieldEventSceneProbeObservation(
    YieldEventSceneProbeState State,
    YieldEventSceneProbeMode Mode,
    ulong ActorId,
    uint EventId,
    uint Opcode,
    ushort SceneId,
    byte YieldId,
    uint Result0,
    uint Result1,
    bool Sent,
    bool ReplacedOriginal,
    bool MatchingEventPlayObserved,
    bool CachedTemplateAvailable,
    ulong RetainerId,
    ulong InboundEventObjectId,
    short InboundScene,
    ulong InboundSceneFlags,
    int InboundEventPlayCount,
    InboundEventPlaySample[]? InboundEventPlaySamples,
    int InboundEventYieldCount,
    InboundEventYieldSample[]? InboundEventYieldSamples,
    int InboundActorControlCount,
    InboundActorControlSample[]? InboundActorControlSamples,
    uint[]? InboundSceneData,
    string? OutboundPacketHex,
    string Message,
    int InboundRawPacketCount = 0,
    InboundRawPacketSample[]? InboundRawPacketSamples = null)
{
    public static YieldEventSceneProbeObservation Idle { get; } = new(
        YieldEventSceneProbeState.Idle,
        YieldEventSceneProbeMode.None,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        false,
        false,
        false,
        false,
        0,
        0,
        0,
        0,
        0,
        null,
        0,
        null,
        0,
        null,
        null,
        null,
        "The yield-event packet observer is idle.");
}
