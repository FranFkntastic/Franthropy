using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Tables;

/// <summary>A quiet per-row remove button that appears only while its table row is hovered.</summary>
/// <remarks>The hidden state reserves the same space, so rows do not shift as the pointer moves.
/// Pair an immediate removal with <see cref="Feedback.DalamudUndoStrip"/> rather than a confirmation.</remarks>
public static class DalamudRowRemoveButton
{
    private const string Glyph = "×";

    /// <summary>Width the button occupies, hidden or shown. Use it to size a fixed trailing column.</summary>
    public static float Width => ImGui.CalcTextSize(Glyph).X + (ImGui.GetStyle().FramePadding.X * 2f);

    /// <summary>Whether the pointer is over the table row that contains the last drawn item.</summary>
    /// <remarks>Call directly after an item in the row. The row's vertical extent is that item's
    /// rect plus cell padding, so the item should be as tall as the row.</remarks>
    public static bool IsLastItemRowHovered()
    {
        var padding = ImGui.GetStyle().CellPadding.Y;
        return IsRowHovered(ImGui.GetItemRectMin().Y - padding, ImGui.GetItemRectMax().Y + padding);
    }

    /// <summary>Whether the pointer is inside the current window between two screen-space Y positions.</summary>
    public static bool IsRowHovered(float rowTop, float rowBottom)
    {
        // A pressed button is the active item, which a plain IsWindowHovered treats as "not
        // hovered"; the button would vanish mid-click and the release would never land.
        if (!ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
            return false;
        var mouseY = ImGui.GetMousePos().Y;
        return mouseY >= rowTop && mouseY < rowBottom;
    }

    /// <summary>Draws the button when <paramref name="rowHovered"/>, otherwise reserves its space.</summary>
    /// <returns>True on the frame the button is clicked.</returns>
    /// <remarks>Always submits exactly one item, so "last item" registration after the call is stable.</remarks>
    public static bool Draw(string id, bool rowHovered, bool enabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!rowHovered)
        {
            ImGui.Dummy(new Vector2(Width, ImGui.GetTextLineHeight()));
            return false;
        }

        ImGui.BeginDisabled(!enabled);
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        var clicked = ImGui.SmallButton($"{Glyph}##{id}");
        ImGui.PopStyleColor(2);
        ImGui.EndDisabled();
        return clicked && enabled;
    }
}
