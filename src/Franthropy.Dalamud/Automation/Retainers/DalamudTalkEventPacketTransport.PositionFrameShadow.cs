using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using FFXIVClientStructs.FFXIV.Application.Network;

namespace Franthropy.Dalamud.Automation.Retainers;

public sealed unsafe partial class DalamudTalkEventPacketTransport
{
    private const double PositionFrameShadowWindowMilliseconds = 25;

    private PositionFrameShadowSession? positionFrameShadowSession;

    public PositionFrameShadowObservation ArmPositionFrameShadow(
        Vector3 expectedPosition,
        Vector3 hypotheticalPosition,
        uint expectedOpcode = 0x1C8)
        => ArmPositionFrame(
            expectedPosition,
            hypotheticalPosition,
            PositionFrameShadowMode.PassThrough,
            expectedOpcode);

    public PositionFrameShadowObservation ArmPositionFrameSubstitution(
        Vector3 expectedPosition,
        Vector3 hypotheticalPosition,
        uint expectedOpcode = 0x1C8)
        => ArmPositionFrame(
            expectedPosition,
            hypotheticalPosition,
            PositionFrameShadowMode.SubstituteOnce,
            expectedOpcode);

    private PositionFrameShadowObservation ArmPositionFrame(
        Vector3 expectedPosition,
        Vector3 hypotheticalPosition,
        PositionFrameShadowMode mode,
        uint expectedOpcode)
    {
        lock (observationGate)
        {
            if (disposed)
                return PositionFrameShadowFailed("The packet transport has been disposed.");
            if (activeTarget is not { } target ||
                observation.State != TalkEventPacketTransportState.AwaitingBuilderPacket)
            {
                return PositionFrameShadowFailed(
                    "Arm the normal bell flight recorder before the position-frame shadow.");
            }
            if (positionFrameShadowSession is not null)
                return PositionFrameShadowFailed("Another position-frame shadow is already armed.");

            var armed = new PositionFrameShadowObservation(
                PositionFrameShadowState.AwaitingStartTalk,
                expectedOpcode,
                target.ActorId,
                target.EventId,
                PositionFrameShadowVector.From(expectedPosition),
                PositionFrameShadowVector.From(hypotheticalPosition),
                0,
                0,
                false,
                false,
                mode == PositionFrameShadowMode.SubstituteOnce
                    ? "Waiting for the exact stock StartTalkEvent; the first exact compact position frame will be substituted once."
                    : "Waiting for the exact stock StartTalkEvent; all outbound packets remain pass-through.",
                Mode: mode);
            positionFrameShadowSession = new(
                target,
                expectedPosition,
                hypotheticalPosition,
                expectedOpcode,
                mode);
            observation = observation with { PositionFrameShadow = armed };
            return armed;
        }
    }

    public PositionFrameShadowObservation ObservePositionFrameShadow()
    {
        lock (observationGate)
        {
            return observation.PositionFrameShadow ??
                new(
                    PositionFrameShadowState.Idle,
                    0,
                    0,
                    0,
                    new(0, 0, 0),
                    new(0, 0, 0),
                    0,
                    0,
                    false,
                    false,
                    "No position-frame shadow has been armed.");
        }
    }

