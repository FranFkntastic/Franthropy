using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.UI.Styling;

namespace Franthropy.Dalamud.UI.Tables;

/// <summary>State styling for a <see cref="DalamudProgressCell"/>. Colour encodes state, never identity.</summary>
[Flags]
public enum DalamudProgressCellFlags
{
    None = 0,

    /// <summary>Fills with the complete colour instead of the accent fill.</summary>
    Complete = 1 << 0,

    /// <summary>Outlines the bar in the overflow colour.</summary>
    Overflow = 1 << 1,

    /// <summary>Fills and labels in muted colours; suppresses <see cref="Complete"/> and <see cref="Overflow"/>.</summary>
    Muted = 1 << 2,

    /// <summary>Outlines the bar in the highlight colour, e.g. for a linked cross-row hover.</summary>
    Highlighted = 1 << 3,

    /// <summary>Hatches the unfilled remainder in the shortfall colour.</summary>
    Shortfall = 1 << 4,

    /// <summary>Reserves the icon square even while the icon texture is still loading.</summary>
    IconSlot = 1 << 5,
}

/// <summary>A two-segment bar: <see cref="Prior"/> filled before this cell (faded), then <see cref="Current"/> (bright).</summary>
/// <param name="Label">Left-aligned text, ellipsized to fit.</param>
/// <param name="Value">Right-aligned text; kept in preference to the label when space is short.</param>
/// <param name="Prior">Fraction already filled before this cell, clamped to [0, 1].</param>
/// <param name="Current">Fraction this cell adds, clamped to the space left after <paramref name="Prior"/>.</param>
public readonly record struct DalamudProgressCell(string Label, string? Value, double Prior, double Current)
{
    public DalamudProgressCellFlags Flags { get; init; }

    /// <summary>Optional square icon drawn left of the bar at text-line height.</summary>
    public ImTextureID? Icon { get; init; }
}

/// <summary>Colours for <see cref="DalamudProgressCellRenderer"/>. Fill colours are drawn at the segment alphas.</summary>
public sealed record DalamudProgressCellStyle(
    Vector4 Fill,
    Vector4 Complete,
    Vector4 Muted,
    Vector4 Track,
    Vector4 Overflow,
    Vector4 Shortfall,
    Vector4 Highlight,
    Vector4 Text,
    Vector4 MutedText)
{
    public float PriorAlpha { get; init; } = 0.24f;
    public float CurrentAlpha { get; init; } = 0.58f;

    public static DalamudProgressCellStyle FromPalette(DalamudUiPalette palette) =>
        new(
            palette.Accent,
            palette.Success,
            palette.Muted,
            DalamudUiPalette.WithAlpha(palette.Text, 0.07f),
            palette.Warning,
            palette.Error,
            palette.Text,
            palette.Text,
            palette.Muted);
}

/// <summary>Draws <see cref="DalamudProgressCell"/> bars at text-line height so rows keep their text rhythm.</summary>
public static class DalamudProgressCellRenderer
{
    private const float TextInset = 4f;
    private const float IconGap = 4f;
    private const float MinimumVisibleSegment = 2f;
    private const float HatchSpacing = 5f;
    private const string Ellipsis = "...";

