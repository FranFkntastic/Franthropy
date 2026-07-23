using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Franthropy.Dalamud.AgentBridge;

/// <summary>Wire-format contracts shared by local, authenticated agent bridge hosts and clients.</summary>
public sealed record AgentBridgeDiscovery
{
    public required int SchemaVersion { get; init; }
    public required string PipeName { get; init; }
    public required int ProcessId { get; init; }
    public required string PluginInstanceId { get; init; }
    public string? RuntimeInstanceId { get; init; }
    public string? PluginInternalName { get; init; }
    public string? ProfileId { get; init; }
    public string? ProfileAlias { get; init; }
    public int ProtocolVersion { get; init; } = 1;
}

public sealed record AgentBridgeRequest
{
    public string? Token { get; init; }
    public string? Command { get; init; }
    public string? Target { get; init; }
    public long? FrameId { get; init; }
    public string? Challenge { get; init; }
    public string? ProofId { get; init; }
    public bool FullViewport { get; init; }
    public string? TransactionId { get; init; }
    public JsonElement? Arguments { get; init; }
    public string? OperationId { get; init; }
}

public sealed record AgentBridgeUiCaptureTransactionReceipt(
    string TransactionId,
    string Target,
    long FrameId,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ReadyAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>A provider-advertised review surface which a generic bridge client can present without plugin-specific tab knowledge.</summary>
public sealed record AgentBridgeReviewSurfaceDescriptor(
    string Id,
    string Label,
    string Command,
    string Target,
    int Order);

/// <summary>A provider-advertised rendered surface which a generic bridge client can prepare for unfocused capture.</summary>
public sealed record AgentBridgeCaptureSurfaceDescriptor(
    string Id,
    string Label,
    int Order,
    bool IsDefault = false);

/// <summary>Versioned, discovery-safe description of one bridge capability.</summary>
public sealed record AgentBridgeCapabilityDescriptor(string Id, int Version = 1);

/// <summary>A typed semantic action which a provider can present and invoke without coordinate input.</summary>
public sealed record AgentBridgeActionDescriptor(
    string Id,
    string Label,
    string SurfaceId,
    AgentBridgeUiControlKind Kind,
    bool Mutating,
    AgentBridgeActionArgumentSchema? Arguments = null,
    string? CompletionOperationKind = null);

public enum AgentBridgeActionArgumentKind
{
    String,
    Integer,
    Boolean,
    Enum,
    ItemName,
}

public sealed record AgentBridgeActionArgumentDescriptor(
    string Name,
    AgentBridgeActionArgumentKind Kind,
    bool Required = true,
    IReadOnlyList<string>? AllowedValues = null,
    long? Minimum = null,
    long? Maximum = null);

public sealed record AgentBridgeActionArgumentSchema(
    IReadOnlyList<AgentBridgeActionArgumentDescriptor> Properties,
    bool AllowAdditionalProperties = false);

/// <summary>Stable build identity for proving which assembly is actually loaded in the game process.</summary>
public sealed record AgentBridgeRuntimeIdentity(
    string PluginInternalName,
    string AssemblyVersion,
    string InformationalVersion,
    string BuildConfiguration,
    string? Commit,
    string MainDllSha256,
    string MainDllPath,
    int ProcessId,
    string RuntimeInstanceId,
    DateTimeOffset LoadedAtUtc)
{
    public static AgentBridgeRuntimeIdentity FromAssembly(string pluginInternalName, Assembly assembly, string? mainDllPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginInternalName);
        ArgumentNullException.ThrowIfNull(assembly);
        var assemblyVersion = assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assemblyVersion;
        var buildConfiguration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
            ?? "Unknown";
        var commit = TryExtractCommit(informationalVersion);
        var location = string.IsNullOrWhiteSpace(mainDllPath) ? assembly.Location : Path.GetFullPath(mainDllPath);
        var sha256 = File.Exists(location)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(location)))
            : string.Empty;
        return new AgentBridgeRuntimeIdentity(
            pluginInternalName,
            assemblyVersion,
            informationalVersion,
            buildConfiguration,
            commit,
            sha256,
            location,
            Environment.ProcessId,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow);
    }

    private static string? TryExtractCommit(string informationalVersion)
    {
        var separator = informationalVersion.LastIndexOf('+');
        if (separator < 0 || separator == informationalVersion.Length - 1)
            return null;
        var metadata = informationalVersion[(separator + 1)..];
        return metadata.Length >= 7 && metadata.All(char.IsAsciiHexDigit) ? metadata : null;
    }
}