    private PositionFrameShadowPendingCapture? TryObservePositionFrameShadowBeforeSend(nint packet)
    {
        lock (observationGate)
        {
            if (positionFrameShadowSession is not { } active || packet == 0)
                return null;

            try
            {
                var packetPointer = (byte*)packet;
                var encodedSize = *(ulong*)(packetPointer + 8);
                if (encodedSize > MaximumPlausiblePacketBytes - 0x10)
                {
                    if (active.StartTalkObservedTimestamp != 0)
                        AbortPositionFrameShadow(active, "The first post-StartTalk packet had an implausible encoded size; substitution was cancelled.");
                    return null;
                }
                var declaredSize = checked((int)encodedSize + 0x10);

                if (active.StartTalkObservedTimestamp == 0)
                {
                    if (declaredSize < PacketPayloadOffset + MinimumTalkPayloadLength)
                    {
                        IncrementPositionFrameShadowNonMatch(active);
                        return null;
                    }

                    var payload = packetPointer + PacketPayloadOffset;
                    if (*(ulong*)payload != active.Target.ActorId ||
                        *(uint*)(payload + 8) != active.Target.EventId)
                    {
                        IncrementPositionFrameShadowNonMatch(active);
                        return null;
                    }
                    var opcode = *(uint*)packetPointer;
                    if (opcode != active.ExpectedStartTalkOpcode)
                    {
                        AbortPositionFrameShadow(
                            active,
                            $"The exact bell actor/event appeared on opcode 0x{opcode:X}, not expected StartTalkEvent 0x{active.ExpectedStartTalkOpcode:X}; substitution was cancelled.");
                        return null;
                    }

                    active.StartTalkObservedTimestamp = Stopwatch.GetTimestamp();
                    observation = observation with
                    {
                        PositionFrameShadow = observation.PositionFrameShadow! with
                        {
                            State = PositionFrameShadowState.AwaitingPositionFrame,
                            Message = "Matched the exact stock StartTalkEvent; waiting 25 ms for its compact truthful position frame.",
                        },
                    };
                    return null;
                }

                var millisecondsAfterStartTalk =
                    Stopwatch.GetElapsedTime(active.StartTalkObservedTimestamp).TotalMilliseconds;
                if (millisecondsAfterStartTalk > PositionFrameShadowWindowMilliseconds)
                {
                    AbortPositionFrameShadow(
                        active,
                        "The first post-StartTalk packet arrived outside the 25-ms window; substitution was cancelled.",
                        PositionFrameShadowState.TimedOut,
                        millisecondsAfterStartTalk);
                    return null;
                }

                if (declaredSize != PositionFrameShadowAnalyzer.CompactPacketSize)
                {
                    AbortPositionFrameShadow(
                        active,
                        $"The first post-StartTalk packet had size 0x{declaredSize:X}, not exact compact size 0x{PositionFrameShadowAnalyzer.CompactPacketSize:X}; substitution was cancelled.",
                        millisecondsAfterStartTalk: millisecondsAfterStartTalk);
                    return null;
                }

                var packetBytes = new ReadOnlySpan<byte>(
                    packetPointer,
                    PositionFrameShadowAnalyzer.CompactPacketSize);
                var analysis = PositionFrameShadowAnalyzer.Analyze(
                    packetBytes,
                    active.ExpectedOpcode,
                    active.ExpectedPosition,
                    active.HypotheticalPosition);
                if (!analysis.Matched)
                {
                    AbortPositionFrameShadow(
                        active,
                        $"{analysis.Message} This was the first post-StartTalk packet, so substitution was cancelled.",
                        millisecondsAfterStartTalk: millisecondsAfterStartTalk,
                        analysis: analysis);
                    return null;
                }
                if (!active.OneShot.TryConsume())
                {
                    AbortPositionFrameShadow(
                        active,
                        "The position-frame one-shot had already been consumed; the packet remained pass-through.",
                        millisecondsAfterStartTalk: millisecondsAfterStartTalk,
                        analysis: analysis);
                    return null;
                }

                var replacementPacket = active.Mode == PositionFrameShadowMode.SubstituteOnce
                    ? Convert.FromHexString(analysis.HypotheticalPacketHex)
                    : null;
                observation = observation with
                {
                    PositionFrameShadow = observation.PositionFrameShadow! with
                    {
                        State = PositionFrameShadowState.CapturedPendingSend,
                        MillisecondsAfterStartTalk = millisecondsAfterStartTalk,
                        Analysis = analysis,
                        Message = replacementPacket is null
                            ? "Captured an owned hypothetical clone; the original packet is still awaiting its unchanged stock send."
                            : "Matched and consumed the exact one-shot; the owned hypothetical clone is awaiting one stock send.",
                    },
                };
                return new(active, analysis.OriginalSha256, replacementPacket);
            }
            catch (Exception exception)
            {
                AbortPositionFrameShadow(
                    active,
                    $"Position-frame inspection failed ({exception.GetType().Name}); substitution was cancelled.");
                return null;
            }
        }
    }

    private bool SendPositionFramePacket(
        ZoneClient* zoneClient,
        PositionFrameShadowPendingCapture? pending,
        nint originalPacket,
        uint argument3,
        uint argument4,
        bool argument5)
    {
        if (pending?.ReplacementPacket is not { } replacement)
            return sendPacketHook.Original(zoneClient, originalPacket, argument3, argument4, argument5);

        fixed (byte* replacementPointer = replacement)
        {
            return sendPacketHook.Original(
                zoneClient,
                (nint)replacementPointer,
                argument3,
                argument4,
                argument5);
        }
    }

    private void AppendPositionFrameOutboundFlightRecorderSample(
        PositionFrameShadowPendingCapture? pending,
        nint originalPacket,
        uint argument3,
        uint argument4,
        bool argument5)
    {
        if (pending?.ReplacementPacket is not { } replacement)
        {
            AppendOutboundFlightRecorderSample(originalPacket, argument3, argument4, argument5);
            return;
        }

        fixed (byte* replacementPointer = replacement)
        {
            AppendOutboundFlightRecorderSample(
                (nint)replacementPointer,
                argument3,
                argument4,
                argument5);
        }
    }

