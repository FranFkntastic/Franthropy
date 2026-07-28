using System.Buffers.Binary;
using System.Numerics;
using Franthropy.Dalamud.Automation.Retainers;

namespace Franthropy.Dalamud.Tests.Automation.Retainers;

public sealed class PositionFrameShadowAnalyzerTests
{
    [Fact]
    public void Analyze_ClonesOnlyPositionFields_AndLeavesSourceUntouched()
    {
        var truthful = new Vector3(-747.6621f, 23.999996f, -556.32623f);
        var hypothetical = new Vector3(-750.25f, 24.5f, -560.75f);
        var packet = CreateCompactFrame(0x2C6, truthful);
        var before = packet.ToArray();

        var result = PositionFrameShadowAnalyzer.Analyze(
            packet,
            0x2C6,
            truthful,
            hypothetical);

        Assert.True(result.Matched);
        Assert.True(result.BytesOutsidePositionFieldsUnchanged);
        Assert.Equal(before, packet);
        Assert.NotEqual(result.OriginalSha256, result.HypotheticalSha256);

        var shadow = Convert.FromHexString(result.HypotheticalPacketHex);
        Assert.Equal(hypothetical.X, ReadSingle(shadow, PositionFrameShadowAnalyzer.PositionXOffset));
        Assert.Equal(hypothetical.Y, ReadSingle(shadow, PositionFrameShadowAnalyzer.PositionYOffset));
        Assert.Equal(hypothetical.Z, ReadSingle(shadow, PositionFrameShadowAnalyzer.PositionZOffset));
        for (var index = 0; index < packet.Length; index++)
        {
            if (index >= PositionFrameShadowAnalyzer.PositionXOffset &&
                index < PositionFrameShadowAnalyzer.PositionZOffset + sizeof(float))
            {
                continue;
            }

            Assert.Equal(packet[index], shadow[index]);
        }
    }

    [Fact]
    public void Analyze_OwnedCloneSurvivesSourceReuse()
    {
        var truthful = new Vector3(1, 2, 3);
        var hypothetical = new Vector3(4, 5, 6);
        var packet = CreateCompactFrame(0x2C6, truthful);

        var result = PositionFrameShadowAnalyzer.Analyze(
            packet,
            0x2C6,
            truthful,
            hypothetical);
        var capturedShadow = result.HypotheticalPacketHex;

        Array.Fill(packet, (byte)0xCC);

        Assert.True(result.Matched);
        Assert.Equal(capturedShadow, result.HypotheticalPacketHex);
        Assert.Equal(
            hypothetical.X,
            ReadSingle(
                Convert.FromHexString(result.HypotheticalPacketHex),
                PositionFrameShadowAnalyzer.PositionXOffset));
    }

    [Fact]
    public void Analyze_RejectsUnrelatedOpcode()
    {
        var truthful = new Vector3(1, 2, 3);
        var packet = CreateCompactFrame(0x37C, truthful);

        var result = PositionFrameShadowAnalyzer.Analyze(
            packet,
            0x2C6,
            truthful,
            new Vector3(4, 5, 6));

        Assert.False(result.Matched);
        Assert.Equal(result.OriginalPacketHex, result.HypotheticalPacketHex);
        Assert.Equal(result.OriginalSha256, result.HypotheticalSha256);
    }

    [Fact]
    public void Analyze_RejectsDifferentTruthfulPosition()
    {
        var packet = CreateCompactFrame(0x2C6, new Vector3(10, 20, 30));

        var result = PositionFrameShadowAnalyzer.Analyze(
            packet,
            0x2C6,
            new Vector3(11, 20, 30),
            new Vector3(4, 5, 6));

        Assert.False(result.Matched);
        Assert.Contains("does not match", result.Message);
    }

    [Fact]
    public void Analyze_RejectsWrongCompactSize()
    {
        var packet = CreateCompactFrame(0x2C6, new Vector3(1, 2, 3));
        BinaryPrimitives.WriteUInt64LittleEndian(
            packet.AsSpan(PositionFrameShadowAnalyzer.EncodedSizeOffset),
            PositionFrameShadowAnalyzer.CompactPacketSize - 0x10 + 4);

        var result = PositionFrameShadowAnalyzer.Analyze(
            packet,
            0x2C6,
            new Vector3(1, 2, 3),
            new Vector3(4, 5, 6));

        Assert.False(result.Matched);
        Assert.Contains("declared size", result.Message);
    }

    [Fact]
    public void OneShotLatch_CanBeConsumedExactlyOnce()
    {
        var latch = new PositionFrameOneShotLatch();

        Assert.True(latch.TryConsume());
        Assert.False(latch.TryConsume());
        Assert.False(latch.TryConsume());
    }

    private static byte[] CreateCompactFrame(uint opcode, Vector3 position)
    {
        var packet = new byte[PositionFrameShadowAnalyzer.CompactPacketSize];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, opcode);
        BinaryPrimitives.WriteUInt64LittleEndian(
            packet.AsSpan(PositionFrameShadowAnalyzer.EncodedSizeOffset),
            PositionFrameShadowAnalyzer.CompactPacketSize - 0x10);
        WriteSingle(packet, PositionFrameShadowAnalyzer.PositionXOffset, position.X);
        WriteSingle(packet, PositionFrameShadowAnalyzer.PositionYOffset, position.Y);
        WriteSingle(packet, PositionFrameShadowAnalyzer.PositionZOffset, position.Z);
        return packet;
    }

    private static float ReadSingle(ReadOnlySpan<byte> packet, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(packet[offset..]));

    private static void WriteSingle(Span<byte> packet, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(packet[offset..], BitConverter.SingleToInt32Bits(value));
}
