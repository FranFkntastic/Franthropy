namespace Franthropy.Dalamud.Automation.Retainers;

public sealed record PositionFrameShadowAnalysis(
    bool Matched,
    string Message,
    uint Opcode,
    int DeclaredSize,
    PositionFrameShadowVector OriginalPosition,
    PositionFrameShadowVector HypotheticalPosition,
    string OriginalSha256,
    string HypotheticalSha256,
    string OriginalPacketHex,
    string HypotheticalPacketHex,
    bool BytesOutsidePositionFieldsUnchanged);
