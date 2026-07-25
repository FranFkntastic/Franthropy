using Franthropy.Dalamud.UI.Windows;

namespace Franthropy.Dalamud.Tests.UI.Windows;

public sealed class CompanionWindowActivationStateTests
{
    [Fact]
    public void Toggle_WhenClosed_OpensAndRequestsFocusOnce()
    {
        var state = new CompanionWindowActivationState();

        Assert.True(state.Toggle(isOpen: false));
        Assert.True(state.ConsumeFocusRequest());
        Assert.False(state.ConsumeFocusRequest());
    }

    [Fact]
    public void Toggle_WhenOpen_ClosesWithoutRequestingFocus()
    {
        var state = new CompanionWindowActivationState();

        Assert.False(state.Toggle(isOpen: true));
        Assert.False(state.ConsumeFocusRequest());
    }
}
