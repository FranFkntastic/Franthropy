using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.UI.Tables;

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
}
