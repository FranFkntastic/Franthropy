using Franthropy.Dalamud.Automation.Retainers;

namespace Franthropy.Dalamud.Tests.Automation.Retainers;

public sealed class YieldEventScene2PacketCodecTests
{
    private const string CapturedPacketHex =
        "C903000000000000200000000000000001000000000000000000000000000000" +
        "20020B000000000217007800A10BC34A";
    private const string QuitRetainerPacketHex =
        "C903000000000000200000000000000001000000000000000000000000000000" +
        "20020B00020000000000000000000000";
    private const string CloseBellPacketHex =
        "C903000000000000200000000000000001000000000000000000000000000000" +
        "20020B00010000000600C01800FB9031";

    [Fact]
    public void TryDecode_DecodesCurrentBuildCapturedPacket()
    {
        var packet = Convert.FromHexString(CapturedPacketHex);

        var decoded = YieldEventScene2PacketCodec.TryDecode(
            packet,
            0x000B0220,
            out var fields);

        Assert.True(decoded);
        Assert.Equal(0x000003C9u, fields.Opcode);
        Assert.Equal(48, fields.DeclaredSize);
        Assert.Equal(0x000B0220u, fields.EventId);
        Assert.Equal((ushort)0, fields.SceneId);
        Assert.Equal((byte)0, fields.YieldId);
        Assert.Equal((byte)2, fields.ResultCount);
        Assert.Equal(0x00780017u, fields.Result0);
        Assert.Equal(0x4AC30BA1u, fields.Result1);
        Assert.Equal(0x007800174AC30BA1ul, fields.RetainerId);
    }

    [Fact]
    public void TryDecode_RejectsDifferentEvent()
    {
        var packet = Convert.FromHexString(CapturedPacketHex);

        var decoded = YieldEventScene2PacketCodec.TryDecode(
            packet,
            0x000B0221,
            out _);

        Assert.False(decoded);
    }

    [Fact]
    public void TryDecode_RejectsDifferentScene()
    {
        var packet = Convert.FromHexString(CapturedPacketHex);
        packet[36] = 2;

        var decoded = YieldEventScene2PacketCodec.TryDecode(
            packet,
            0x000B0220,
            out _);

        Assert.False(decoded);
    }

    [Fact]
    public void TryDecode_RejectsDifferentResultCount()
    {
        var packet = Convert.FromHexString(CapturedPacketHex);
        packet[39] = 1;

        var decoded = YieldEventScene2PacketCodec.TryDecode(
            packet,
            0x000B0220,
            out _);

        Assert.False(decoded);
    }

    [Fact]
    public void TryDecode_RejectsTruncatedPacket()
    {
        var packet = Convert.FromHexString(CapturedPacketHex);

        var decoded = YieldEventScene2PacketCodec.TryDecode(
            packet.AsSpan(0, packet.Length - 1),
            0x000B0220,
            out _);

        Assert.False(decoded);
    }

    [Theory]
    [InlineData(QuitRetainerPacketHex, 2)]
    [InlineData(CloseBellPacketHex, 1)]
    public void TryDecodeEnvelope_DecodesZeroResultLifecyclePackets(
        string packetHex,
        ushort expectedScene)
    {
        var decoded = YieldEventScene2PacketCodec.TryDecodeEnvelope(
            Convert.FromHexString(packetHex),
            0x000B0220,
            out var fields);

        Assert.True(decoded);
        Assert.Equal(0x000003C9u, fields.Opcode);
        Assert.Equal(expectedScene, fields.SceneId);
        Assert.Equal((byte)0, fields.YieldId);
        Assert.Equal((byte)0, fields.ResultCount);
    }
}
