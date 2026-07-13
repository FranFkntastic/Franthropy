using Franthropy.Dalamud.Automation.Coordination;

namespace Franthropy.Dalamud.Tests.Automation.Coordination;

public sealed class ExternalAutomationInterlockTests
{
    [Fact]
    public void UnavailableAutomation_IsSuccessfulNoOp()
    {
        var adapter = new FakeAdapter { IsAvailable = false };

        var result = new ExternalAutomationInterlock(adapter).InterruptIfRunning();

        Assert.True(result.Success);
        Assert.Equal("Unavailable", result.Code);
        Assert.Equal(0, adapter.InterruptCount);
    }

    [Fact]
    public void IdleAutomation_IsSuccessfulNoOp()
    {
        var adapter = new FakeAdapter { IsRunning = false };

        var result = new ExternalAutomationInterlock(adapter).InterruptIfRunning();

        Assert.True(result.Success);
        Assert.Equal("AlreadyIdle", result.Code);
        Assert.Equal(0, adapter.InterruptCount);
    }

    [Fact]
    public void RunningAutomation_IsInterruptedAndVerified()
    {
        var adapter = new FakeAdapter { IsRunning = true };

        var result = new ExternalAutomationInterlock(adapter).InterruptIfRunning();

        Assert.True(result.Success);
        Assert.True(result.WasRunning);
        Assert.True(result.Interrupted);
        Assert.Equal("Interrupted", result.Code);
        Assert.Equal(1, adapter.InterruptCount);
        Assert.False(adapter.IsRunning);
    }

    [Fact]
    public void StateFailure_FailsWithoutInterrupting()
    {
        var adapter = new FakeAdapter { QuerySucceeds = false };

        var result = new ExternalAutomationInterlock(adapter).InterruptIfRunning();

        Assert.False(result.Success);
        Assert.Equal("StateUnavailable", result.Code);
        Assert.Equal(0, adapter.InterruptCount);
    }

    [Fact]
    public void InterruptFailure_IsExplicit()
    {
        var adapter = new FakeAdapter { IsRunning = true, InterruptSucceeds = false };

        var result = new ExternalAutomationInterlock(adapter).InterruptIfRunning();

        Assert.False(result.Success);
        Assert.Equal("InterruptFailed", result.Code);
        Assert.Equal(1, adapter.InterruptCount);
    }

    [Fact]
    public void UnobservedInterrupt_IsExplicit()
    {
        var adapter = new FakeAdapter { IsRunning = true, RemainRunningAfterInterrupt = true };

        var result = new ExternalAutomationInterlock(adapter).InterruptIfRunning();

        Assert.False(result.Success);
        Assert.Equal("InterruptNotObserved", result.Code);
        Assert.Equal(1, adapter.InterruptCount);
    }

    private sealed class FakeAdapter : IInterruptibleAutomationAdapter
    {
        public string DisplayName => "Test automation";
        public bool IsAvailable { get; set; } = true;
        public bool IsRunning { get; set; }
        public bool QuerySucceeds { get; set; } = true;
        public bool InterruptSucceeds { get; set; } = true;
        public bool RemainRunningAfterInterrupt { get; set; }
        public int InterruptCount { get; private set; }

        public bool TryGetRunning(out bool isRunning, out string? error)
        {
            isRunning = IsRunning;
            error = QuerySucceeds ? null : "Query failed.";
            return QuerySucceeds;
        }

        public bool TryInterrupt(out string? error)
        {
            InterruptCount++;
            error = InterruptSucceeds ? null : "Interrupt failed.";
            if (InterruptSucceeds && !RemainRunningAfterInterrupt)
                IsRunning = false;
            return InterruptSucceeds;
        }
    }
}