public sealed record AgentBridgeManifest(
    int ProtocolVersion,
    AgentBridgeRuntimeIdentity Runtime,
    string ProfileId,
    string ProfileAlias,
    string SnapshotSchema,
    IReadOnlyList<AgentBridgeCapabilityDescriptor> Capabilities,
    IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> ReviewSurfaces,
    IReadOnlyList<AgentBridgeCaptureSurfaceDescriptor> CaptureSurfaces,
    IReadOnlyList<AgentBridgeActionDescriptor> Actions);

public enum AgentBridgeOperationState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record AgentBridgeOperationSnapshot(
    string Id,
    string Kind,
    AgentBridgeOperationState State,
    string Message,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long? Current = null,
    long? Total = null,
    bool CanCancel = false,
    string? ErrorCode = null,
    IReadOnlyDictionary<string, string>? Postconditions = null);

public sealed record AgentBridgeResponse
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public object? Receipt { get; init; }

    public string? OperationId { get; init; }

    public static AgentBridgeResponse Ok(string message, object? receipt = null, string? operationId = null) =>
        new() { Success = true, Message = message, Receipt = receipt, OperationId = operationId };
    public static AgentBridgeResponse Fail(string message) => new() { Success = false, Message = message };
}

public sealed record AgentBridgeCaptureReceipt
{
    public required int SchemaVersion { get; init; }
    public required string CaptureId { get; init; }
    public required string FileName { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string Sha256 { get; init; }
    public required int ProcessId { get; init; }
    public required string Scope { get; init; }
}

/// <summary>Current-user DPAPI helpers. Callers own the returned buffers and must clear secret bytes when finished.</summary>
public static class AgentBridgeDataProtection
{
    public static string ProtectToken(string token, string pluginInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        try
        {
            var entropy = GetEntropy(pluginInstanceId);
            try
            {
                var protectedBytes = ProtectedData.Protect(tokenBytes, entropy, DataProtectionScope.CurrentUser);
                try { return Convert.ToBase64String(protectedBytes); }
                finally { CryptographicOperations.ZeroMemory(protectedBytes); }
            }
            finally { CryptographicOperations.ZeroMemory(entropy); }
        }
        finally { CryptographicOperations.ZeroMemory(tokenBytes); }
    }

    public static string UnprotectToken(string protectedToken, string pluginInstanceId)
    {
        var protectedBytes = Convert.FromBase64String(protectedToken);
        try
        {
            var entropy = GetEntropy(pluginInstanceId);
            try
            {
                var tokenBytes = ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
                try { return Encoding.UTF8.GetString(tokenBytes); }
                finally { CryptographicOperations.ZeroMemory(tokenBytes); }
            }
            finally { CryptographicOperations.ZeroMemory(entropy); }
        }
        finally { CryptographicOperations.ZeroMemory(protectedBytes); }
    }

    public static byte[] ProtectBytes(ReadOnlySpan<byte> source, string pluginInstanceId) => Protect(source, pluginInstanceId);

    public static byte[] UnprotectBytes(ReadOnlySpan<byte> source, string pluginInstanceId) => Unprotect(source, pluginInstanceId);

    private static byte[] Protect(ReadOnlySpan<byte> source, string pluginInstanceId)
    {
        var sourceBytes = source.ToArray();
        var entropy = GetEntropy(pluginInstanceId);
        try { return ProtectedData.Protect(sourceBytes, entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(sourceBytes); CryptographicOperations.ZeroMemory(entropy); }
    }

    private static byte[] Unprotect(ReadOnlySpan<byte> source, string pluginInstanceId)
    {
        var sourceBytes = source.ToArray();
        var entropy = GetEntropy(pluginInstanceId);
        try { return ProtectedData.Unprotect(sourceBytes, entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(sourceBytes); CryptographicOperations.ZeroMemory(entropy); }
    }

    private static byte[] GetEntropy(string pluginInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginInstanceId);
        return Encoding.UTF8.GetBytes(pluginInstanceId);
    }
}
