using Franthropy.Dalamud.AgentBridge;

namespace Franthropy.Dalamud.Tests.AgentBridge;

public sealed class AgentBridgeSurfaceRegistryTests
{
    [Fact]
    public void Registry_UsesSameDescriptorForCatalogAndPresentation()
    {
        var registry = new AgentBridgeSurfaceRegistry();
        var presented = 0;
        var descriptor = new AgentBridgeReviewSurfaceDescriptor("plugin.main", "Main", "present-surface", "plugin.main", 10);

        registry.Register(descriptor, () => presented++);

        Assert.Equal(descriptor, Assert.Single(registry.Snapshot()));
        Assert.True(registry.TryPresent("plugin.main"));
        Assert.Equal(1, presented);
        Assert.False(registry.TryPresent("missing"));
    }

    [Fact]
    public void EquivalentReregistration_DoesNotChangeCatalogRevision()
    {
        var registry = new AgentBridgeSurfaceRegistry();
        var descriptor = new AgentBridgeReviewSurfaceDescriptor("plugin.main", "Main", "present-surface", "plugin.main", 10);
        registry.Register(descriptor, () => { });
        var revision = registry.CatalogRevision;

        registry.Register(descriptor, () => { });

        Assert.Equal(revision, registry.CatalogRevision);
    }
}
