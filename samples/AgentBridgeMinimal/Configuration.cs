using Dalamud.Configuration;
using System;

namespace AgentBridgeMinimal;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string PluginInstanceId { get; set; } = Guid.NewGuid().ToString("N");
    public string AgentBridgeProtectedAccessToken { get; set; } = string.Empty;
}
