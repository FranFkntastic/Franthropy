using Franthropy.Dalamud.Observations;
using Franthropy.Dalamud.Diagnostics;

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

    [Fact]
    public void Shared_host_refuses_an_unapproved_game_build_before_registering_callbacks()
    {
        var exception = Assert.Throws<GamePatchCompatibilityException>(() =>
            new DalamudSharedObservationHost(new DalamudSharedObservationHostOptions
            {
                PluginConfigDirectory = "unused",
                PluginName = "Test",
                PluginInstanceId = "instance",
                GameBuild = "2099.01.01.0000.0000",
                GameInventory = null!,
                PlayerState = null!,
                AddonLifecycle = null!,
            }));

        Assert.Equal("UnsupportedGameBuild", GamePatchCompatibility.FailureCode);
        Assert.Equal(DalamudSharedObservationHost.ApprovedGameBuild, exception.Compatibility.ApprovedGameVersion);
    }
}
