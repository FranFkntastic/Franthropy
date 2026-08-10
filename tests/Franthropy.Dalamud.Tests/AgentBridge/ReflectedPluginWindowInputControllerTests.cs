using Dalamud.Interface.Windowing;
using Franthropy.Dalamud.AgentBridge;
using System.Numerics;

namespace Franthropy.Dalamud.Tests.AgentBridge;

public sealed class ReflectedPluginWindowInputControllerTests
{
    [Fact]
    public async Task SubmitAsync_ClicksInsideRuntimeBoundWindowAcrossFrames()
    {
        var window = new FakeWindow("Encore###EncoreMain") { IsOpen = true };
        var descriptor = Descriptor("runtime-1");
        var sink = new FakeSink(new(descriptor.WindowName!, new(100, 50), new(800, 600), 7));
        using var controller = new ReflectedPluginWindowInputController(
            transaction => transaction == "lease-1" ? new(descriptor, window) : null,
            sink);

        var task = controller.SubmitAsync("lease-1", new(1,
        [
            new(ReflectedPluginWindowInputKind.Click, X: 0.25f, Y: 0.5f),
        ]));
        for (var index = 0; index < 8 && !task.IsCompleted; index++)
            controller.RenderFrame();

        var receipt = await task;
        Assert.Contains("move:300,350:7", sink.Events);
        Assert.Contains("mouse:0:True", sink.Events);
        Assert.Contains("mouse:0:False", sink.Events);
        Assert.Equal("runtime-1", receipt.RuntimeInstanceId);
        Assert.Equal(800, receipt.WindowWidth);
    }

    [Fact]
    public void SubmitAsync_RejectsCoordinatesOutsideWindow()
    {
        var window = new FakeWindow("Test") { IsOpen = true };
        var descriptor = Descriptor("runtime-1") with { WindowName = "Test" };
        using var controller = new ReflectedPluginWindowInputController(
            _ => new(descriptor, window),
            new FakeSink(new("Test", Vector2.Zero, new(100, 100), 1)));

        var error = Assert.Throws<ArgumentException>(() =>
        {
            _ = controller.SubmitAsync("lease", new(1,
            [
                new(ReflectedPluginWindowInputKind.Move, X: 1.1f, Y: 0.5f),
            ]));
        });
        Assert.Contains("between 0 and 1", error.Message);
    }

    [Fact]
    public async Task RenderFrame_RuntimeChangeReleasesHeldMouseButton()
    {
        var runtime = "runtime-1";
        var window = new FakeWindow("Test") { IsOpen = true };
        var sink = new FakeSink(new("Test", Vector2.Zero, new(100, 100), 1));
        using var controller = new ReflectedPluginWindowInputController(
            _ => new(Descriptor(runtime) with { WindowName = "Test" }, window),
            sink);
        var task = controller.SubmitAsync("lease", new(1,
        [
            new(ReflectedPluginWindowInputKind.Drag, X: 0.1f, Y: 0.1f, EndX: 0.9f, EndY: 0.9f, Frames: 4),
        ]));
        controller.RenderFrame();
        controller.RenderFrame();
        controller.RenderFrame();
        runtime = "runtime-2";
        controller.RenderFrame();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
        Assert.Equal("mouse:0:False", sink.Events.Last());
    }

    private static AgentBridgePluginSurfaceDescriptor Descriptor(string runtime) => new(
        "plugin.Encore.window.main",
        "Encore",
        "Encore",
        "Encore",
        AgentBridgePluginSurfaceKind.Window,
        AgentBridgeSurfaceProvenance.ReflectedWindowSystem,
        AgentBridgeSurfaceAuthority.ReversiblePresentation,
        true,
        runtime,
        "Encore",
        "Encore###EncoreMain");

    private sealed class FakeWindow(string name) : Window(name)
    {
        public override void Draw()
        {
        }
    }

    private sealed class FakeSink(ReflectedPluginWindowFrame frame) : IReflectedPluginWindowInputSink
    {
        public List<string> Events { get; } = [];

        public bool TryGetWindow(string windowName, out ReflectedPluginWindowFrame value)
        {
            value = frame;
            return string.Equals(windowName, frame.WindowName, StringComparison.Ordinal);
        }

        public void Move(Vector2 position, uint viewportId) => Events.Add($"move:{position.X:0},{position.Y:0}:{viewportId}");
        public void SetMouseButton(int button, bool down) => Events.Add($"mouse:{button}:{down}");
        public void Scroll(float deltaX, float deltaY) => Events.Add($"scroll:{deltaX}:{deltaY}");
        public void TypeText(string text) => Events.Add($"text:{text}");
        public bool SetKey(string key, bool down)
        {
            Events.Add($"key:{key}:{down}");
            return true;
        }
    }
}
