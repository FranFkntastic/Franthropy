using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Franthropy.Dalamud.UI.Styling;

public static class DalamudUiChrome
{
    public static IDisposable PushTable(DalamudUiTheme theme) =>
        PushTable(theme.Palette);

    public static IDisposable PushTable(DalamudUiPalette palette)
    {
        var colors = ImRaii.PushColor(ImGuiCol.TableHeaderBg, palette.SurfaceRaised);
        colors.Push(ImGuiCol.TableRowBg, palette.Surface);
        colors.Push(
            ImGuiCol.TableRowBgAlt,
            DalamudUiPalette.WithAlpha(palette.SurfaceRaised, 0.72f));
        colors.Push(ImGuiCol.Border, palette.Border);
        return colors;
    }

    public static IDisposable PushButton(
        DalamudUiPalette palette,
        DalamudUiTone tone = DalamudUiTone.Accent,
        bool quiet = false)
    {
        var accent = palette.Resolve(tone);
        var resting = quiet
            ? palette.SurfaceRaised
            : palette.ToneSurface(tone, 0.48f);
        var colors = ImRaii.PushColor(ImGuiCol.Button, resting);
        colors.Push(ImGuiCol.ButtonHovered, palette.ToneSurface(tone, quiet ? 0.28f : 0.65f));
        colors.Push(ImGuiCol.ButtonActive, accent);
        colors.Push(ImGuiCol.Border, DalamudUiPalette.WithAlpha(accent, quiet ? 0.50f : 0.85f));
        return colors;
    }

    public static IDisposable PushButton(
        DalamudUiTheme theme,
        DalamudUiTone tone = DalamudUiTone.Accent,
        bool quiet = false) =>
        PushButton(theme.Palette, tone, quiet);

    public static IDisposable PushInput(
        DalamudUiPalette palette,
        DalamudUiTone tone = DalamudUiTone.Accent)
    {
        var colors = ImRaii.PushColor(ImGuiCol.FrameBg, palette.SurfaceRaised);
        colors.Push(ImGuiCol.FrameBgHovered, palette.ToneSurface(tone, 0.28f));
        colors.Push(ImGuiCol.FrameBgActive, palette.ToneSurface(tone, 0.38f));
        colors.Push(ImGuiCol.Border, DalamudUiPalette.WithAlpha(palette.Resolve(tone), 0.55f));
        return colors;
    }

    public static IDisposable PushInput(
        DalamudUiTheme theme,
        DalamudUiTone tone = DalamudUiTone.Accent) =>
        PushInput(theme.Palette, tone);

    public static void DrawCallout(
        string id,
        string title,
        string? detail,
        DalamudUiTheme theme,
        DalamudUiTone tone = DalamudUiTone.Neutral,
        Action? drawActions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(theme);

        var palette = theme.Palette;
        var accent = palette.Resolve(tone);
        var minimum = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var lineCount = string.IsNullOrWhiteSpace(detail) ? 1f : 2f;
        var actionHeight = drawActions is null ? 0f : ImGui.GetFrameHeight() + theme.Metrics.ItemSpacing.Y;
        var height = (ImGui.GetTextLineHeightWithSpacing() * lineCount) +
                     actionHeight +
                     (theme.Metrics.WindowPadding.Y * 2f);
        var maximum = new Vector2(minimum.X + width, minimum.Y + height);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            minimum,
            maximum,
            ImGui.GetColorU32(palette.ToneSurface(tone, 0.13f)),
            theme.Metrics.ChildRounding);
        drawList.AddRect(
            minimum,
            maximum,
            ImGui.GetColorU32(DalamudUiPalette.WithAlpha(accent, 0.48f)),
            theme.Metrics.ChildRounding,
            ImDrawFlags.None,
            theme.Metrics.BorderSize);
        drawList.AddRectFilled(
            minimum,
            new Vector2(minimum.X + 3f, maximum.Y),
            ImGui.GetColorU32(accent),
            theme.Metrics.ChildRounding,
            ImDrawFlags.RoundCornersLeft);

