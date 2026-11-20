using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Drawing;

/// <summary>Draw-list operations ImGui does not provide directly.</summary>
public static class DalamudDrawListPrimitives
{
    /// <summary>Draws text with its bottom-left corner at <paramref name="pivot"/>, rotated about the pivot.</summary>
    /// <param name="drawList">Target draw list, e.g. <c>ImGui.GetWindowDrawList()</c>.</param>
    /// <param name="pivot">Screen position of the text's bottom-left corner and the rotation centre.</param>
    /// <param name="radians">Clockwise in screen space (Y down); <c>-MathF.PI / 4</c> slants text up and to the right,
    /// as for angled column headers.</param>
    /// <param name="color">Packed text colour.</param>
    /// <param name="text">Text to draw on one line.</param>
    /// <param name="lineHeight">Height of the text line, normally <c>ImGui.GetTextLineHeight()</c>.</param>
    /// <remarks>Rotates the vertices the text emitted, so it stays in the current font, atlas, and clip rect.</remarks>
    public static unsafe void AddRotatedText(
        ImDrawListPtr drawList,
        Vector2 pivot,
        float radians,
        uint color,
        string text,
        float lineHeight)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = drawList.VtxBuffer.Size;
        drawList.AddText(new Vector2(pivot.X, pivot.Y - lineHeight), color, text);
        var end = drawList.VtxBuffer.Size;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var vertices = drawList.VtxBuffer.Data;
        for (var index = start; index < end; index++)
        {
            var offset = vertices[index].Pos - pivot;
            vertices[index].Pos = pivot + new Vector2((offset.X * cos) - (offset.Y * sin), (offset.X * sin) + (offset.Y * cos));
        }
    }
}
