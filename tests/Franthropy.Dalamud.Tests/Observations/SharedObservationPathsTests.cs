using Franthropy.Dalamud.Observations;

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
}
