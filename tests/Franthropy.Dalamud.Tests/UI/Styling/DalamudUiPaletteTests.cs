using System.Numerics;
using Franthropy.Dalamud.UI.Styling;

namespace Franthropy.Dalamud.Tests.UI.Styling;

public sealed class DalamudUiPaletteTests
{
    [Fact]
    public void Tone_surfaces_preserve_surface_alpha_and_clamp_strength()
    {
        var palette = DalamudUiPalette.Dark(new Vector4(0.25f, 0.50f, 0.75f, 1f));

        Assert.Equal(palette.Surface, palette.ToneSurface(DalamudUiTone.Accent, -1f));
        Assert.Equal(palette.Accent.X, palette.ToneSurface(DalamudUiTone.Accent, 2f).X);
        Assert.Equal(palette.Accent.Y, palette.ToneSurface(DalamudUiTone.Accent, 2f).Y);
        Assert.Equal(palette.Accent.Z, palette.ToneSurface(DalamudUiTone.Accent, 2f).Z);
        Assert.Equal(palette.Surface.W, palette.ToneSurface(DalamudUiTone.Accent, 2f).W);
    }
}
