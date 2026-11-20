using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Layout;

/// <summary>Measuring and flow helpers for rows of buttons and text that must wrap instead of clipping.</summary>
/// <remarks>All widths are in the current font and style; measure in the scope where the item is drawn.</remarks>
public static class DalamudUiLayout
{
    /// <summary>The ellipsis <see cref="ElideText"/> appends by default.</summary>
    public const string Ellipsis = "…";

    /// <summary>Width of a framed button with this label, ignoring any <c>##</c>/<c>###</c> ID suffix.</summary>
    public static float ButtonWidth(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        return ImGui.CalcTextSize(label, hideTextAfterDoubleHash: true).X + (ImGui.GetStyle().FramePadding.X * 2f);
    }

    /// <summary>Continues the current row when an item of <paramref name="itemWidth"/> still fits; otherwise starts a new line.</summary>
    /// <remarks>Call between two items, in place of <see cref="ImGui.SameLine()"/>.</remarks>
    public static void SameLineIfFits(float itemWidth)
    {
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X < itemWidth)
            ImGui.NewLine();
    }

    /// <summary>Continues the current row when a button with this label still fits; otherwise starts a new line.</summary>
    public static void SameLineIfFits(string buttonLabel) => SameLineIfFits(ButtonWidth(buttonLabel));

    /// <summary>Places the next item of <paramref name="width"/> flush right on the current row, or flush right on a new line when the row is full.</summary>
    /// <remarks>Measures the content region, so it right-aligns inside table cells and columns as well as windows.</remarks>
    public static void SameLineRight(float width)
    {
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X < width)
            ImGui.NewLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, ImGui.GetContentRegionAvail().X - width));
    }

    /// <summary>Draws one line of text flush right in the remaining content region.</summary>
    /// <param name="text">Text to draw.</param>
    /// <param name="color">Text colour; the style's text colour when null.</param>
    public static void DrawRightAligned(string text, Vector4? color = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var width = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, ImGui.GetContentRegionAvail().X - width));
        if (color is { } value)
            ImGui.TextColored(value, text);
        else
            ImGui.TextUnformatted(text);
    }

    /// <summary>Shortens <paramref name="text"/> to the longest prefix that fits <paramref name="maxWidth"/> with <paramref name="ellipsis"/> appended.</summary>
    /// <returns>The text unchanged when it fits; the bare ellipsis when no prefix fits beside it;
    /// empty when not even the ellipsis fits. Compare lengths to know whether to show the full text in a tooltip.</returns>
    public static string ElideText(string text, float maxWidth, string ellipsis = Ellipsis)
    {
        ArgumentNullException.ThrowIfNull(ellipsis);
        if (string.IsNullOrEmpty(text) || maxWidth <= 0f)
            return string.Empty;
        if (ImGui.CalcTextSize(text).X <= maxWidth)
            return text;

        var room = maxWidth - ImGui.CalcTextSize(ellipsis).X;
        if (room < 0f)
            return string.Empty;

        // Prefix width is monotonic in length, so a binary search keeps long labels cheap per frame.
        int low = 0, high = text.Length - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(text[..mid]).X <= room) low = mid;
            else high = mid - 1;
        }

        return text[..low].TrimEnd() + ellipsis;
    }
}
