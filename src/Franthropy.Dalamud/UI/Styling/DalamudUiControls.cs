using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Franthropy.Dalamud.UI.Styling;

public static class DalamudUiControls
{
    public static bool Button(
        string label,
        DalamudUiTheme theme,
        DalamudUiTone tone = DalamudUiTone.Accent,
        bool quiet = false,
        Vector2 size = default,
        bool enabled = true,
        string? tooltip = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(theme);

        using var button = DalamudUiChrome.PushButton(theme.Palette, tone, quiet);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, theme.Metrics.FrameRounding)
            .Push(ImGuiStyleVar.FramePadding, theme.Metrics.FramePadding)
            .Push(ImGuiStyleVar.FrameBorderSize, theme.Metrics.BorderSize);
        using var disabled = ImRaii.Disabled(!enabled);
        var clicked = ImGui.Button(label, size);
        if (!string.IsNullOrWhiteSpace(tooltip) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
        return enabled && clicked;
    }

    public static bool SegmentedOption(
        string label,
        bool selected,
        DalamudUiTheme theme,
        Vector2 size = default,
        bool enabled = true,
        string? tooltip = null)
    {
        var palette = theme.Palette;
        var selectedSurface = palette.ToneSurface(DalamudUiTone.Accent, 0.52f);
        var restingSurface = selected ? selectedSurface : palette.SurfaceRaised;
        using var colors = ImRaii.PushColor(ImGuiCol.Button, restingSurface)
            .Push(ImGuiCol.ButtonHovered, palette.ToneSurface(DalamudUiTone.Accent, selected ? 0.66f : 0.30f))
            .Push(ImGuiCol.ButtonActive, palette.ToneSurface(DalamudUiTone.Accent, 0.78f))
            .Push(ImGuiCol.Text, selected ? palette.Text : palette.Muted)
            .Push(ImGuiCol.Border, DalamudUiPalette.WithAlpha(palette.Accent, selected ? 0.90f : 0.34f));
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, theme.Metrics.FrameRounding)
            .Push(ImGuiStyleVar.FramePadding, theme.Metrics.FramePadding)
            .Push(ImGuiStyleVar.FrameBorderSize, theme.Metrics.BorderSize);
        using var disabled = ImRaii.Disabled(!enabled);
        var clicked = ImGui.Button(label, size);
        if (!string.IsNullOrWhiteSpace(tooltip) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
        return enabled && clicked;
    }
}
