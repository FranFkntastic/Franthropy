using Dalamud.Interface.Windowing;
using Franthropy.Dalamud.AgentBridge;

namespace Franthropy.Dalamud.Tests.AgentBridge;

public sealed class ReflectedPluginWindowPresentationManagerTests
{
    [Fact]
    public void BeginAndRestore_RoundTripsExactPresentationState()
    {
        var window = new FakeWindow("Settings")
        {
            IsOpen = false,
            IsFocused = false,
            Collapsed = true,
            RequestFocus = false,
            Position = new(12, 24),
            Size = new(640, 480),
        };
        var manager = CreateManager(window, "runtime-1");

        var receipt = manager.Begin("plugin.Fake.window.1");

        Assert.False(receipt.Before.IsOpen);
        Assert.True(receipt.Presented.IsOpen);
        Assert.True(window.IsOpen);
        Assert.False(window.Collapsed);
        Assert.True(window.RequestFocus);
        window.IsFocused = true;
        window.Position = new(100, 200);
        window.Size = new(800, 600);

        var restored = manager.Restore(receipt.TransactionId);

        Assert.True(restored.Success);
        Assert.False(window.IsOpen);
        Assert.True(window.Collapsed);
        Assert.False(window.RequestFocus);
        Assert.False(window.IsFocused);
        Assert.Equal(new(12, 24), window.Position);
        Assert.Equal(new(640, 480), window.Size);
    }

    [Fact]
    public void MismatchedTransaction_DoesNotDiscardActiveLease()
    {
        var window = new FakeWindow("Settings");
        var manager = CreateManager(window, "runtime-1");
        var receipt = manager.Begin("plugin.Fake.window.1");

        Assert.False(manager.Restore("wrong").Success);
        Assert.True(window.IsOpen);
        Assert.True(manager.Restore(receipt.TransactionId).Success);
    }

    [Fact]
    public void Expiry_RestoresPriorState()
    {
        var window = new FakeWindow("Settings") { IsOpen = false, Collapsed = true };
        var manager = CreateManager(window, "runtime-1", TimeSpan.FromMilliseconds(1));
        var receipt = manager.Begin("plugin.Fake.window.1");

        var result = manager.Expire(receipt.ExpiresAtUtc.AddMilliseconds(1));

        Assert.True(result?.Success);
        Assert.False(window.IsOpen);
        Assert.True(window.Collapsed);
    }

    [Fact]
    public void RuntimeReplacement_IsNeverMutatedDuringRestore()
    {
        var original = new FakeWindow("Settings") { IsOpen = false };
        var replacement = new FakeWindow("Settings") { IsOpen = false };
        var runtime = "runtime-1";
        var current = original;
        var manager = new ReflectedPluginWindowPresentationManager(_ =>
            new ReflectedPluginWindowPresentationTarget(Descriptor(runtime, current), current));
        var receipt = manager.Begin("plugin.Fake.window.1");
        current = replacement;
        runtime = "runtime-2";

        var result = manager.Restore(receipt.TransactionId);

        Assert.False(result.Success);
        Assert.True(original.IsOpen);
        Assert.False(replacement.IsOpen);
        Assert.Contains("replacement", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetActiveTarget_ReturnsCurrentWindowOnlyForOriginalRuntime()
    {
        var original = new FakeWindow("Settings");
        var replacement = new FakeWindow("Settings");
        var runtime = "runtime-1";
        var current = original;
        var manager = new ReflectedPluginWindowPresentationManager(_ =>
            new ReflectedPluginWindowPresentationTarget(Descriptor(runtime, current), current));
        var receipt = manager.Begin("plugin.Fake.window.1");

        Assert.Same(original, manager.GetActiveTarget(receipt.TransactionId)?.Window);

        current = replacement;
        runtime = "runtime-2";

        Assert.Null(manager.GetActiveTarget(receipt.TransactionId));
    }

    private static ReflectedPluginWindowPresentationManager CreateManager(
        IWindow window,
        string runtime,
        TimeSpan? lifetime = null) =>
        new(_ => new ReflectedPluginWindowPresentationTarget(Descriptor(runtime, window), window), lifetime);

    private static AgentBridgePluginSurfaceDescriptor Descriptor(string runtime, IWindow window) => new(
        "plugin.Fake.window.1",
        "Fake",
        "Fake Plugin",
        "Settings",
        AgentBridgePluginSurfaceKind.Window,
        AgentBridgeSurfaceProvenance.ReflectedWindowSystem,
        AgentBridgeSurfaceAuthority.ReversiblePresentation,
        true,
        runtime,
        "Fake",
        window.WindowName,
        window.IsOpen,
        window.IsFocused,
        window.Collapsed);

    private sealed class FakeWindow(string name) : Window(name)
    {
        public override void Draw()
        {
        }
    }
}