    /// <summary>Draws the bar across <paramref name="width"/> (or the remaining content width). Returns true while hovered.</summary>
    /// <remarks><paramref name="id"/> must be unique within the current ImGui ID scope.</remarks>
    public static bool Draw(string id, in DalamudProgressCell cell, DalamudProgressCellStyle style, float width = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(style);
        var height = ImGui.GetTextLineHeight();
        var available = width > 0 ? width : ImGui.GetContentRegionAvail().X;
        var size = new Vector2(Math.Max(1f, available), height);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemVisible())
            DrawBar(ImGui.GetWindowDrawList(), origin, size, cell, style, hovered);
        return hovered;
    }

    private static void DrawBar(ImDrawListPtr drawList, Vector2 origin, Vector2 size, in DalamudProgressCell cell,
        DalamudProgressCellStyle style, bool hovered)
    {
        var flags = cell.Flags;
        var muted = flags.HasFlag(DalamudProgressCellFlags.Muted);
        var height = size.Y;
        var left = origin.X;
        if (cell.Icon is { } icon)
        {
            drawList.AddImage(icon, origin, origin + new Vector2(height));
            left += height + IconGap;
        }
        else if (flags.HasFlag(DalamudProgressCellFlags.IconSlot))
        {
            left += height + IconGap;
        }

        var min = new Vector2(Math.Min(left, origin.X + size.X), origin.Y);
        var max = origin + size;
        var barWidth = max.X - min.X;
        if (barWidth <= 0)
            return;

        var rounding = MathF.Min(3f, height * 0.2f);
        var track = hovered ? Brighten(style.Track) : style.Track;
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(track), rounding);

        var fill = muted ? style.Muted
            : flags.HasFlag(DalamudProgressCellFlags.Complete) ? style.Complete
            : style.Fill;
        var prior = Clamp01(cell.Prior);
        var current = Math.Min(Clamp01(cell.Current), 1 - prior);
        var priorEnd = SegmentEnd(min.X, barWidth, 0, prior, max.X);
        var currentEnd = SegmentEnd(priorEnd, barWidth, prior, current, max.X);
        // Muted fills recede further so muted text stays legible over them.
        var alpha = fill.W * (muted ? 0.55f : 1f);
        if (priorEnd > min.X)
            FillSegment(drawList, min.X, priorEnd, min, max, rounding,
                DalamudUiPalette.WithAlpha(fill, alpha * style.PriorAlpha));
        if (currentEnd > priorEnd)
            FillSegment(drawList, priorEnd, currentEnd, min, max, rounding,
                DalamudUiPalette.WithAlpha(fill, alpha * style.CurrentAlpha));

        if (flags.HasFlag(DalamudProgressCellFlags.Shortfall) && currentEnd < max.X)
            Hatch(drawList, new Vector2(currentEnd, min.Y), max, muted ? style.Muted : style.Shortfall);

        DrawText(drawList, min, max, cell, muted ? style.MutedText : style.Text);

        if (!muted && flags.HasFlag(DalamudProgressCellFlags.Overflow))
            drawList.AddRect(min, max, ImGui.GetColorU32(style.Overflow), rounding, ImDrawFlags.None, 1.5f);
        if (flags.HasFlag(DalamudProgressCellFlags.Highlighted))
            drawList.AddRect(min - Vector2.One, max + Vector2.One,
                ImGui.GetColorU32(DalamudUiPalette.WithAlpha(style.Highlight, 0.9f)), rounding + 1, ImDrawFlags.None, 1.5f);
    }

    private static float SegmentEnd(float start, float barWidth, double before, double fraction, float limit)
    {
        if (fraction <= 0)
            return start;
        // A tiny but real share still gets a visible sliver.
        var end = start + Math.Max(MinimumVisibleSegment, barWidth * (float)fraction);
        // Snap a full bar to the edge despite floating-point remainders from callers' subtraction.
        return before + fraction >= 1 - 1e-6 ? limit : Math.Min(end, limit);
    }

    private static void FillSegment(ImDrawListPtr drawList, float from, float to, Vector2 min, Vector2 max,
        float rounding, Vector4 color)
    {
        var corners = (from <= min.X ? ImDrawFlags.RoundCornersLeft : ImDrawFlags.None) |
                      (to >= max.X ? ImDrawFlags.RoundCornersRight : ImDrawFlags.None);
        drawList.AddRectFilled(new Vector2(from, min.Y), new Vector2(to, max.Y), ImGui.GetColorU32(color),
            corners == ImDrawFlags.None ? 0 : rounding, corners == ImDrawFlags.None ? ImDrawFlags.RoundCornersNone : corners);
    }

    private static void Hatch(ImDrawListPtr drawList, Vector2 min, Vector2 max, Vector4 color)
    {
        var height = max.Y - min.Y;
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(DalamudUiPalette.WithAlpha(color, 0.10f)));
        drawList.PushClipRect(min, max, true);
        var line = ImGui.GetColorU32(DalamudUiPalette.WithAlpha(color, 0.45f));
        // Anchor stripes to absolute X so adjacent rows line up into one continuous texture.
        var start = MathF.Floor((min.X - height) / HatchSpacing) * HatchSpacing;
        for (var x = start; x < max.X; x += HatchSpacing)
            drawList.AddLine(new Vector2(x, max.Y), new Vector2(x + height, min.Y), line, 1f);
        drawList.PopClipRect();
    }

    private static void DrawText(ImDrawListPtr drawList, Vector2 min, Vector2 max, in DalamudProgressCell cell, Vector4 color)
    {
        var textColor = ImGui.GetColorU32(color);
        var shadow = ImGui.GetColorU32(new Vector4(0, 0, 0, 0.55f * color.W));
        var right = max.X - TextInset;
        if (!string.IsNullOrEmpty(cell.Value))
        {
            var valueWidth = ImGui.CalcTextSize(cell.Value).X;
            if (valueWidth > right - (min.X + TextInset))
                return;
            right -= valueWidth;
            ShadowedText(drawList, new Vector2(right, min.Y), textColor, shadow, cell.Value);
            right -= TextInset * 2;
        }

        var label = Fit(cell.Label, right - (min.X + TextInset));
        if (label.Length > 0)
            ShadowedText(drawList, new Vector2(min.X + TextInset, min.Y), textColor, shadow, label);
    }

    private static void ShadowedText(ImDrawListPtr drawList, Vector2 position, uint color, uint shadow, string text)
    {
        drawList.AddText(position + Vector2.One, shadow, text);
        drawList.AddText(position, color, text);
    }

    internal static string Fit(string text, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            return string.Empty;
        if (ImGui.CalcTextSize(text).X <= maxWidth)
            return text;
        var room = maxWidth - ImGui.CalcTextSize(Ellipsis).X;
        if (room <= 0)
            return string.Empty;
        int low = 0, high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(text[..mid]).X <= room) low = mid;
            else high = mid - 1;
        }

        return low == 0 ? string.Empty : text[..low].TrimEnd() + Ellipsis;
    }

    private static double Clamp01(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);

    private static Vector4 Brighten(Vector4 color) =>
        DalamudUiPalette.WithAlpha(color, Math.Min(1f, color.W * 1.8f));
}