        var contentMinimum = minimum + theme.Metrics.WindowPadding;
        ImGui.SetCursorScreenPos(contentMinimum);
        ImGui.TextColored(accent, title);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            ImGui.PushTextWrapPos(maximum.X - theme.Metrics.WindowPadding.X);
            ImGui.TextUnformatted(detail);
            ImGui.PopTextWrapPos();
        }
        drawActions?.Invoke();

        ImGui.SetCursorScreenPos(new Vector2(minimum.X, maximum.Y));
        ImGui.Dummy(Vector2.Zero);
    }

    public static void DrawSectionHeading(
        string title,
        string? detail,
        DalamudUiPalette palette,
        Action? drawActions = null,
        float actionWidth = 0f)
    {
        var minimum = ImGui.GetCursorScreenPos();
        var height = ImGui.GetFrameHeight() + 4f;
        var maximum = new Vector2(
            minimum.X + ImGui.GetContentRegionAvail().X,
            minimum.Y + height);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            minimum,
            maximum,
            ImGui.GetColorU32(palette.SurfaceRaised),
            2f);
        drawList.AddRectFilled(
            minimum,
            new Vector2(minimum.X + 4f, maximum.Y),
            ImGui.GetColorU32(palette.Accent),
            2f);

        ImGui.SetCursorScreenPos(new Vector2(minimum.X + 11f, minimum.Y + 2f));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(palette.Text, title);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            ImGui.SameLine();
            ImGui.TextColored(palette.Muted, detail);
        }

        if (drawActions is not null)
        {
            ImGui.SameLine();
            if (actionWidth > 0f)
            {
                var rightAlignedX = ImGui.GetWindowContentRegionMax().X - actionWidth;
                ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), rightAlignedX));
            }
            drawActions();
        }

        ImGui.SetCursorScreenPos(new Vector2(minimum.X, maximum.Y));
        ImGui.Dummy(new Vector2(0f, 3f));
    }

    public static void DrawStatusBand(
        string id,
        DalamudUiPalette palette,
        DalamudUiTone tone,
        Action drawContent,
        float height = 38f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(drawContent);
        var minimum = ImGui.GetCursorScreenPos();
        var accent = palette.Resolve(tone);
        var maximum = new Vector2(minimum.X + ImGui.GetContentRegionAvail().X, minimum.Y + height);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            minimum,
            maximum,
            ImGui.GetColorU32(palette.ToneSurface(tone)),
            3f);
        drawList.AddRect(
            minimum,
            maximum,
            ImGui.GetColorU32(DalamudUiPalette.WithAlpha(accent, 0.50f)),
            3f);
        drawList.AddRectFilled(
            minimum,
            new Vector2(minimum.X + 3f, minimum.Y + height),
            ImGui.GetColorU32(accent));

        ImGui.SetCursorScreenPos(new Vector2(minimum.X + 14f, minimum.Y + 7f));
        ImGui.PushID(id);
        drawContent();
        ImGui.PopID();
        ImGui.SetCursorScreenPos(new Vector2(minimum.X, maximum.Y));
        ImGui.Dummy(Vector2.Zero);
    }

    public static void DrawStatusFact(
        string label,
        string value,
        DalamudUiPalette palette,
        DalamudUiTone tone = DalamudUiTone.Neutral)
    {
        var accent = palette.Resolve(tone);
        var markerMinimum = ImGui.GetCursorScreenPos();
        var markerHeight = ImGui.GetTextLineHeight();
        ImGui.GetWindowDrawList().AddRectFilled(
            markerMinimum,
            new Vector2(markerMinimum.X + 2f, markerMinimum.Y + markerHeight),
            ImGui.GetColorU32(DalamudUiPalette.WithAlpha(accent, 0.82f)),
            1f);

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 7f);
        ImGui.TextColored(palette.Muted, label);
        ImGui.SameLine();
        ImGui.TextColored(accent, value);
    }

    public static void DrawBadge(
        string text,
        DalamudUiPalette palette,
        DalamudUiTone tone = DalamudUiTone.Neutral)
    {
        var accent = palette.Resolve(tone);
        var padding = new Vector2(6f, 2f);
        var textSize = ImGui.CalcTextSize(text);
        var size = textSize + (padding * 2f);
        var minimum = ImGui.GetCursorScreenPos();
        var maximum = minimum + size;

        ImGui.Dummy(size);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            minimum,
            maximum,
            ImGui.GetColorU32(palette.ToneSurface(tone, 0.28f)),
            3f);
        drawList.AddRect(
            minimum,
            maximum,
            ImGui.GetColorU32(DalamudUiPalette.WithAlpha(accent, 0.62f)),
            3f);
        drawList.AddText(minimum + padding, ImGui.GetColorU32(accent), text);
    }

}
