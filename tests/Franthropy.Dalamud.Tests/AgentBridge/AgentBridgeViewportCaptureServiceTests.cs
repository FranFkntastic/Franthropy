using System.Numerics;
using Franthropy.Dalamud.AgentBridge;

namespace Franthropy.Dalamud.Tests.AgentBridge;

public sealed class AgentBridgeViewportCaptureServiceTests
{
    [Fact]
    public void PluginWindowCaptureUsesRenderedViewport()
    {
        var region = new AgentBridgeCaptureRegion(
            Vector2.Zero,
            new Vector2(800, 600),
            42,
            Vector2.Zero,
            new Vector2(1920, 1080),
            DateTimeOffset.UtcNow);

        var viewportId = AgentBridgeViewportCaptureService.ResolveCaptureViewportId(
            fullViewport: false,
            mainViewportId: 1,
            region);

        Assert.Equal(42u, viewportId);
    }

    [Fact]
    public void FullViewportCaptureStillUsesMainViewport()
    {
        var viewportId = AgentBridgeViewportCaptureService.ResolveCaptureViewportId(
            fullViewport: true,
            mainViewportId: 1,
            region: null);

        Assert.Equal(1u, viewportId);
    }
}
