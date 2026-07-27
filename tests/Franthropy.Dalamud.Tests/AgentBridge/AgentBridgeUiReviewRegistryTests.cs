using System.Numerics;
using System.Text.Json;
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

    [Fact]
    public void Review_ReturnsOnlyRequestedControlWithInvocationFrame()
    {
        var registry = new AgentBridgeUiReviewRegistry();
        registry.BeginFrame();
        registry.Register("first", "First", AgentBridgeUiControlKind.Button, Vector2.Zero, new(100, 30), true, false, "Ready", () => { });
        registry.Register("second", "Second", AgentBridgeUiControlKind.Button, Vector2.Zero, new(100, 30), true, false, "Working", () => { });
        var frame = registry.EndFrame();

        var review = registry.Review("second");

        Assert.Equal(frame.FrameId, review.FrameId);
        Assert.Equal(frame.ExpiresAtUtc, review.ExpiresAtUtc);
        Assert.Equal("second", Assert.IsType<AgentBridgeUiControl>(review.Control).Id);
        Assert.Equal("Working", review.Control.Value);
        Assert.Null(registry.Review("missing").Control);
    }

    [Fact]
    public void ReviewedControl_RemainsInvokableWhenTheNextImGuiFrameRenders()
    {
        var registry = new AgentBridgeUiReviewRegistry();
        var invoked = 0;
        registry.BeginFrame();
        registry.Register("squire.run", "Run Squire", AgentBridgeUiControlKind.Button, Vector2.Zero, new(100, 30), true, false, "Ready", () => invoked++);
        var reviewedFrame = registry.EndFrame();
        registry.Review("squire.run");

        registry.BeginFrame();
        registry.Register("squire.run", "Run Squire", AgentBridgeUiControlKind.Button, Vector2.Zero, new(100, 30), false, false, "Refreshing", () => { });
        registry.EndFrame();

        var result = registry.Invoke("squire.run", reviewedFrame.FrameId);

        Assert.True(result.Success);
        Assert.Equal(1, invoked);
        Assert.False(registry.Invoke("squire.run", reviewedFrame.FrameId).Success);
    }

    [Fact]
    public void TypedAction_ValidatesArgumentsAndReturnsOperationReceipt()
    {
        var registry = new AgentBridgeUiReviewRegistry();
        string? selectedItem = null;
        var schema = new AgentBridgeActionArgumentSchema([
            new("itemName", AgentBridgeActionArgumentKind.ItemName),
            new("quantity", AgentBridgeActionArgumentKind.Integer, Minimum: 1, Maximum: 99),
        ]);
        registry.BeginFrame();
        registry.Register(
            "stock.retrieve",
            "Retrieve item",
            AgentBridgeUiControlKind.Input,
            Vector2.Zero,
            new(100, 30),
            true,
            false,
            null,
            schema,
            arguments =>
            {
                selectedItem = arguments!.Value.GetProperty("itemName").GetString();
                return AgentBridgeUiActionResult.Ok("Retrieval queued.", "operation-1");
            });
        var frame = registry.EndFrame();

        using var invalid = JsonDocument.Parse("{\"itemName\":\"Potion\",\"quantity\":0}");
        var rejected = registry.Invoke("stock.retrieve", frame.FrameId, invalid.RootElement);
        Assert.False(rejected.Success);
        Assert.Contains("at least 1", rejected.Message);
        Assert.Null(selectedItem);

        using var valid = JsonDocument.Parse("{\"itemName\":\"Potion\",\"quantity\":3}");
        var accepted = registry.Invoke("stock.retrieve", frame.FrameId, valid.RootElement);
        Assert.True(accepted.Success);
        Assert.Equal("Potion", selectedItem);
        Assert.Equal("operation-1", accepted.Action?.OperationId);
    }

    [Fact]
    public void ReviewedActionMetadata_BuildsStableRuntimeCatalog()
    {
        var registry = new AgentBridgeUiReviewRegistry();
        registry.BeginFrame();
        registry.Register(
            "stock.retrieve",
            "Retrieve item",
            AgentBridgeUiControlKind.Input,
            Vector2.Zero,
            new(100, 30),
            true,
            false,
            null,
            arguments: null,
            surfaceId: "stock.main",
            mutating: true,
            completionOperationKind: "inventory-transfer",
            _ => AgentBridgeUiActionResult.Ok("Queued."));
        registry.EndFrame();

        var action = Assert.Single(registry.ActionCatalog());
        Assert.Equal("stock.main", action.SurfaceId);
        Assert.Equal("inventory-transfer", action.CompletionOperationKind);
        Assert.True(registry.CatalogRevision > 1);
    }
}
