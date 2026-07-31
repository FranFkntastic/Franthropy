namespace Franthropy.Dalamud.Automation.Retainers;

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
