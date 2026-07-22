using Franthropy.Dalamud.AgentBridge;

namespace Franthropy.Dalamud.Tests.AgentBridge;

public sealed class AgentBridgeOperationRegistryTests
{
    [Fact]
    public void Operation_TracksProgressAndTerminalPostconditions()
    {
        var registry = new AgentBridgeOperationRegistry();
        var created = registry.Begin("retainer-refresh", "Queued.", id: "refresh-1");
        Assert.Equal(AgentBridgeOperationState.Queued, created.State);

        registry.Update(created.Id, AgentBridgeOperationState.Running, "Refreshing 4/9.", 4, 9);
        var completed = registry.Update(
            created.Id,
            AgentBridgeOperationState.Succeeded,
            "Refresh complete.",
            9,
            9,
            postconditions: new Dictionary<string, string> { ["retainersObserved"] = "9" });

        Assert.Equal(AgentBridgeOperationState.Succeeded, completed.State);
        Assert.Equal("9", completed.Postconditions?["retainersObserved"]);
        Assert.Throws<InvalidOperationException>(() =>
            registry.Update(created.Id, AgentBridgeOperationState.Running, "Too late."));
    }
}
