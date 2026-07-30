using Dalamud.Plugin;
using Franthropy.Dalamud.AgentBridge;
using System;

namespace AgentBridgeMinimal;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Configuration configuration;
    private readonly AgentBridgeHost host;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        var profile = AgentBridgeProfileIdentity.FromPluginConfigDirectory(
            pluginInterface.GetPluginConfigDirectory());
        var runtime = AgentBridgeRuntimeIdentity.FromAssembly(
            "AgentBridgeMinimal",
            GetType().Assembly,
            pluginInterface.AssemblyLocation.FullName);
        var router = new AgentBridgeCommandRouter()
            .Register("get-snapshot", _ => AgentBridgeResponse.Ok(
                "Snapshot captured.",
                new
                {
                    schema = "agent-bridge-minimal.snapshot.v1",
                    loadedAtUtc = runtime.LoadedAtUtc,
                    readOnly = true,
                }));

        host = new AgentBridgeHost(new AgentBridgeHostOptions
        {
            ConfigDirectory = pluginInterface.GetPluginConfigDirectory(),
            PluginInstanceId = configuration.PluginInstanceId,
            PipeName = $"franthropy.agentbridge.sample.{Environment.ProcessId}.{configuration.PluginInstanceId}",
            GetProtectedAccessToken = () => configuration.AgentBridgeProtectedAccessToken,
            SetProtectedAccessToken = value => configuration.AgentBridgeProtectedAccessToken = value,
            SaveConfiguration = SaveConfiguration,
            CreateManifest = () => new AgentBridgeManifest(
                ProtocolVersion: 1,
                Runtime: runtime,
                ProfileId: profile.Id,
                ProfileAlias: profile.Alias,
                SnapshotSchema: "agent-bridge-minimal.snapshot.v1",
                Capabilities: [new AgentBridgeCapabilityDescriptor("snapshot.read")],
                ReviewSurfaces: [],
                CaptureSurfaces: [],
                Actions: []),
            HandleRequestAsync = router.HandleAsync,
        });
        host.Start();
    }

    public void Dispose() => host.Dispose();

    private void SaveConfiguration() => pluginInterface.SavePluginConfig(configuration);
}
