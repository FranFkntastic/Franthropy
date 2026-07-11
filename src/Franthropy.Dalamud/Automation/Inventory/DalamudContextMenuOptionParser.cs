using System.Collections.Generic;

namespace Franthropy.Dalamud.Automation.Inventory;

public sealed record DalamudContextMenuOptionSpec(string SemanticName, IReadOnlySet<string> AcceptedLabels);

public sealed record DalamudContextMenuOptionMatch(bool Success, int Index, string? Label, string Code)
{
    public static DalamudContextMenuOptionMatch Missing() => new(false, -1, null, "OptionMissing");
    public static DalamudContextMenuOptionMatch Ambiguous() => new(false, -1, null, "OptionAmbiguous");
    public static DalamudContextMenuOptionMatch Found(int index, string label) => new(true, index, label, "OptionFound");
}

public static class DalamudContextMenuOptionParser
{
    public static DalamudContextMenuOptionMatch Find(
        IReadOnlyList<string> labels,
        DalamudContextMenuOptionSpec option)
    {
        var matches = new List<(int Index, string Label)>();
        for (var index = 0; index < labels.Count; index++)
        {
            var normalized = Normalize(labels[index]);
            if (option.AcceptedLabels.Any(label => Normalize(label).Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                matches.Add((index, labels[index]));
        }

        return matches.Count switch
        {
            0 => DalamudContextMenuOptionMatch.Missing(),
            1 => DalamudContextMenuOptionMatch.Found(matches[0].Index, matches[0].Label),
            _ => DalamudContextMenuOptionMatch.Ambiguous(),
        };
    }

    private static string Normalize(string value) => value.Trim();
}