    private void CompletePositionFrameShadowAfterSend(
        PositionFrameShadowPendingCapture? pending,
        nint originalPacket,
        bool accepted)
    {
        if (pending is null)
            return;

        lock (observationGate)
        {
            if (positionFrameShadowSession != pending.Session ||
                observation.PositionFrameShadow is not { } current)
                return;

            var originalUnchanged = false;
            try
            {
                var bytes = new ReadOnlySpan<byte>(
                    (void*)originalPacket,
                    PositionFrameShadowAnalyzer.CompactPacketSize);
                originalUnchanged =
                    string.Equals(
                        pending.OriginalSha256,
                        Convert.ToHexString(SHA256.HashData(bytes)),
                        StringComparison.Ordinal);
            }
            catch
            {
                // The post-send proof remains false if the source buffer cannot be re-read.
            }

            observation = observation with
            {
                PositionFrameShadow = current with
                {
                    State = PositionFrameShadowState.Captured,
                    OriginalSendAccepted = accepted,
                    OriginalBufferUnchanged = originalUnchanged,
                    ReplacementTransmitted = pending.ReplacementPacket is not null,
                    TransmittedSha256 = pending.ReplacementPacket is null
                        ? pending.OriginalSha256
                        : current.Analysis?.HypotheticalSha256,
                    Message = pending.ReplacementPacket is null
                        ? "Shadow proof complete: the hypothetical clone is owned locally, and the stock send received the original buffer unchanged."
                        : "One exact hypothetical position frame was passed to the stock send; the truthful source buffer remained unchanged.",
                },
            };
            positionFrameShadowSession = null;
        }
    }

    private void IncrementPositionFrameShadowNonMatch(PositionFrameShadowSession active)
    {
        active.NonMatchingOutboundPacketCount++;
        if (observation.PositionFrameShadow is { } current)
        {
            observation = observation with
            {
                PositionFrameShadow = current with
                {
                    NonMatchingOutboundPacketCount = active.NonMatchingOutboundPacketCount,
                },
            };
        }
    }

    private void CancelPositionFrameShadow(string reason)
    {
        lock (observationGate)
        {
            positionFrameShadowSession = null;
            if (observation.PositionFrameShadow is { Armed: true } current)
            {
                observation = observation with
                {
                    PositionFrameShadow = current with
                    {
                        State = PositionFrameShadowState.Cancelled,
                        Message = reason,
                    },
                };
            }
        }
    }

    private void AbortPositionFrameShadow(
        PositionFrameShadowSession active,
        string reason,
        PositionFrameShadowState state = PositionFrameShadowState.Cancelled,
        double millisecondsAfterStartTalk = 0,
        PositionFrameShadowAnalysis? analysis = null)
    {
        if (positionFrameShadowSession != active)
            return;

        positionFrameShadowSession = null;
        if (observation.PositionFrameShadow is not { } current)
            return;

        active.NonMatchingOutboundPacketCount++;
        observation = observation with
        {
            PositionFrameShadow = current with
            {
                State = state,
                NonMatchingOutboundPacketCount = active.NonMatchingOutboundPacketCount,
                MillisecondsAfterStartTalk = millisecondsAfterStartTalk,
                Analysis = analysis,
                Message = reason,
            },
        };
    }

    private PositionFrameShadowObservation PositionFrameShadowFailed(string message) =>
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
            message);

    private sealed class PositionFrameShadowSession(
        CaptureTarget target,
        Vector3 expectedPosition,
        Vector3 hypotheticalPosition,
        uint expectedOpcode,
        PositionFrameShadowMode mode)
    {
        public CaptureTarget Target { get; } = target;
        public Vector3 ExpectedPosition { get; } = expectedPosition;
        public Vector3 HypotheticalPosition { get; } = hypotheticalPosition;
        public uint ExpectedOpcode { get; } = expectedOpcode;
        public uint ExpectedStartTalkOpcode { get; } = 0x259;
        public PositionFrameShadowMode Mode { get; } = mode;
        public PositionFrameOneShotLatch OneShot { get; } = new();
        public long StartTalkObservedTimestamp { get; set; }
        public int NonMatchingOutboundPacketCount { get; set; }
    }

    private sealed record PositionFrameShadowPendingCapture(
        PositionFrameShadowSession Session,
        string OriginalSha256,
        byte[]? ReplacementPacket);
}
