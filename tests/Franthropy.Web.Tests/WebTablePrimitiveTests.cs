using Franthropy.Web.Tables;
using Xunit;

namespace Franthropy.Web.Tests;

public sealed class WebTablePrimitiveTests
{
    [Fact]
    public void SortState_Toggle_UsesAscendingThenDescendingCycle()
    {
        var first = WebTableSortState<TestColumn>.Unsorted.Toggle(TestColumn.Name);
        var second = first.Toggle(TestColumn.Name);
        var switched = second.Toggle(TestColumn.Quantity);

        Assert.Equal(TestColumn.Name, first.Column);
        Assert.False(first.Descending);
        Assert.Equal("ascending", first.GetAriaSort(TestColumn.Name));
        Assert.True(second.Descending);
        Assert.Equal("descending", second.GetAriaSort(TestColumn.Name));
        Assert.Equal(TestColumn.Quantity, switched.Column);
        Assert.False(switched.Descending);
    }

    [Fact]
    public void Ordering_AppliesTypedRuleAndStableTieBreaker()
    {
        var rows = new[]
        {
            new TestRow("b", "Bronze", 10),
            new TestRow("a", "Adamantite", 10),
            new TestRow("c", "Copper", 5),
        };
        var rules = new[]
        {
            WebTableSortRule<TestRow, TestColumn>.Create(TestColumn.Quantity, row => row.Quantity),
        };

        var ordered = WebTableOrdering.Apply(
            rows,
            new WebTableSortState<TestColumn>(TestColumn.Quantity, Descending: true),
            rules,
            items => items.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
            items => items.ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase));

        Assert.Equal(["Adamantite", "Bronze", "Copper"], ordered.Select(row => row.Name));
    }

    [Fact]
    public void Selection_RepresentsOneStableRowKey()
    {
        var selection = WebTableSelection<string>.Single("node-2");

        Assert.True(selection.IsSelected("node-2"));
        Assert.False(selection.IsSelected("node-1"));
        Assert.False(WebTableSelection<string>.None.IsSelected("node-2"));
    }

    private enum TestColumn { Name, Quantity }
    private sealed record TestRow(string Key, string Name, int Quantity);
}
