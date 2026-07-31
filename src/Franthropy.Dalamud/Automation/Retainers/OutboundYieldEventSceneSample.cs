namespace Franthropy.Dalamud.Automation.Retainers;

public sealed record OutboundYieldEventSceneSample(
    double MillisecondsAfterReplay,
    uint Opcode,
    uint EventId,
    ushort SceneId,
    byte YieldId,
    byte ResultCount,
    ulong RetainerId);
