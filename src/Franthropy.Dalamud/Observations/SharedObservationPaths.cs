using Franthropy.Dalamud.AgentBridge;

namespace Franthropy.Dalamud.Observations;

public sealed record SharedObservationPaths(
    string ProfileId,
    string ProfileAlias,
    string SharedDirectory,
    string DatabasePath,
    string ChangeSignalPath,
    string MigrationLockPath,
    string CandidatesDirectory,
    string BackupsDirectory,
    string QuarantineDirectory,
    string CaptureSessionsPath)
{
    public static SharedObservationPaths FromPluginConfigDirectory(string pluginConfigDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginConfigDirectory);
        var pluginDirectory = new DirectoryInfo(Path.GetFullPath(pluginConfigDirectory));
        var pluginConfigsDirectory = pluginDirectory.Parent
            ?? throw new InvalidOperationException("Plugin configuration directory has no parent.");
        if (!string.Equals(pluginConfigsDirectory.Name, "pluginConfigs", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Plugin configuration directory must be an immediate child of an XIVLauncher pluginConfigs directory.");

        var profile = AgentBridgeProfileIdentity.FromPluginConfigDirectory(pluginDirectory.FullName);
        var shared = Path.Combine(pluginConfigsDirectory.FullName, "Franthropy.Shared");
        return new SharedObservationPaths(
            profile.Id,
            profile.Alias,
            shared,
            Path.Combine(shared, "observations.db"),
            Path.Combine(shared, "changes.signal"),
            Path.Combine(shared, "migration.lock"),
            Path.Combine(shared, "candidates"),
            Path.Combine(shared, "backups"),
            Path.Combine(shared, "quarantine"),
            Path.Combine(shared, "capture-sessions.json"));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(SharedDirectory);
        Directory.CreateDirectory(CandidatesDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(QuarantineDirectory);
    }
}
