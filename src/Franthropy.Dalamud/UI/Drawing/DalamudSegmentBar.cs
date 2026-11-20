using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Drawing;

/// <summary>Cell geometry and the empty-cell colour for <see cref="DalamudSegmentBar"/>.</summary>
/// <param name="Cell">Width of one cell.</param>
/// <param name="Gap">Space between cells.</param>
/// <param name="Height">Bar height; the bar is centred on a frame-height line.</param>
/// <param name="Empty">Colour of cells no segment fills.</param>
public readonly record struct DalamudSegmentBarStyle(float Cell, float Gap, float Height, Vector4 Empty)
{
    /// <summary>4 px cells with 1 px gaps, 12 px tall, on a dark grey track.</summary>
    public static DalamudSegmentBarStyle Default { get; } = new(4f, 1f, 12f, new Vector4(.15f, .15f, .15f, 1f));

    /// <summary>Number of whole cells that fit <paramref name="width"/>; at least one.</summary>
    public int CellCount(float width) => Math.Max(1, (int)((width + Gap) / (Cell + Gap)));

    /// <summary>Width actually drawn for <paramref name="width"/>, which snaps down to whole cells.</summary>
    public float MeasureWidth(float width) => (CellCount(width) * (Cell + Gap)) - Gap;
}

/// <summary>
/// A capacity or composition bar made of discrete cells, like an inventory slot strip.
/// Segments fill cells left to right in order; each non-empty segment gets at least one cell,
/// so a small share stays visible, and cells left over show the empty colour.
/// </summary>
public static class DalamudSegmentBar
{
    /// <summary>Draws the bar in <see cref="DalamudSegmentBarStyle.Default"/> geometry.</summary>
    public static void Draw(float width, long total, params ReadOnlySpan<(long Units, Vector4 Color)> segments) =>
        Draw(width, total, DalamudSegmentBarStyle.Default, segments);

    /// <summary>Width the default-style bar draws for <paramref name="width"/>.</summary>
    public static float MeasureWidth(float width) => DalamudSegmentBarStyle.Default.MeasureWidth(width);

    /// <summary>Draws the bar vertically centred on a frame-height line and advances the cursor past it.</summary>
    /// <param name="width">Space available; the bar uses <see cref="DalamudSegmentBarStyle.MeasureWidth"/> of it.</param>
    /// <param name="total">Units the full bar represents. Zero or less draws an empty bar.</param>
    /// <param name="style">Cell geometry and empty colour.</param>
    /// <param name="segments">Units and colour per segment, in fill order. Segments beyond the last cell are dropped.</param>
    public static void Draw(
        float width,
        long total,
        DalamudSegmentBarStyle style,
        params ReadOnlySpan<(long Units, Vector4 Color)> segments)
    {
        var frame = ImGui.GetFrameHeight();
        var origin = ImGui.GetCursorScreenPos();
        var top = origin.Y + MathF.Floor((frame - style.Height) / 2f);
        var cells = style.CellCount(width);
        var drawList = ImGui.GetWindowDrawList();

        var next = 0;
        if (total > 0)
        {
            foreach (var (units, color) in segments)
            {
                if (units <= 0 || next >= cells)
                    continue;
                var count = Math.Clamp((int)Math.Round((double)units * cells / total), 1, cells - next);
                DrawCells(drawList, origin.X, top, style, next, count, ImGui.GetColorU32(color));
                next += count;
            }
        }

        DrawCells(drawList, origin.X, top, style, next, cells - next, ImGui.GetColorU32(style.Empty));
        ImGui.Dummy(new Vector2(style.MeasureWidth(width), frame));
    }

    private static void DrawCells(ImDrawListPtr drawList, float left, float top, DalamudSegmentBarStyle style,
        int first, int count, uint color)
    {
        var pitch = style.Cell + style.Gap;
        for (var index = first; index < first + count; index++)
        {
            var x = left + (index * pitch);
            drawList.AddRectFilled(new Vector2(x, top), new Vector2(x + style.Cell, top + style.Height), color);
        }
    }
}
