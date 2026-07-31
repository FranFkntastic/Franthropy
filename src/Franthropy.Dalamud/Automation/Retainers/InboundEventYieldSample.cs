namespace Franthropy.Dalamud.Automation.Retainers;

public sealed record InboundEventYieldSample(
    double MillisecondsAfterOutbound,
    uint EventId,
    short Scene,
    byte YieldId,
    byte IntParamCount,
    int[] IntParams);
