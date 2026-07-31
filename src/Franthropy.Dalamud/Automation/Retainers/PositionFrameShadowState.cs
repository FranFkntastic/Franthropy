namespace Franthropy.Dalamud.Automation.Retainers;

public enum PositionFrameShadowState
{
    Idle,
    AwaitingStartTalk,
    AwaitingPositionFrame,
    CapturedPendingSend,
    Captured,
    TimedOut,
    Cancelled,
}
