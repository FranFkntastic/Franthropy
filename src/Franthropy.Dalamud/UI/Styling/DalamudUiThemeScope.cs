using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Franthropy.Dalamud.UI.Styling;

public static class DalamudUiThemeScope
{
    public static IDisposable Push(
        DalamudUiTheme theme,
        DalamudUiThemeParts parts = DalamudUiThemeParts.Controls | DalamudUiThemeParts.Metrics)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (parts == DalamudUiThemeParts.None)
            return EmptyScope.Instance;

        IDisposable? colors = null;
        IDisposable? styles = null;
        var palette = theme.Palette;
        var metrics = theme.Metrics;

        if ((parts & (DalamudUiThemeParts.Text | DalamudUiThemeParts.Controls | DalamudUiThemeParts.Surfaces)) != 0)
        {
            var colorScope = ImRaii.PushColor(ImGuiCol.Text, palette.Text);
            if ((parts & DalamudUiThemeParts.Text) != 0)
                colorScope.Push(ImGuiCol.TextDisabled, palette.Muted);
            if ((parts & DalamudUiThemeParts.Controls) != 0)
            {
                colorScope.Push(ImGuiCol.FrameBg, palette.SurfaceRaised);
                colorScope.Push(ImGuiCol.FrameBgHovered, palette.ToneSurface(DalamudUiTone.Accent, 0.28f));
                colorScope.Push(ImGuiCol.FrameBgActive, palette.ToneSurface(DalamudUiTone.Accent, 0.40f));
                colorScope.Push(ImGuiCol.CheckMark, palette.Accent);
                colorScope.Push(ImGuiCol.SliderGrab, palette.Accent);
                colorScope.Push(ImGuiCol.SliderGrabActive, palette.Accent);
                colorScope.Push(ImGuiCol.Header, palette.ToneSurface(DalamudUiTone.Accent, 0.24f));
                colorScope.Push(ImGuiCol.HeaderHovered, palette.ToneSurface(DalamudUiTone.Accent, 0.38f));
                colorScope.Push(ImGuiCol.HeaderActive, palette.ToneSurface(DalamudUiTone.Accent, 0.52f));
            }
            if ((parts & DalamudUiThemeParts.Surfaces) != 0)
            {
                colorScope.Push(ImGuiCol.ChildBg, palette.Surface);
                colorScope.Push(ImGuiCol.PopupBg, palette.Surface);
                colorScope.Push(ImGuiCol.Border, palette.Border);
                colorScope.Push(ImGuiCol.Separator, palette.Border);
                colorScope.Push(ImGuiCol.TableHeaderBg, palette.SurfaceRaised);
                colorScope.Push(ImGuiCol.TableRowBg, palette.Surface);
                colorScope.Push(ImGuiCol.TableRowBgAlt, DalamudUiPalette.WithAlpha(palette.SurfaceRaised, 0.72f));
            }
            colors = colorScope;
        }

        if ((parts & DalamudUiThemeParts.Metrics) != 0)
        {
            var styleScope = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, metrics.FramePadding);
            styleScope.Push(ImGuiStyleVar.ItemSpacing, metrics.ItemSpacing);
            styleScope.Push(ImGuiStyleVar.ItemInnerSpacing, metrics.ItemInnerSpacing);
            styleScope.Push(ImGuiStyleVar.FrameRounding, metrics.FrameRounding);
            styleScope.Push(ImGuiStyleVar.ChildRounding, metrics.ChildRounding);
            styleScope.Push(ImGuiStyleVar.PopupRounding, metrics.PopupRounding);
            styleScope.Push(ImGuiStyleVar.GrabRounding, metrics.GrabRounding);
            styleScope.Push(ImGuiStyleVar.FrameBorderSize, metrics.BorderSize);
            styles = styleScope;
        }

        return new CompositeScope(colors, styles);
    }

    private sealed class CompositeScope(IDisposable? colors, IDisposable? styles) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            styles?.Dispose();
            colors?.Dispose();
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();
        public void Dispose() { }
    }
}
