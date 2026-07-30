using Franthropy.Dalamud.UI.Filtering;
using Franthropy.Filtering.Completion;
using Franthropy.Filtering.Syntax;

namespace Franthropy.Dalamud.Tests.UI.Filtering;

public sealed class DalamudFilterAutocompleteStateTests
{
    [Fact]
    public void DownMovesToTheNextSuggestion()
    {
        var input = DalamudFilterAutocompleteInput.Decide(
            inputActive: true,
            suggestionCount: 3,
            downPressed: true,
            upPressed: false,
            tabPressed: false);

        Assert.Equal(1, input.SelectionDelta);
        Assert.False(input.ApplyCompletion);
        Assert.False(input.KeepInputFocused);
    }

    [Fact]
    public void UpMovesToThePreviousSuggestion()
    {
        var input = DalamudFilterAutocompleteInput.Decide(
            inputActive: true,
            suggestionCount: 3,
            downPressed: false,
            upPressed: true,
            tabPressed: false);

        Assert.Equal(-1, input.SelectionDelta);
        Assert.False(input.ApplyCompletion);
        Assert.False(input.KeepInputFocused);
    }

    [Fact]
    public void CompletionCallbackAppliesTheSelectedSuggestion()
    {
        var state = new DalamudFilterAutocompleteState();
        state.SetExpression("is:h", 4);
        var completion = new FilterCompletionItem(
            "hq",
            "hq",
            FilterCompletionKind.Value,
            new TextSpan(3, 1));
        var input = DalamudFilterAutocompleteInput.Decide(
            inputActive: true,
            suggestionCount: 1,
            downPressed: false,
            upPressed: false,
            tabPressed: true);

        Assert.True(input.ApplyCompletion);
        Assert.True(input.KeepInputFocused);
        Assert.True(state.TryApply([completion]));
        Assert.Equal("is:hq", state.Expression);
        Assert.Equal(5, state.CaretPosition);
    }

    [Fact]
    public void TabWithoutSuggestionsKeepsTheInputFocusedWithoutChangingText()
    {
        var state = new DalamudFilterAutocompleteState();
        state.SetExpression("quality:h", 9);
        var input = DalamudFilterAutocompleteInput.Decide(
            inputActive: true,
            suggestionCount: 0,
            downPressed: false,
            upPressed: false,
            tabPressed: true);

        if (input.KeepInputFocused)
            state.RequestFocus();

        Assert.False(input.ApplyCompletion);
        Assert.Equal("quality:h", state.Expression);
        Assert.Equal(9, state.CaretPosition);
    }

    [Fact]
    public void InputFlagsConsumeTabForCompletionInsteadOfFocusTraversal()
    {
        Assert.True(DalamudFilterAutocompleteRenderer.InputFlagsConsumeTab);
    }

    [Fact]
    public void SuggestionWindowRemainsMouseSelectable()
    {
        Assert.True(DalamudFilterAutocompleteRenderer.SuggestionWindowAllowsMouseSelection);
        Assert.True(DalamudFilterAutocompleteRenderer.SuggestionWindowAllowsScrolling);
        Assert.True(DalamudFilterAutocompleteRenderer.SuggestionWindowUsesInteractivePopupStyle);
        Assert.True(DalamudFilterAutocompleteRenderer.MaximumVisibleItems > 8);
        Assert.True(DalamudFilterAutocompleteRenderer.DefaultMaximumItems >= 9);
    }

    [Fact]
    public void AppliesCompletionAtCaretWithoutReplacingSurroundingExpression()
    {
        var state = new DalamudFilterAutocompleteState();
        state.SetExpression("quality:h AND location:armoury", 9);
        var completion = new FilterCompletionItem(
            "hq",
            "hq",
            FilterCompletionKind.Value,
            new TextSpan(8, 1));

        Assert.True(state.TryApply([completion]));
        Assert.Equal("quality:hq AND location:armoury", state.Expression);
        Assert.Equal(10, state.CaretPosition);
    }

    [Fact]
    public void SelectionWrapsInBothDirections()
    {
        var state = new DalamudFilterAutocompleteState();

        state.MoveSelection(-1, 3);
        Assert.Equal(2, state.SelectedIndex);

        state.MoveSelection(1, 3);
        Assert.Equal(0, state.SelectedIndex);
    }

    [Fact]
    public void EditingPlainTextPreservesExpressionAndCaretUntilCompletionIsApplied()
    {
        var state = new DalamudFilterAutocompleteState();
        state.SetExpression("darksteel or", 12);
        var completion = new FilterCompletionItem(
            "offer.price",
            "offer.price",
            FilterCompletionKind.Field,
            new TextSpan(10, 2));

        state.MoveSelection(1, 3);

        Assert.Equal("darksteel or", state.Expression);
        Assert.Equal(12, state.CaretPosition);

        Assert.True(state.TryApply([completion]));
        Assert.Equal("darksteel offer.price", state.Expression);
        Assert.Equal(21, state.CaretPosition);
    }

    [Fact]
    public void FocusRequestPreservesTheCurrentCaret()
    {
        var state = new DalamudFilterAutocompleteState();
        state.SetExpression("is:h", 4);

        state.RequestFocus();

        Assert.Equal(4, state.CaretPosition);
    }
}
