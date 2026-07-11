using System.Numerics;
using Franthropy.Dalamud.AgentBridge;

namespace Franthropy.Dalamud.Tests.AgentBridge;

public sealed class AgentBridgeViewportRegionTests
{
    [Fact]
    public void GetUvBounds_NormalizesWindowCoordinatesAndClampsPadding()
    {
        var region = new AgentBridgeViewportRegion(new(100, 50), new(200, 100), Vector2.Zero, new(1000, 500), DateTimeOffset.UtcNow);

        var (uv0, uv1) = region.GetUvBounds(10);

        Assert.Equal(new Vector2(.09f, .08f), uv0);
        Assert.Equal(new Vector2(.31f, .32f), uv1);
    }

    [Fact]
    public void IsFresh_RejectsAnExpiredRegion()
    {
        var renderedAt = DateTimeOffset.UtcNow.AddSeconds(-4);
        var region = new AgentBridgeViewportRegion(Vector2.Zero, Vector2.One, Vector2.Zero, Vector2.One, renderedAt);

        Assert.False(region.IsFresh(TimeSpan.FromSeconds(3), DateTimeOffset.UtcNow));
    }
}
