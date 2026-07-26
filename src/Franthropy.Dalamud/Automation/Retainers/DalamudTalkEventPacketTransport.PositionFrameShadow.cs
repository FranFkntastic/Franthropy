using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;

namespace Franthropy.Dalamud.Automation.Retainers;

public enum PositionFrameShadowState
{
    Idle,
    AwaitingStartTalk,
    AwaitingPositionFrame,
    CapturedPendingSend,
    Captured,
    TimedOut,
    Cancelled,
}

public sealed record PositionFrameShadowObservation(
    PositionFrameShadowState State,
    uint ExpectedOpcode,
    ulong ActorId,
    uint EventId,
    PositionFrameShadowVector ExpectedPosition,
    PositionFrameShadowVector HypotheticalPosition,
    int NonMatchingOutboundPacketCount,
    double MillisecondsAfterStartTalk,
    bool OriginalSendAccepted,
    bool OriginalBufferUnchanged,
    string Message,
    PositionFrameShadowAnalysis? Analysis = null)
{
    public bool Armed =>
        State is PositionFrameShadowState.AwaitingStartTalk or
            PositionFrameShadowState.AwaitingPositionFrame or
            PositionFrameShadowState.CapturedPendingSend;

    public bool Captured => State == PositionFrameShadowState.Captured;
}

public sealed record PositionFrameShadowAnalysis(
    bool Matched,
    string Message,
    uint Opcode,
    int DeclaredSize,
    PositionFrameShadowVector OriginalPosition,
    PositionFrameShadowVector HypotheticalPosition,
    string OriginalSha256,
    string HypotheticalSha256,
    string OriginalPacketHex,
    string HypotheticalPacketHex,
    bool BytesOutsidePositionFieldsUnchanged);

public sealed record PositionFrameShadowVector(float X, float Y, float Z)
{
    public static PositionFrameShadowVector From(Vector3 value) =>
        new(value.X, value.Y, value.Z);
}

public static class PositionFrameShadowAnalyzer
{
    public const int CompactPacketSize = 0x38;
    public const int EncodedSizeOffset = 0x08;
    public const int PositionXOffset = 0x28;
    public const int PositionYOffset = 0x2C;
    public const int PositionZOffset = 0x30;
    public const float PositionTolerance = 0.02f;

    public static PositionFrameShadowAnalysis Analyze(
        ReadOnlySpan<byte> packet,
        uint expectedOpcode,
        Vector3 expectedPosition,
        Vector3 hypotheticalPosition)
    {
        if (packet.Length < CompactPacketSize)
        {
            return NotMatched(
                packet,
                $"Packet length {packet.Length} is smaller than the compact position-frame envelope.");
        }

        var opcode = BinaryPrimitives.ReadUInt32LittleEndian(packet);
        var encodedSize = BinaryPrimitives.ReadUInt64LittleEndian(packet[EncodedSizeOffset..]);
        var declaredSize = encodedSize > int.MaxValue - 0x10
            ? int.MaxValue
            : (int)encodedSize + 0x10;
        if (opcode != expectedOpcode || declaredSize != CompactPacketSize)
        {
            return NotMatched(
                packet[..Math.Min(packet.Length, CompactPacketSize)],
                $"Packet 0x{opcode:X} with declared size {declaredSize} is not the expected compact frame 0x{expectedOpcode:X}.",
                opcode,
                declaredSize);
        }

        var originalPosition = new Vector3(
            ReadSingle(packet, PositionXOffset),
            ReadSingle(packet, PositionYOffset),
            ReadSingle(packet, PositionZOffset));
        if (Vector3.Distance(originalPosition, expectedPosition) > PositionTolerance)
        {
            return NotMatched(
                packet[..CompactPacketSize],
                $"Compact frame position {Format(originalPosition)} does not match the armed truthful position {Format(expectedPosition)}.",
                opcode,
                declaredSize,
                originalPosition,
                hypotheticalPosition);
        }

        var original = packet[..CompactPacketSize].ToArray();
        var hypothetical = original.ToArray();
        WriteSingle(hypothetical, PositionXOffset, hypotheticalPosition.X);
        WriteSingle(hypothetical, PositionYOffset, hypotheticalPosition.Y);
        WriteSingle(hypothetical, PositionZOffset, hypotheticalPosition.Z);

        var outsideFieldsUnchanged = true;
        for (var index = 0; index < original.Length; index++)
        {
            if (index >= PositionXOffset && index < PositionZOffset + sizeof(float))
                continue;
            if (original[index] != hypothetical[index])
            {
                outsideFieldsUnchanged = false;
                break;
            }
        }

        return new(
            true,
            "Matched the compact truthful position frame and produced an owned hypothetical clone without editing the source buffer.",
            opcode,
            declaredSize,
            PositionFrameShadowVector.From(originalPosition),
            PositionFrameShadowVector.From(hypotheticalPosition),
            Hash(original),
            Hash(hypothetical),
            Convert.ToHexString(original),
            Convert.ToHexString(hypothetical),
            outsideFieldsUnchanged);
    }

