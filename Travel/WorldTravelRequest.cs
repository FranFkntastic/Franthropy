namespace Franthropy.Dalamud.Travel;

public sealed record WorldTravelRequest(
    string TargetWorld,
    string CurrentWorld,
    string Command,
    bool IsCurrentWorld);
