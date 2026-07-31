namespace Franthropy.Dalamud.Automation.Retainers;

public sealed record InboundEventTerminationSample(
    double MillisecondsAfterOutbound,
    uint EventId,
    ulong ActorId,
    byte EventType,
    uint Detail,
    uint Extra);
