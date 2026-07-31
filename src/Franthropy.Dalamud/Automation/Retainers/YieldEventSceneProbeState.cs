namespace Franthropy.Dalamud.Automation.Retainers;

public enum YieldEventSceneProbeState
{
    Idle,
    AwaitingControlPacket,
    Sending,
    PacketSent,
    Stopped,
    Failed,
}
