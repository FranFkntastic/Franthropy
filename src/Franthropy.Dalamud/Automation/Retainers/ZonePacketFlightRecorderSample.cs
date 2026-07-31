namespace Franthropy.Dalamud.Automation.Retainers;

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
