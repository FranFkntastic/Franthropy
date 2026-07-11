namespace Franthropy.Dalamud.Characters;

public sealed record CharacterScope(ulong LocalContentId, string Name, uint HomeWorldId);

public sealed record CharacterIdentitySnapshot(
    CharacterScope? Scope,
    uint? CurrentWorldId,
    uint? ActiveClassJobId,
    DateTimeOffset CapturedAt,
    bool IsLoggedIn,
    SnapshotComponentStatus Status,
    string? Diagnostic = null);

public sealed record CharacterJobSnapshot(
    uint ClassJobId,
    string Abbreviation,
    string Name,
    uint Level,
    bool? IsUnlocked,
    uint? ParentClassJobId,
    string? Role);

public enum SnapshotComponentStatus
{
    Complete,
    Partial,
    Unavailable,
}

