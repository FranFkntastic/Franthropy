using System.Diagnostics;

namespace Franthropy.Dalamud.Diagnostics;

/// <summary>
/// Exact-build approval for a client-memory, native-call, packet-layout, or UI-callback contract.
/// A new game build is blocked until the owning contract is deliberately re-verified and its
/// approved version is updated in source.
/// </summary>
public readonly record struct GamePatchCompatibility(
    string ContractId,
    string ApprovedGameVersion,
    string CurrentGameVersion,
    bool IsApproved)
{
    public const string FailureCode = "UnsupportedGameBuild";

    public string Message => IsApproved
        ? $"{ContractId} is approved for game build {CurrentGameVersion}."
        : $"{ContractId} is blocked: current game build is {CurrentGameVersion}, but the contract was last approved for {ApprovedGameVersion}.";
}

public static class GamePatchCompatibilityGate
{
    public static GamePatchCompatibility Evaluate(
        string contractId,
        string approvedGameVersion,
        string? currentGameVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedGameVersion);

        var current = string.IsNullOrWhiteSpace(currentGameVersion)
            ? ReadCurrentGameVersion()
            : currentGameVersion.Trim();
        var approved = approvedGameVersion.Trim();

        return new(
            contractId.Trim(),
            approved,
            current,
            string.Equals(current, approved, StringComparison.Ordinal));
    }

    public static GamePatchCompatibility Require(
        string contractId,
        string approvedGameVersion,
        string? currentGameVersion = null)
    {
        var compatibility = Evaluate(contractId, approvedGameVersion, currentGameVersion);
        if (!compatibility.IsApproved)
            throw new GamePatchCompatibilityException(compatibility);

        return compatibility;
    }

    public static string ReadCurrentGameVersion(string? processPath = null)
    {
        var executablePath = string.IsNullOrWhiteSpace(processPath)
            ? Environment.ProcessPath
            : processPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            return "unknown";

        var versionPath = Path.Combine(
            Path.GetDirectoryName(executablePath) ?? string.Empty,
            "ffxivgame.ver");
        try
        {
            if (File.Exists(versionPath))
            {
                var version = File.ReadAllText(versionPath).Trim();
                return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
            }

            return FileVersionInfo.GetVersionInfo(executablePath).FileVersion?.Trim() is { Length: > 0 } fileVersion
                ? fileVersion
                : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}

public sealed class GamePatchCompatibilityException : InvalidOperationException
{
    public GamePatchCompatibilityException(GamePatchCompatibility compatibility)
        : base(compatibility.Message)
    {
        Compatibility = compatibility;
    }

    public GamePatchCompatibility Compatibility { get; }
}
