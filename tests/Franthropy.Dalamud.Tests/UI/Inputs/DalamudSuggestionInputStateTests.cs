using Franthropy.Dalamud.UI.Inputs;

namespace Franthropy.Dalamud.Tests.UI.Inputs;

public sealed class DalamudSuggestionInputStateTests
{
    [Fact]
    public void HighlightWrapsInBothDirectionsAndClampsAfterShrink()
    {
        var state = new DalamudSuggestionInputState();
        state.Move(-1, 9);
        Assert.Equal(8, state.HighlightedIndex);
        state.Move(1, 9);
        Assert.Equal(0, state.HighlightedIndex);
        state.Move(8, 9);
        state.Clamp(2);
        Assert.Equal(1, state.HighlightedIndex);
        state.Clamp(0);
        Assert.Equal(0, state.HighlightedIndex);
    }

    [Fact]
    public void PickingReturnsHighlightedValueClearsTextAndDeactivates()
    {
        var state = new DalamudSuggestionInputState { Text = "ore", Active = true };
        DalamudSuggestion<int>[] choices = [new(1, "First"), new(2, "Second", "3 here")];
        state.Move(1, choices.Length);
        Assert.True(state.TryPick(choices, out var value));
        Assert.Equal(2, value);
        Assert.Equal(string.Empty, state.Text);
        Assert.False(state.Active);
        Assert.Equal(0, state.HighlightedIndex);
    }

    [Fact]
    public void EmptyListDoesNotConsumeInput()
    {
        var state = new DalamudSuggestionInputState { Text = "ore", Active = true };
        Assert.False(state.TryPick<int>([], out _));
        Assert.Equal("ore", state.Text);
        Assert.True(state.Active);
    }
}
