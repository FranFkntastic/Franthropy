namespace Franthropy.Dalamud.Travel;

public sealed record WorldTravelCommandResult(
    bool Success,
    string Message,
    string? Command = null)
{
    public static WorldTravelCommandResult Ok(string message, string command)
        => new(true, message, command);

    public static WorldTravelCommandResult Fail(string message, string? command = null)
        => new(false, message, command);
}
