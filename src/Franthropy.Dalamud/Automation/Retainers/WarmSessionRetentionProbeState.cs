namespace Franthropy.Dalamud.Automation.Retainers;

public enum WarmSessionRetentionProbeState
{
    Idle,
    AwaitingSelection,
    AwaitingTeardown,
    TeardownSuppressed,
    SendingReplay,
    ReplaySent,
    Scene2Observed,
    SendingRelease,
    ReleaseSent,
    Stopped,
    Failed,
}
