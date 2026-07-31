namespace Franthropy.Dalamud.Automation.Retainers;

public sealed record InboundEventPlaySample(
    double MillisecondsAfterOutbound,
    ulong ObjectId,
    uint EventId,
    short Scene,
    ulong SceneFlags,
    byte SceneDataCount,
    uint[] SceneData);
