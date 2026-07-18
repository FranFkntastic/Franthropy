using Franthropy.Dalamud.UI.Filtering;
using Franthropy.Filtering.Completion;
using Franthropy.Filtering.Syntax;

namespace Franthropy.Dalamud.Tests.UI.Filtering;

public sealed class DalamudFilterAutocompleteStateTests
{
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
    public void FocusRequestPreservesTheCurrentCaret()
    {
        var state = new DalamudFilterAutocompleteState();
        state.SetExpression("is:h", 4);

        state.RequestFocus();

        Assert.Equal(4, state.CaretPosition);
    }
}
