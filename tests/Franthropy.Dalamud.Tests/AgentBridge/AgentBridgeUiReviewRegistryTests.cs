using System.Numerics;
using Franthropy.Dalamud.AgentBridge;

namespace Franthropy.Dalamud.Tests.AgentBridge;

public sealed class AgentBridgeUiReviewRegistryTests
{
    [Fact]
    public void RegisteredControl_IsExposedAndCanBeInvokedOnce()
    {
        var registry = new AgentBridgeUiReviewRegistry();
        var invoked = 0;

        registry.BeginFrame();
        registry.Register("settings.capture", "Allow screenshot handoff", AgentBridgeUiControlKind.Toggle, new(12, 18), new(212, 42), true, false, "Disabled", () => invoked++);
        var frame = registry.EndFrame();

        var control = Assert.Single(frame.Controls);
        Assert.Equal("settings.capture", control.Id);
        Assert.Equal(200, control.Width);
        Assert.Equal(24, control.Height);

        var result = registry.Invoke(control.Id, frame.FrameId);

        Assert.True(result.Success);
        Assert.Equal(1, invoked);
        Assert.Empty(result.Frame.Controls);
        Assert.False(registry.Invoke(control.Id, frame.FrameId).Success);
        Assert.Equal(1, invoked);
    }

    [Fact]
    public void StaleOrDisabledControls_AreRejectedWithoutInvocation()
    {
        var registry = new AgentBridgeUiReviewRegistry();
        var invoked = 0;
        registry.BeginFrame();
        registry.Register("route.stop", "Stop route", AgentBridgeUiControlKind.Button, Vector2.Zero, new(100, 30), false, false, null, () => invoked++);
        var frame = registry.EndFrame();

        Assert.False(registry.Invoke("route.stop", frame.FrameId).Success);
        Assert.False(registry.Invoke("route.stop", frame.FrameId + 1).Success);
        Assert.Equal(0, invoked);
    }
}
