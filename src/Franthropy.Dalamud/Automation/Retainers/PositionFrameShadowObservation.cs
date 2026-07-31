namespace Franthropy.Dalamud.Automation.Retainers;

public sealed record PositionFrameShadowObservation(
    PositionFrameShadowState State,
    uint ExpectedOpcode,
    ulong ActorId,
    uint EventId,
    PositionFrameShadowVector ExpectedPosition,
    PositionFrameShadowVector HypotheticalPosition,
    int NonMatchingOutboundPacketCount,
    double MillisecondsAfterStartTalk,
    bool OriginalSendAccepted,
    bool OriginalBufferUnchanged,
    string Message,
    PositionFrameShadowAnalysis? Analysis = null,
    PositionFrameShadowMode Mode = PositionFrameShadowMode.PassThrough,
    bool ReplacementTransmitted = false,
    string? TransmittedSha256 = null)
{
    public bool Armed =>
        State is PositionFrameShadowState.AwaitingStartTalk or
            PositionFrameShadowState.AwaitingPositionFrame or
            PositionFrameShadowState.CapturedPendingSend;

    public bool Captured => State == PositionFrameShadowState.Captured;
}
