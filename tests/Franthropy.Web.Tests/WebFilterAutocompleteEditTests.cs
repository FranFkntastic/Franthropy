using Franthropy.Filtering.Completion;
using Franthropy.Filtering.Syntax;
using Franthropy.Web.Filtering;
using Xunit;

namespace Franthropy.Web.Tests;

public sealed class WebFilterAutocompleteEditTests
{
    [Fact]
    public void TryApply_ReplacesOnlyTheCompletionSpanAndReturnsTheNewCaret()
    {
        var completion = new FilterCompletionItem(
            "!=",
            "!=",
            FilterCompletionKind.Operator,
            new TextSpan(8, 1));

        var applied = WebFilterAutocompleteEdit.TryApply("quantity! darksteel", completion, out var edit);

        Assert.True(applied);
        Assert.Equal("quantity!= darksteel", edit.Value);
        Assert.Equal(10, edit.CaretPosition);
        Assert.True(edit.IsCompletion);
    }

    [Fact]
    public void TryApply_RejectsAStaleSpanWithoutMutatingTheExpression()
    {
        var completion = new FilterCompletionItem(
            ":",
            ":",
            FilterCompletionKind.Operator,
            new TextSpan(99, 0));

        var applied = WebFilterAutocompleteEdit.TryApply("quality", completion, out var edit);

        Assert.False(applied);
        Assert.Equal("quality", edit.Value);
    }
}
