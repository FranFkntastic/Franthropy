namespace Franthropy.Dalamud.Automation.Retainers;

public sealed record InboundActorControlSample(
    double MillisecondsAfterOutbound,
    uint EntityId,
    uint Category,
    uint Arg1,
    uint Arg2,
    uint Arg3,
    uint Arg4,
    uint Arg5,
    uint Arg6,
    uint Arg7,
    uint Arg8,
    ulong TargetId,
    bool IsRecorded);
