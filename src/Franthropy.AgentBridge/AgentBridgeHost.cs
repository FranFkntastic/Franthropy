using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Franthropy.Dalamud.AgentBridge;

/// <summary>
/// Product-neutral authenticated named-pipe host for local agent bridges. Product plugins own the
/// allowlisted command router and state; this host owns credentials, discovery, framing and disposal.
/// </summary>
public sealed class AgentBridgeHost : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AgentBridgeHostOptions options;
    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    private Task? listener;
    private string? accessToken;
    private bool disposed;

    public AgentBridgeHost(AgentBridgeHostOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConfigDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PluginInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PipeName);
        ArgumentNullException.ThrowIfNull(options.GetProtectedAccessToken);
        ArgumentNullException.ThrowIfNull(options.SetProtectedAccessToken);
        ArgumentNullException.ThrowIfNull(options.SaveConfiguration);
        ArgumentNullException.ThrowIfNull(options.CreateManifest);
        ArgumentNullException.ThrowIfNull(options.HandleRequestAsync);
    }

    public bool IsRunning
    {
        get { lock (gate) return listener is not null; }
    }

    public string DiscoveryPath => Path.Combine(BridgeDirectory, $"discovery-{Environment.ProcessId}.json");

    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (listener is not null)
                return;

            accessToken = GetOrCreateAccessToken();
            var manifest = options.CreateManifest();
            ValidateManifest(manifest);
            Directory.CreateDirectory(BridgeDirectory);
            if (!options.EnableAudit && File.Exists(AuditPath))
                File.Delete(AuditPath);
            WriteDiscovery(manifest);
            cancellation = new CancellationTokenSource();
            listener = Task.Run(() => ListenLoopAsync(cancellation.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? activeCancellation;
        Task? activeListener;
        lock (gate)
        {
            activeCancellation = cancellation;
            activeListener = listener;
            cancellation = null;
            listener = null;
            accessToken = null;
        }

        if (activeCancellation is not null)
        {
            activeCancellation.Cancel();
            if (activeListener is not null)
            {
                try { activeListener.Wait(options.StopTimeout); }
                catch (Exception exception) when (exception is AggregateException or OperationCanceledException) { }
            }
            activeCancellation.Dispose();
        }

        if (File.Exists(DiscoveryPath))
            File.Delete(DiscoveryPath);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
        }
        Stop();
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    options.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(options.RequestTimeout);
                var requestJson = await ReadBoundedLineAsync(reader, options.MaxRequestCharacters, requestTimeout.Token).ConfigureAwait(false);
                var response = await HandleRequestAsync(requestJson, requestTimeout.Token).ConfigureAwait(false);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AppendAudit("host-error", exception.GetType().Name);
                try { await Task.Delay(options.ErrorBackoff, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
        }
    }

    private async ValueTask<AgentBridgeResponse> HandleRequestAsync(string? requestJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
            return AgentBridgeResponse.Fail("Invalid bridge request.");

        AgentBridgeRequest? request;
        try { request = JsonSerializer.Deserialize<AgentBridgeRequest>(requestJson, JsonOptions); }
        catch (JsonException) { return AgentBridgeResponse.Fail("Bridge request JSON is invalid."); }

        var expectedToken = accessToken;
        if (request is null || string.IsNullOrEmpty(expectedToken) || !TokenEquals(request.Token, expectedToken))
            return AgentBridgeResponse.Fail("Bridge authentication failed.");

        var command = request.Command?.Trim().ToLowerInvariant();
        if (command == "hello")
            return AgentBridgeResponse.Ok("Bridge is ready.", options.CreateManifest());
        if (command == "get-manifest")
            return AgentBridgeResponse.Ok("Bridge manifest captured.", options.CreateManifest());

        var response = await options.HandleRequestAsync(request with { Command = command }, cancellationToken).ConfigureAwait(false);
        AppendAudit(command ?? "missing-command", response.Success ? "accepted" : "rejected");
        return response;
    }

    private string GetOrCreateAccessToken()
    {
        var protectedToken = options.GetProtectedAccessToken();
        if (!string.IsNullOrWhiteSpace(protectedToken))
        {
            try { return AgentBridgeDataProtection.UnprotectToken(protectedToken, options.PluginInstanceId); }
            catch (Exception exception) when (exception is FormatException or CryptographicException)
            {
                options.SetProtectedAccessToken(string.Empty);
            }
        }

        var token = Guid.NewGuid().ToString("N");
        options.SetProtectedAccessToken(AgentBridgeDataProtection.ProtectToken(token, options.PluginInstanceId));
        options.SaveConfiguration();
        return token;
    }

    private static bool TokenEquals(string? supplied, string expected)
    {
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied ?? string.Empty);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        try { return CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(suppliedBytes);
            CryptographicOperations.ZeroMemory(expectedBytes);
        }
    }

    private void WriteDiscovery(AgentBridgeManifest manifest)
    {
        var discovery = new AgentBridgeDiscovery
        {
            SchemaVersion = 2,
            ProtocolVersion = manifest.ProtocolVersion,
            PipeName = options.PipeName,
            ProcessId = Environment.ProcessId,
            PluginInstanceId = options.PluginInstanceId,
            RuntimeInstanceId = manifest.Runtime.RuntimeInstanceId,
            PluginInternalName = manifest.Runtime.PluginInternalName,
            ProfileId = manifest.ProfileId,
            ProfileAlias = manifest.ProfileAlias,
        };
        var temporaryPath = DiscoveryPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(discovery, JsonOptions));
        File.Move(temporaryPath, DiscoveryPath, true);
    }

    private void AppendAudit(string action, string result)
    {
        if (!options.EnableAudit)
            return;
        Directory.CreateDirectory(BridgeDirectory);
        File.AppendAllText(AuditPath, JsonSerializer.Serialize(new
        {
            atUtc = DateTimeOffset.UtcNow,
            action,
            result,
        }, JsonOptions) + Environment.NewLine);
    }

    private static void ValidateManifest(AgentBridgeManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.ProtocolVersion < 1)
            throw new InvalidOperationException("Agent bridge protocol version must be positive.");
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.Runtime.PluginInternalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.Runtime.RuntimeInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.ProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.ProfileAlias);
        var duplicateAction = manifest.Actions.GroupBy(action => action.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicateAction is not null)
            throw new InvalidOperationException($"Agent bridge action '{duplicateAction.Key}' is declared more than once.");
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return builder.Length == 0 ? null : builder.ToString();
            var newline = Array.IndexOf(buffer, '\n', 0, read);
            var length = newline >= 0 ? newline : read;
            if (newline >= 0 && length > 0 && buffer[length - 1] == '\r')
                length--;
            if (builder.Length + length > maximumCharacters)
                return null;
            builder.Append(buffer, 0, length);
            if (newline >= 0)
                return builder.ToString();
        }
    }

    private string BridgeDirectory => Path.Combine(options.ConfigDirectory, "agent-bridge");
    private string AuditPath => Path.Combine(BridgeDirectory, "audit.jsonl");
}

