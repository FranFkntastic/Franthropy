using System.Diagnostics.CodeAnalysis;

namespace Franthropy.Dalamud.Automation.Ui;

/// <summary>
/// Builds the chat command used to request a saved gearset by its conventional
/// job-abbreviation name. Submission is not proof that the game changed jobs;
/// callers must verify the rendered job/equipment state afterward.
/// </summary>
public sealed record GearsetChangeCommand(
    string RequestedTarget,
    string JobName,
    string GearsetName,
    string Command)
{
    public static bool TryCreate(
        string? target,
        [NotNullWhen(true)] out GearsetChangeCommand? command)
    {
        command = NormalizeTarget(target) switch
        {
            "MIN" => Create(target!, "Miner", "MIN"),
            "BTN" => Create(target!, "Botanist", "BTN"),
            "BSM" => Create(target!, "Blacksmith", "BSM"),
            _ => null,
        };
        return command is not null;
    }

    private static GearsetChangeCommand Create(string requestedTarget, string jobName, string gearsetName) =>
        new(requestedTarget, jobName, gearsetName, $"/gearset change \"{gearsetName}\"");

    private static string NormalizeTarget(string? target) => target?.Trim().ToUpperInvariant() switch
    {
        "MINER" or "MIN" => "MIN",
        "BOTANIST" or "BTN" => "BTN",
        "BLACKSMITH" or "BSM" => "BSM",
        _ => string.Empty,
    };
}
