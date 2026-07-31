using Franthropy.Dalamud.AgentBridge;

namespace Franthropy.Dalamud.Tests.AgentBridge;

public sealed class AgentBridgeUiCaptureTransactionManagerTests
{
    [Fact]
    public async Task ReadyAndCompleteRestorePriorWindowState()
    {
        var open = false;
        var collapsed = true;
        bool? forcedCollapsed = null;
        var manager = CreateManager(
            () => open,
            value => open = value,
            () => collapsed,
            value =>
            {
                forcedCollapsed = value;
                collapsed = value;
            },
            (wasOpen, wasCollapsed) =>
            {
                forcedCollapsed = null;
                if (wasOpen)
                    collapsed = wasCollapsed;
            });

        var handle = manager.Begin("main-window");
        Assert.True(open);
        Assert.False(collapsed);
        Assert.False(forcedCollapsed);
        Assert.True(manager.ShouldPresentInMainViewport("main-window"));

        for (var frameId = 42; frameId <= 47; frameId++)
            manager.MarkRendered("main-window", frameId);
        var receipt = await handle.Ready;
        Assert.Equal(handle.TransactionId, receipt.TransactionId);
        Assert.Equal(47, receipt.FrameId);

        var result = manager.Complete(handle.TransactionId);
        Assert.True(result.Success);
        Assert.False(open);
        Assert.Null(forcedCollapsed);
        Assert.False(manager.ShouldPresentInMainViewport("main-window"));
    }

    [Fact]
    public void CompletionAlwaysRestoresCollapsePresentationEvenWhenWindowWasClosed()
    {
        var open = false;
        var collapsed = false;
        (bool WasOpen, bool WasCollapsed)? restored = null;
        var manager = CreateManager(
            () => open,
            value => open = value,
            () => collapsed,
            value => collapsed = value,
            (wasOpen, wasCollapsed) => restored = (wasOpen, wasCollapsed));

        var handle = manager.Begin("main-window");
        Assert.True(manager.Complete(handle.TransactionId).Success);

        Assert.Equal((false, false), restored);
    }

    [Fact]
    public void ReadyWaitsForSettledRenderPasses()
    {
        var open = false;
        var collapsed = false;
        var manager = CreateManager(() => open, value => open = value, () => collapsed, value => collapsed = value);
        var handle = manager.Begin("main-window");

        for (var frame = 0; frame < 5; frame++)
            manager.MarkRendered("main-window", 5);

        Assert.False(handle.Ready.IsCompleted);
        manager.MarkRendered("main-window", 6);
        Assert.True(handle.Ready.IsCompletedSuccessfully);
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
        var manager = CreateManager(
            () => open,
            value => open = value,
            () => collapsed,
            value => collapsed = value,
            lifetime: TimeSpan.FromMilliseconds(1));
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
            (_, _) => { },
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
        Action<bool, bool>? restoreCollapsed = null,
        TimeSpan? lifetime = null) =>
        new(
            isOpen,
            setOpen,
            isCollapsed,
            setCollapsed,
            restoreCollapsed ?? ((wasOpen, wasCollapsed) =>
            {
                if (wasOpen)
                    setCollapsed(wasCollapsed);
            }),
            lifetime);
}
