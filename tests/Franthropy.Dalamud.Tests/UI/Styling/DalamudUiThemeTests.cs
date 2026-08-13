using System.Numerics;
using Franthropy.Dalamud.UI.Styling;

namespace Franthropy.Dalamud.Tests.UI.Styling;

public sealed class DalamudUiThemeTests
{
    [Fact]
    public void Dark_theme_defaults_to_static_compact_rendering()
    {
        var theme = DalamudUiTheme.Dark(new Vector4(0.35f, 0.55f, 0.92f, 1f));

        Assert.Equal(DalamudUiMotionLevel.Off, theme.Motion.Level);
        Assert.Equal(0f, theme.Motion.Resolve(theme.Motion.HoverSeconds));
        Assert.Equal(DalamudUiMetrics.For(DalamudUiDensity.Compact), theme.Metrics);
    }

    [Fact]
    public void Theme_roles_can_be_overridden_without_rebuilding_other_layers()
    {
        var original = DalamudUiTheme.Dark(
            new Vector4(0.35f, 0.55f, 0.92f, 1f),
            DalamudUiDensity.Comfortable,
            DalamudUiMotionLevel.Subtle);
        var replacementPalette = original.Palette with
        {
            Warning = new Vector4(1f, 0.5f, 0.1f, 1f),
        };

        var changed = original with { Palette = replacementPalette };

        Assert.Equal(original.Metrics, changed.Metrics);
        Assert.Equal(original.Motion, changed.Motion);
        Assert.NotEqual(original.Palette.Warning, changed.Palette.Warning);
    }
}
