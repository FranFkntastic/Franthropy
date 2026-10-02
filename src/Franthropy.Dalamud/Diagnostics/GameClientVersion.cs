using System.Diagnostics;

namespace Franthropy.Dalamud.Diagnostics;

/// <summary>Diagnostic build identity. It never decides whether an operation is available.</summary>
public static class GameClientVersion
{
    public static string ReadCurrentGameVersion(string? processPath = null)
    {
        var executablePath = string.IsNullOrWhiteSpace(processPath) ? Environment.ProcessPath : processPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            return "unknown";
        try
        {
            var versionPath = Path.Combine(Path.GetDirectoryName(executablePath) ?? string.Empty, "ffxivgame.ver");
            if (File.Exists(versionPath))
            {
                var version = File.ReadAllText(versionPath).Trim();
                return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
            }
            return FileVersionInfo.GetVersionInfo(executablePath).FileVersion?.Trim() is { Length: > 0 } fileVersion
                ? fileVersion : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
