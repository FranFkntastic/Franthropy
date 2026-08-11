using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.UI.Tables;
using System.Reflection;

namespace Franthropy.Dalamud.Tests.UI.Tables;

public sealed class DalamudTableLayoutTests
{
    [Fact]
    public void Fit_content_preserves_the_callers_exact_table_behavior()
    {
        const ImGuiTableFlags flags =
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.BordersOuter |
            ImGuiTableFlags.BordersInnerH;

        var layout = DalamudTableLayout.FitContent(flags);

        Assert.Equal(flags, layout.Flags);
        Assert.Equal(0f, layout.Size.X);
        Assert.Equal(0f, layout.Size.Y);
        Assert.Equal(0, layout.FreezeColumns);
        Assert.Equal(0, layout.FreezeRows);
    }

    [Theory]
    [InlineData(false, false, false, DalamudTableRowBackground.None)]
    [InlineData(true, false, false, DalamudTableRowBackground.Selected)]
    [InlineData(true, true, false, DalamudTableRowBackground.Hovered)]
    [InlineData(true, true, true, DalamudTableRowBackground.Active)]
    public void Row_background_uses_interaction_precedence_without_overlay_selection(
        bool selected,
        bool hovered,
        bool active,
        DalamudTableRowBackground expected)
    {
        Assert.Equal(
            expected,
            DalamudTableSelectionRenderer.ResolveBackground(selected, hovered, active));
    }

    [Fact]
    public void Projection_is_reused_until_its_source_identity_changes()
    {
        var table = new DalamudTableProjection<int>(
        [
            new("Value", 80f, value => value.ToString(), value => value),
        ]);
        int[] rows = [3, 1, 2];

        var first = table.Apply(rows);
        var second = table.Apply(rows);

        Assert.Same(first, second);
        Assert.Equal(1, table.ApplyCount);

        var replacement = rows.ToArray();
        var third = table.Apply(replacement);

        Assert.NotSame(first, third);
        Assert.Equal(2, table.ApplyCount);
    }

    [Fact]
    public void Projection_owns_one_table_level_selection_mode()
    {
        var selection = new TableSelectionModel<int>();
        var table = new DalamudTableProjection<int>(
            [new("Value", 80f, value => value.ToString())],
            DalamudTableSelection<int>.Single(selection, value => value));

        Assert.Equal(DalamudTableSelectionMode.Single, table.SelectionMode);
    }

    [Fact]
    public void Stable_column_identity_does_not_change_with_the_visible_label()
    {
        var first = DalamudTableProjection<int>.StableColumnId("listing-shortfall");
        var second = DalamudTableProjection<int>.StableColumnId("listing-shortfall");
        var renamedLabel = DalamudTableProjection<int>.StableColumnId("Listing demand");

        Assert.Equal(first, second);
        Assert.NotEqual(first, renamedLabel);
        Assert.NotEqual(0u, first);
    }

    [Fact]
    public void Column_contract_carries_stable_identity_and_contextual_help()
    {
        var column = new DalamudTableColumn<int>(
            "Listing shortfall",
            120f,
            value => value.ToString(),
            Id: "listing-shortfall",
            HeaderTooltip: "Units still needed by linked Listing Plans.");

        Assert.Equal("listing-shortfall", column.Id);
        Assert.Equal("Units still needed by linked Listing Plans.", column.HeaderTooltip);
    }

    [Fact]
    public void Projection_rejects_duplicate_stable_column_identity()
    {
        var exception = Assert.Throws<ArgumentException>(() => new DalamudTableProjection<int>(
        [
            new("First label", 80f, value => value.ToString(), Id: "quantity"),
            new("Renamed label", 80f, value => value.ToString(), Id: "quantity"),
        ]));

        Assert.Contains("quantity", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_action_column_uses_its_position_as_a_stable_fallback()
    {
        var table = new DalamudTableProjection<int>(
        [
            new("Item", 80f, value => value.ToString()),
            new("", 28f, _ => string.Empty),
        ]);

        Assert.Equal(2, table.ColumnCount);
    }

    [Fact]
    public void Header_label_honors_no_header_label_without_discarding_the_column_name()
    {
        var column = new DalamudTableColumn<int>(
            "Remove",
            28f,
            _ => string.Empty,
            Flags: ImGuiTableColumnFlags.NoHeaderLabel);

        Assert.Equal(string.Empty, DalamudTableProjection<int>.HeaderLabel(column));
        Assert.Equal("Remove", column.Label);
    }

    [Theory]
    [InlineData(nameof(DalamudTableProjection<int>.DrawRow))]
    [InlineData(nameof(DalamudTableProjection<int>.DrawSelectableRow))]
    public void Legacy_row_entry_points_are_explicitly_deprecated(string methodName)
    {
        var methods = typeof(DalamudTableProjection<int>)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == methodName);

        Assert.All(methods, method => Assert.NotNull(method.GetCustomAttribute<ObsoleteAttribute>()));
    }
}
