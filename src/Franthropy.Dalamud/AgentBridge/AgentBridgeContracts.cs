using System.Security.Cryptography;
using System.Text;

namespace Franthropy.Dalamud.AgentBridge;

/// <summary>Wire-format contracts shared by local, authenticated agent bridge hosts and clients.</summary>
public sealed record AgentBridgeDiscovery
{
    public required int SchemaVersion { get; init; }
    public required string PipeName { get; init; }
    public required int ProcessId { get; init; }
    public required string PluginInstanceId { get; init; }
}

public sealed record AgentBridgeRequest
{
    public string? Token { get; init; }
    public string? Command { get; init; }
    public string? Target { get; init; }
    public long? FrameId { get; init; }
    public bool FullViewport { get; init; }
    public string? TransactionId { get; init; }
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

public sealed record AgentBridgeResponse
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public object? Receipt { get; init; }

    public static AgentBridgeResponse Ok(string message, object? receipt = null) => new() { Success = true, Message = message, Receipt = receipt };
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
