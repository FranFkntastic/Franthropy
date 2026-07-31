using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Franthropy.Dalamud.Automation.Retainers;

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
