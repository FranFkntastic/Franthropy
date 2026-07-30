using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Franthropy.Dalamud.AgentBridge;

namespace Franthropy.Dalamud.Tests.AgentBridge;

public sealed class AgentBridgeHostTests
{
    [Fact]
    public async Task Start_AdvertisesAuthenticatedManifest_AndDisposeRemovesDiscovery()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var pluginConfigDirectory = Path.Combine(root, "XIVLauncher", "pluginConfigs", "Demo");
            Directory.CreateDirectory(pluginConfigDirectory);
            var profile = AgentBridgeProfileIdentity.FromPluginConfigDirectory(pluginConfigDirectory);
            var identity = AgentBridgeRuntimeIdentity.FromAssembly("Demo", typeof(AgentBridgeHostTests).Assembly);
            var manifest = new AgentBridgeManifest(
                2,
                identity,
                profile.Id,
                profile.Alias,
                "demo.snapshot.v1",
                [new("snapshot")],
                [],
                [],
                []);
            var pipeName = $"Franthropy.AgentBridge.Tests.{Guid.NewGuid():N}";
            var protectedToken = string.Empty;
            using var host = new AgentBridgeHost(new AgentBridgeHostOptions
            {
                ConfigDirectory = pluginConfigDirectory,
                PluginInstanceId = "plugin-instance",
                PipeName = pipeName,
                GetProtectedAccessToken = () => protectedToken,
                SetProtectedAccessToken = value => protectedToken = value,
                SaveConfiguration = () => { },
                CreateManifest = () => manifest,
                HandleRequestAsync = (_, _) => ValueTask.FromResult(AgentBridgeResponse.Fail("Bridge command is not allowed.")),
            });

            host.Start();

            Assert.True(host.IsRunning);
            Assert.True(File.Exists(host.DiscoveryPath));
            var discovery = JsonSerializer.Deserialize<AgentBridgeDiscovery>(File.ReadAllText(host.DiscoveryPath), WebJson);
            Assert.NotNull(discovery);
            Assert.Equal("Demo", discovery.PluginInternalName);
            Assert.Equal(identity.RuntimeInstanceId, discovery.RuntimeInstanceId);
            var token = AgentBridgeDataProtection.UnprotectToken(protectedToken, "plugin-instance");
            var response = await SendAsync(pipeName, new AgentBridgeRequest { Token = token, Command = "hello" });
            Assert.True(response.Success);
            Assert.Equal(identity.MainDllSha256, response.Receipt.GetProperty("runtime").GetProperty("mainDllSha256").GetString());
            Assert.Equal(identity.MainDllPath, response.Receipt.GetProperty("runtime").GetProperty("mainDllPath").GetString());

            host.Dispose();
            Assert.False(File.Exists(host.DiscoveryPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidToken_IsRejectedBeforeProductHandler()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var pluginConfigDirectory = Path.Combine(root, "XIVLauncher", "pluginConfigs", "Demo");
            Directory.CreateDirectory(pluginConfigDirectory);
            var profile = AgentBridgeProfileIdentity.FromPluginConfigDirectory(pluginConfigDirectory);
            var identity = AgentBridgeRuntimeIdentity.FromAssembly("Demo", typeof(AgentBridgeHostTests).Assembly);
            var manifest = new AgentBridgeManifest(2, identity, profile.Id, profile.Alias, "demo.v1", [], [], [], []);
            var pipeName = $"Franthropy.AgentBridge.Tests.{Guid.NewGuid():N}";
            var handled = false;
            var protectedToken = string.Empty;
            using var host = new AgentBridgeHost(new AgentBridgeHostOptions
            {
                ConfigDirectory = pluginConfigDirectory,
                PluginInstanceId = "plugin-instance",
                PipeName = pipeName,
                GetProtectedAccessToken = () => protectedToken,
                SetProtectedAccessToken = value => protectedToken = value,
                SaveConfiguration = () => { },
                CreateManifest = () => manifest,
                HandleRequestAsync = (_, _) =>
                {
                    handled = true;
                    return ValueTask.FromResult(AgentBridgeResponse.Ok("Unexpected."));
                },
            });
            host.Start();

            var response = await SendAsync(pipeName, new AgentBridgeRequest { Token = "wrong", Command = "product-command" });

            Assert.False(response.Success);
            Assert.Contains("authentication", response.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(handled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<ResponseEnvelope> SendAsync(string pipeName, AgentBridgeRequest request)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5_000);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, WebJson));
        var json = await reader.ReadLineAsync();
        return JsonSerializer.Deserialize<ResponseEnvelope>(json!, WebJson)!;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Franthropy.AgentBridge.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private sealed record ResponseEnvelope(bool Success, string Message, JsonElement Receipt);
}
