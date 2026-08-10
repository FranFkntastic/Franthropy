using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Franthropy.Dalamud.AgentBridge;

/// <summary>
/// Opt-in authenticated bridge commands for observing and changing installed-plugin load state.
/// A serving plugin can never change its own lifecycle through the request it is handling.
/// </summary>
public sealed class DalamudPluginLifecycleBridge
{
    private static readonly TimeSpan StateChangeTimeout = TimeSpan.FromSeconds(20);
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IFramework framework;

    public DalamudPluginLifecycleBridge(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework)
    {
        this.pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        this.commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        this.framework = framework ?? throw new ArgumentNullException(nameof(framework));
    }

    public AgentBridgeCommandRouter RegisterCommands(AgentBridgeCommandRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        router.Register("list-plugins", _ => AgentBridgeResponse.Ok("Installed plugin state captured.", Snapshot()));
        router.Register("enable-plugin", (request, token) => SetEnabledAsync(request, true, token));
        router.Register("disable-plugin", (request, token) => SetEnabledAsync(request, false, token));
        return router;
    }

    public AgentBridgePluginLifecycleSnapshot Snapshot() => new(
        DateTimeOffset.UtcNow,
        pluginInterface.InstalledPlugins
            .OrderBy(plugin => plugin.InternalName, StringComparer.OrdinalIgnoreCase)
            .Select(ToState)
            .ToArray());

    private async ValueTask<AgentBridgeResponse> SetEnabledAsync(
        AgentBridgeRequest request,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Target))
            return AgentBridgeResponse.Fail("A plugin internal name is required.");

        try
        {
            var receipt = await SetEnabledAsync(request.Target, enabled, cancellationToken).ConfigureAwait(false);
            return AgentBridgeResponse.Ok(enabled ? "Plugin enabled." : "Plugin disabled.", receipt);
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        {
            return AgentBridgeResponse.Fail($"Plugin lifecycle change failed: {exception.Message}");
        }
    }

    private async Task<AgentBridgePluginLifecycleChangeReceipt> SetEnabledAsync(
        string internalName,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var managedPlugin = FindRequiredExposed(internalName, enabled);
        var before = ToState(managedPlugin);
        if (string.Equals(before.InternalName, pluginInterface.Manifest.InternalName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The bridge cannot change its own lifecycle while serving a request.");

        if (before.IsLoaded == enabled)
            return new(enabled, false, before, before, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var requestedAt = DateTimeOffset.UtcNow;
        var command = enabled ? "/xlenableplugin" : "/xldisableplugin";
        var accepted = false;
        await framework.RunOnTick(() =>
            accepted = commandManager.ProcessCommand($"{command} \"{EscapeArgument(managedPlugin.Name)}\"")).ConfigureAwait(false);
        if (!accepted)
            throw new InvalidOperationException($"Dalamud did not accept the {command} lifecycle command.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StateChangeTimeout);
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var current = FindRequired(before.InternalName, before.Version, before.IsDev);
            if (current.IsLoaded == enabled)
                return new(enabled, true, before, current, requestedAt, DateTimeOffset.UtcNow);
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
    }

    private IExposedPlugin FindRequiredExposed(string internalName, bool enabling)
    {
        var matches = pluginInterface.InstalledPlugins.Where(plugin =>
            string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1)
            return matches[0];
        if (matches.Length == 0)
            throw new KeyNotFoundException($"Plugin '{internalName}' is not installed.");

        var preferred = enabling
            ? matches.Where(plugin => plugin.IsDev).ToArray()
            : matches.Where(plugin => plugin.IsLoaded).ToArray();
        return preferred.Length == 1
            ? preferred[0]
            : throw new InvalidOperationException(
                $"Plugin '{internalName}' is ambiguous: {string.Join(", ", matches.Select(plugin => $"{plugin.Version} ({(plugin.IsDev ? "dev" : "installed")})"))}.");
    }

    private AgentBridgeInstalledPluginState FindRequired(string internalName, string version, bool isDev)
    {
        var match = pluginInterface.InstalledPlugins.SingleOrDefault(plugin =>
            string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(plugin.Version.ToString(), version, StringComparison.OrdinalIgnoreCase) &&
            plugin.IsDev == isDev);
        return match is null
            ? throw new KeyNotFoundException($"Managed plugin '{internalName}' {version} is no longer installed.")
            : ToState(match);
    }

    private static AgentBridgeInstalledPluginState ToState(IExposedPlugin plugin) => new(
        plugin.InternalName,
        plugin.Name,
        plugin.Version.ToString(),
        plugin.IsLoaded,
        plugin.IsDev,
        plugin.IsTesting,
        plugin.IsThirdParty,
        plugin.IsOutdated,
        plugin.IsBanned,
        plugin.IsOrphaned,
        plugin.IsDecommissioned,
        plugin.HasMainUi,
        plugin.HasConfigUi);

    private static string EscapeArgument(string value) => value.Replace("\"", string.Empty, StringComparison.Ordinal);
}

public sealed record AgentBridgePluginLifecycleSnapshot(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<AgentBridgeInstalledPluginState> Plugins);

public sealed record AgentBridgeInstalledPluginState(
    string InternalName,
    string Name,
    string Version,
    bool IsLoaded,
    bool IsDev,
    bool IsTesting,
    bool IsThirdParty,
    bool IsOutdated,
    bool IsBanned,
    bool IsOrphaned,
    bool IsDecommissioned,
    bool HasMainUi,
    bool HasConfigUi);

public sealed record AgentBridgePluginLifecycleChangeReceipt(
    bool RequestedEnabled,
    bool Changed,
    AgentBridgeInstalledPluginState Before,
    AgentBridgeInstalledPluginState After,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset CompletedAtUtc);
