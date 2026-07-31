using System.Text.RegularExpressions;

namespace Franthropy.Dalamud.Tests.Automation.Retainers;

public sealed partial class RetainerTransportSourceOwnershipTests
{
    [Fact]
    public void TransportFiles_DeclareOnlyTheirNamedOwner()
    {
        var sourceDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Franthropy.Dalamud",
            "Automation",
            "Retainers");
        var transportFiles = Directory.GetFiles(
                sourceDirectory,
                "*.cs",
                SearchOption.TopDirectoryOnly)
            .Where(IsTransportComponentFile)
            .ToArray();

        Assert.NotEmpty(transportFiles);
        foreach (var file in transportFiles)
        {
            var fileOwner = Path.GetFileNameWithoutExtension(file);
            var expectedOwner = fileOwner.StartsWith(
                "DalamudTalkEventPacketTransport",
                StringComparison.Ordinal)
                ? "DalamudTalkEventPacketTransport"
                : fileOwner;
            var topLevelTypes = TopLevelTypeDeclaration()
                .Matches(File.ReadAllText(file))
                .Select(match => match.Groups["name"].Value)
                .ToArray();

            Assert.Equal(
                [expectedOwner],
                topLevelTypes);
        }
    }

    private static bool IsTransportComponentFile(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        return name.StartsWith("DalamudTalkEventPacketTransport", StringComparison.Ordinal) ||
               name.StartsWith("TalkEventPacketTransport", StringComparison.Ordinal) ||
               name.StartsWith("Inbound", StringComparison.Ordinal) ||
               name.StartsWith("ZonePacket", StringComparison.Ordinal) ||
               name.StartsWith("PositionFrame", StringComparison.Ordinal) ||
               name.StartsWith("WarmSession", StringComparison.Ordinal) ||
               name.StartsWith("OutboundYieldEventScene", StringComparison.Ordinal) ||
               name.StartsWith("YieldEventScene", StringComparison.Ordinal) ||
               name == "NativeRetainerVerb";
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Franthropy.sln")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException(
            $"Could not find the Franthropy repository above {AppContext.BaseDirectory}.");
    }

    [GeneratedRegex(
        @"^(?:public|internal)\s+(?:(?:sealed|static|abstract|unsafe|partial|readonly)\s+)*(?:(?:class|record(?:\s+struct)?|enum|interface|struct)\s+|delegate\s+\S+\s+)(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Multiline)]
    private static partial Regex TopLevelTypeDeclaration();
}
