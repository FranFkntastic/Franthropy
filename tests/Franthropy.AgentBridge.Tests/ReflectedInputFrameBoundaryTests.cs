namespace Franthropy.AgentBridge.Tests;

public sealed class ReflectedInputFrameBoundaryTests
{
    [Fact]
    public void NativePumpUsesPreFrameHookAndRetainsDelegateThroughRemoval()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Franthropy.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root.FullName, "src", "Franthropy.Dalamud", "AgentBridge", "ReflectedPluginWindowInputFramePump.cs"));
        Assert.Contains("ImGuiContextHookType.NewFramePre", source);
        Assert.Contains("ImGuiContextHookType.Shutdown", source);
        Assert.Contains("readonly ImGuiContextHookCallback callback", source);
        Assert.Contains("ImGuiP.RemoveContextHook", source);
        Assert.Contains("GC.KeepAlive(callback)", source);
        Assert.DoesNotContain("MouseClicked[", source);
        Assert.DoesNotContain("SetCursorPos", source);
    }
}
