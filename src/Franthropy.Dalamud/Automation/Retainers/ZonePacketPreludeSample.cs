namespace Franthropy.Dalamud.Automation.Retainers;

public sealed record ZonePacketPreludeSample(
    double MillisecondsAfterArm,
    string Direction,
    uint Opcode,
    int DeclaredSize,
    uint TargetId,
    uint Argument3,
    uint Argument4,
    bool Argument5,
    bool Truncated,
    string PacketHex);
