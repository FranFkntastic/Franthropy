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
}
