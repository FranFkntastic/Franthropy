using Franthropy.Dalamud.Observations;
using System.Reflection;
using Dalamud.Plugin.Services;

namespace Franthropy.Dalamud.Tests.Observations;

public sealed class SharedObservationPathsTests
{
    [Fact]
    public void Resolver_fails_when_plugin_directory_is_not_under_pluginConfigs()
    {
        var root = Path.Combine(Path.GetTempPath(), "Franthropy.Paths.Tests", Guid.NewGuid().ToString("N"));
        var invalid = Path.Combine(root, "config", "Plugin");
        Directory.CreateDirectory(invalid);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                SharedObservationPaths.FromPluginConfigDirectory(invalid));

            Assert.Contains("pluginConfigs", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("2026.09.15.0000.0000")]
    [InlineData("2099.01.01.0000.0000")]
    [InlineData("unknown")]
    public void Shared_host_accepts_diagnostic_build_changes_without_registering_callbacks(string diagnosticBuild)
    {
        var root = Path.Combine(Path.GetTempPath(), "Franthropy.Capability.Tests", Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "XIVLauncher", "pluginConfigs", "Test");
        Directory.CreateDirectory(config);
        try
        {
            using var host = new DalamudSharedObservationHost(new DalamudSharedObservationHostOptions
            {
                PluginConfigDirectory = config,
                PluginName = "Test",
                PluginInstanceId = "instance",
                GameBuild = diagnosticBuild,
                GameInventory = DispatchProxy.Create<IGameInventory, UncalledServiceProxy>(),
                PlayerState = DispatchProxy.Create<IPlayerState, UncalledServiceProxy>(),
                AddonLifecycle = DispatchProxy.Create<IAddonLifecycle, UncalledServiceProxy>(),
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Shared_host_still_requires_actual_inventory_dependency()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new DalamudSharedObservationHost(new()
        {
            PluginConfigDirectory = "unused", PluginName = "Test", PluginInstanceId = "instance", GameBuild = "unknown",
            GameInventory = null!, PlayerState = null!, AddonLifecycle = null!,
        }));
        Assert.Contains("GameInventory", exception.ParamName);
    }

    public class UncalledServiceProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => throw new InvalidOperationException($"Unexpected callback registration before Start: {method?.Name}");
    }
}