public sealed class AgentBridgeHostOptions
{
    public required string ConfigDirectory { get; init; }
    public required string PluginInstanceId { get; init; }
    public required string PipeName { get; init; }
    public required Func<string?> GetProtectedAccessToken { get; init; }
    public required Action<string> SetProtectedAccessToken { get; init; }
    public required Action SaveConfiguration { get; init; }
    public required Func<AgentBridgeManifest> CreateManifest { get; init; }
    public required Func<AgentBridgeRequest, CancellationToken, ValueTask<AgentBridgeResponse>> HandleRequestAsync { get; init; }
    public bool EnableAudit { get; init; }
    public int MaxRequestCharacters { get; init; } = 65_536;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ErrorBackoff { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>An allowlist by construction: unregistered commands never reach product handlers.</summary>
public sealed class AgentBridgeCommandRouter
{
    private readonly Dictionary<string, Func<AgentBridgeRequest, CancellationToken, ValueTask<AgentBridgeResponse>>> handlers =
        new(StringComparer.OrdinalIgnoreCase);

    public AgentBridgeCommandRouter Register(
        string command,
        Func<AgentBridgeRequest, CancellationToken, ValueTask<AgentBridgeResponse>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(handler);
        if (!handlers.TryAdd(command.Trim(), handler))
            throw new InvalidOperationException($"Agent bridge command '{command}' is already registered.");
        return this;
    }

    public AgentBridgeCommandRouter Register(string command, Func<AgentBridgeRequest, AgentBridgeResponse> handler) =>
        Register(command, (request, _) => ValueTask.FromResult(handler(request)));

    public ValueTask<AgentBridgeResponse> HandleAsync(AgentBridgeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Command) || !handlers.TryGetValue(request.Command, out var handler))
            return ValueTask.FromResult(AgentBridgeResponse.Fail("Bridge command is not allowed."));
        return handler(request, cancellationToken);
    }
}

public static class AgentBridgeProfileIdentity
{
    public static (string Id, string Alias) FromPluginConfigDirectory(string pluginConfigDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginConfigDirectory);
        var pluginDirectory = new DirectoryInfo(Path.GetFullPath(pluginConfigDirectory));
        var profileDirectory = pluginDirectory.Parent?.Parent
            ?? throw new InvalidOperationException("Plugin configuration directory is outside an XIVLauncher profile.");
        var profilePath = profileDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profilePath.ToUpperInvariant())))[..16].ToLowerInvariant();
        var alias = string.Equals(profileDirectory.Name, "XIVLauncher", StringComparison.OrdinalIgnoreCase)
            ? "primary"
            : profileDirectory.Name;
        return ($"{alias.ToLowerInvariant()}-{hash}", alias);
    }
}
