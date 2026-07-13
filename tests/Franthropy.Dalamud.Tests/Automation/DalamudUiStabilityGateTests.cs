using Franthropy.Dalamud.Automation;

namespace Franthropy.Dalamud.Tests.Automation;

public sealed class DalamudUiStabilityGateTests
{
    [Fact]
    public void Observe_RequiresConsecutiveReadyFrames()
    {
        var gate = new DalamudUiStabilityGate(3);

        Assert.False(gate.Observe(true));
        Assert.False(gate.Observe(true));
        Assert.True(gate.Observe(true));
        Assert.True(gate.Observe(true));
    }

    [Fact]
    public void Observe_NotReady_ResetsProgress()
    {
        var gate = new DalamudUiStabilityGate(3);

        Assert.False(gate.Observe(true));
        Assert.False(gate.Observe(true));
        Assert.False(gate.Observe(false));
        Assert.Equal(0, gate.ObservedConsecutiveFrames);
        Assert.False(gate.Observe(true));
        Assert.False(gate.Observe(true));
        Assert.True(gate.Observe(true));
    }

    [Fact]
    public void Constructor_RejectsImpossibleFrameCount() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DalamudUiStabilityGate(0));
}
