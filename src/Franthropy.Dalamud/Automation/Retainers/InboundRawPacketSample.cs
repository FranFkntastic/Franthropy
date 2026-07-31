namespace Franthropy.Dalamud.Automation.Retainers;

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
