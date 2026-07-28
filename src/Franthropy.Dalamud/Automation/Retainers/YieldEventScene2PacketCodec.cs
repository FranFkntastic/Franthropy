using System.Buffers.Binary;

namespace Franthropy.Dalamud.Automation.Retainers;

internal static class YieldEventScene2PacketCodec
{
    internal const int HeaderSize = 0x20;
    internal const int PayloadSize = 16;
    internal const int MinimumDeclaredPacketSize = HeaderSize + PayloadSize;
    internal const byte ExpectedResultCount = 2;

    internal static bool TryDecode(
        ReadOnlySpan<byte> packet,
        uint expectedEventId,
        out YieldEventScene2PacketFields fields)
    {
        if (!TryDecodeEnvelope(packet, expectedEventId, out fields))
            return false;

        return fields.SceneId == 0 &&
               fields.ResultCount == ExpectedResultCount;
    }

    internal static bool TryDecodeEnvelope(
        ReadOnlySpan<byte> packet,
        uint expectedEventId,
        out YieldEventScene2PacketFields fields)
    {
        fields = default;
        if (packet.Length < MinimumDeclaredPacketSize)
            return false;

        var encodedSize = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(8, 8));
        if (encodedSize < 0x10 + PayloadSize ||
            encodedSize > int.MaxValue - 0x10)
        {
            return false;
        }

        var declaredSize = checked((int)encodedSize + 0x10);
        if (declaredSize > packet.Length)
            return false;

        var payload = packet.Slice(HeaderSize, PayloadSize);
        var eventId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var sceneId = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2));
        var yieldId = payload[6];
        var resultCount = payload[7];
        if (eventId != expectedEventId)
            return false;

        var result0 = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4));
        var result1 = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(12, 4));
        fields = new(
            BinaryPrimitives.ReadUInt32LittleEndian(packet),
            declaredSize,
            eventId,
            sceneId,
            yieldId,
            resultCount,
            result0,
            result1,
            ((ulong)result0 << 32) | result1);
        return true;
    }
}

internal readonly record struct YieldEventScene2PacketFields(
    uint Opcode,
    int DeclaredSize,
    uint EventId,
    ushort SceneId,
    byte YieldId,
    byte ResultCount,
    uint Result0,
    uint Result1,
    ulong RetainerId);
