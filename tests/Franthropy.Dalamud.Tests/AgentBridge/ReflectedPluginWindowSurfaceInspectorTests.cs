using Dalamud.Interface.Windowing;
using Franthropy.Dalamud.AgentBridge;

namespace Franthropy.Dalamud.Tests.AgentBridge;

public sealed class ReflectedPluginWindowSurfaceInspectorTests
{
    [Fact]
    public void Inspect_FindsSharedWindowSystemWithoutInvokingPluginProperties()
    {
        var windowSystem = new WindowSystem("FakePlugin");
        var window = new FakeWindow("Settings###stable-id") { IsOpen = true, IsFocused = false, Collapsed = false };
        windowSystem.AddWindow(window);
        var plugin = new FakePlugin(windowSystem);

        var inspector = new ReflectedPluginWindowSurfaceInspector();
        var surfaces = inspector.Inspect(
            plugin,
            "FakePlugin",
            "Fake Plugin",
            "runtime-1");

        var surface = Assert.Single(surfaces);
        Assert.Equal("Settings", surface.Label);
        Assert.Equal(AgentBridgeSurfaceProvenance.ReflectedWindowSystem, surface.Provenance);
        Assert.Equal(AgentBridgeSurfaceAuthority.ReversiblePresentation, surface.Authority);
        Assert.True(surface.IsOpen);
        Assert.False(surface.IsFocused);
        Assert.False(plugin.PropertyWasRead);
        Assert.True(inspector.TryResolveWindow(plugin, "FakePlugin", surface.Id, out var resolved));
        Assert.Same(window, resolved);
    }

    [Fact]
    public void Inspect_FindsWindowSystemInsideBoundedPluginOwnedManager()
    {
        var windowSystem = new WindowSystem("NestedPlugin");
        windowSystem.AddWindow(new FakeWindow("Workbench"));
        var plugin = new NestedPlugin(new PluginOwnedWindowManager(windowSystem));

        var surface = Assert.Single(new ReflectedPluginWindowSurfaceInspector().Inspect(
            plugin,
            "NestedPlugin",
            "Nested Plugin",
            "runtime-2"));

        Assert.Equal("Workbench", surface.Label);
    }

    [Fact]
    public void Inspect_FindsDirectPluginOwnedWindowWithoutWindowSystem()
    {
        var plugin = new DirectWindowPlugin(new FakeWindow("Direct Window"));

        var surface = Assert.Single(new ReflectedPluginWindowSurfaceInspector().Inspect(
            plugin,
            "DirectPlugin",
            "Direct Plugin",
            "runtime-3"));

        Assert.Equal("Direct Window", surface.Label);
        Assert.Equal(AgentBridgeSurfaceAuthority.ReversiblePresentation, surface.Authority);
    }

    private sealed class FakePlugin(IWindowSystem windowSystem)
    {
        private readonly IWindowSystem windows = windowSystem;

        public object DangerousProperty
        {
            get
            {
                PropertyWasRead = true;
                throw new InvalidOperationException("Properties must never be inspected.");
            }
        }

        public bool PropertyWasRead { get; private set; }
    }

    private sealed class FakeWindow(string name) : Window(name)
    {
        public override void Draw()
        {
        }
    }

    private sealed class NestedPlugin(PluginOwnedWindowManager manager)
    {
        private readonly PluginOwnedWindowManager windowManager = manager;
    }

    private sealed class PluginOwnedWindowManager(IWindowSystem windows)
    {
        private readonly IWindowSystem windowSystem = windows;
    }

    private sealed class DirectWindowPlugin(IWindow window)
    {
        private readonly IWindow mainWindow = window;
    }
}
