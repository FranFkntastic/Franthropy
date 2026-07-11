using Franthropy.Dalamud.AgentBridge;

namespace Franthropy.Dalamud.Tests.AgentBridge;

public sealed class AgentBridgeUiCaptureTransactionManagerTests
{
    [Fact]
    public async Task ReadyAndCompleteRestorePriorWindowState()
    {
        var open = false;
        var collapsed = true;
        var manager = CreateManager(() => open, value => open = value, () => collapsed, value => collapsed = value);

        var handle = manager.Begin("main-window");
        Assert.True(open);
        Assert.False(collapsed);
        Assert.True(manager.ShouldPresentInMainViewport("main-window"));

        manager.MarkRendered("main-window", 42);
        var receipt = await handle.Ready;
        Assert.Equal(handle.TransactionId, receipt.TransactionId);
        Assert.Equal(42, receipt.FrameId);

        var result = manager.Complete(handle.TransactionId);
        Assert.True(result.Success);
        Assert.False(open);
        Assert.False(manager.ShouldPresentInMainViewport("main-window"));
    }

    [Fact]
    public void MismatchedCompletionFailsWithoutClearingActiveTransaction()
    {
        var open = true;
        var collapsed = false;
        var manager = CreateManager(() => open, value => open = value, () => collapsed, value => collapsed = value);
        var handle = manager.Begin("main-window");

        var result = manager.Complete("wrong-transaction");

        Assert.False(result.Success);
        Assert.True(manager.ShouldPresentInMainViewport("main-window"));
        Assert.True(manager.Cancel(handle.TransactionId).Success);
    }

    [Fact]
    public async Task ExpirationRestoresStateAndFailsReadyWaiter()
    {
        var open = false;
        var collapsed = false;
        var manager = CreateManager(() => open, value => open = value, () => collapsed, value => collapsed = value, TimeSpan.FromMilliseconds(1));
        var handle = manager.Begin("main-window");

        await Task.Delay(20);
        Assert.False(manager.ShouldPresentInMainViewport("main-window"));
        await Assert.ThrowsAsync<TimeoutException>(() => handle.Ready);
        Assert.False(open);
    }

    [Fact]
    public void PresentationHooksRunExactlyOnceAroundTerminalPath()
    {
        var open = false;
        var collapsed = false;
        var began = 0;
        var restored = 0;
        var manager = new AgentBridgeUiCaptureTransactionManager(
            () => open,
            value => open = value,
            () => collapsed,
            value => collapsed = value,
            beginPresentation: () => began++,
            restorePresentation: () => restored++);

        var handle = manager.Begin("main-window");
        Assert.Equal(1, began);
        Assert.Equal(0, restored);

        Assert.True(manager.Cancel(handle.TransactionId).Success);
        Assert.Equal(1, restored);
        Assert.False(manager.Cancel(handle.TransactionId).Success);
        Assert.Equal(1, restored);
    }

    private static AgentBridgeUiCaptureTransactionManager CreateManager(
        Func<bool> isOpen,
        Action<bool> setOpen,
        Func<bool> isCollapsed,
        Action<bool> setCollapsed,
        TimeSpan? lifetime = null) => new(isOpen, setOpen, isCollapsed, setCollapsed, lifetime);
}