    private static PositionFrameShadowAnalysis NotMatched(
        ReadOnlySpan<byte> packet,
        string message,
        uint opcode = 0,
        int declaredSize = 0,
        Vector3 originalPosition = default,
        Vector3 hypotheticalPosition = default)
    {
        var bytes = packet.ToArray();
        var hash = Hash(bytes);
        var hex = Convert.ToHexString(bytes);
        return new(
            false,
            message,
            opcode,
            declaredSize,
            PositionFrameShadowVector.From(originalPosition),
            PositionFrameShadowVector.From(hypotheticalPosition),
            hash,
            hash,
            hex,
            hex,
            true);
    }

    private static float ReadSingle(ReadOnlySpan<byte> bytes, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]));

    private static void WriteSingle(Span<byte> bytes, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], BitConverter.SingleToInt32Bits(value));

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static string Format(Vector3 value) =>
        $"({value.X:F6},{value.Y:F6},{value.Z:F6})";
}

public sealed unsafe partial class DalamudTalkEventPacketTransport
{
    private const double PositionFrameShadowWindowMilliseconds = 25;

    private PositionFrameShadowSession? positionFrameShadowSession;

    public PositionFrameShadowObservation ArmPositionFrameShadow(
        Vector3 expectedPosition,
        Vector3 hypotheticalPosition,
        uint expectedOpcode = 0x2C6)
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
                "Waiting for the exact stock StartTalkEvent; all outbound packets remain pass-through.");
            positionFrameShadowSession = new(target, expectedPosition, hypotheticalPosition, expectedOpcode);
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
                    return null;
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
                    observation = observation with
                    {
                        PositionFrameShadow = observation.PositionFrameShadow! with
                        {
                            State = PositionFrameShadowState.TimedOut,
                            MillisecondsAfterStartTalk = millisecondsAfterStartTalk,
                            Message = "No matching compact position frame appeared inside the 25-ms shadow window; no packet was altered.",
                        },
                    };
                    positionFrameShadowSession = null;
                    return null;
                }

                if (declaredSize < PositionFrameShadowAnalyzer.CompactPacketSize)
                {
                    IncrementPositionFrameShadowNonMatch(active);
                    return null;
                }

                var packetBytes = new ReadOnlySpan<byte>(
                    packetPointer,
                    Math.Min(declaredSize, PositionFrameShadowAnalyzer.CompactPacketSize));
                var analysis = PositionFrameShadowAnalyzer.Analyze(
                    packetBytes,
                    active.ExpectedOpcode,
                    active.ExpectedPosition,
                    active.HypotheticalPosition);
                if (!analysis.Matched)
                {
                    IncrementPositionFrameShadowNonMatch(active);
                    return null;
                }

                observation = observation with
                {
                    PositionFrameShadow = observation.PositionFrameShadow! with
                    {
                        State = PositionFrameShadowState.CapturedPendingSend,
                        MillisecondsAfterStartTalk = millisecondsAfterStartTalk,
                        Analysis = analysis,
                        Message = "Captured an owned hypothetical clone; the original packet is still awaiting its unchanged stock send.",
                    },
                };
                return new(active, analysis.OriginalSha256);
            }
            catch
            {
                IncrementPositionFrameShadowNonMatch(active);
                return null;
            }
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
                    Message =
                        "Shadow proof complete: the hypothetical clone is owned locally, and the stock send received the original buffer unchanged.",
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
        uint expectedOpcode)
    {
        public CaptureTarget Target { get; } = target;
        public Vector3 ExpectedPosition { get; } = expectedPosition;
        public Vector3 HypotheticalPosition { get; } = hypotheticalPosition;
        public uint ExpectedOpcode { get; } = expectedOpcode;
        public long StartTalkObservedTimestamp { get; set; }
        public int NonMatchingOutboundPacketCount { get; set; }
    }

    private sealed record PositionFrameShadowPendingCapture(
        PositionFrameShadowSession Session,
        string OriginalSha256);
}
