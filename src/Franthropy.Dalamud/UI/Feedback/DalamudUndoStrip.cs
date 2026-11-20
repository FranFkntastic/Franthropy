using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.UI.Layout;

namespace Franthropy.Dalamud.UI.Feedback;

/// <summary>What the user did with a <see cref="DalamudUndoStrip"/> this frame.</summary>
public enum DalamudUndoStripResult
{
    None,
    Undo,
    Dismiss,
}

/// <summary>One muted line naming the last removal, with Undo and dismiss buttons.</summary>
/// <remarks>The caller owns what was removed and how long the offer lasts; draw the strip only
/// while an undo is available, or let <see cref="DalamudTimedUndoStrip{TItem}"/> own both. It takes
/// one frame-height line where it is drawn; a label too long for the line is elided, with the full
/// text in a tooltip.</remarks>
public static class DalamudUndoStrip
{
    private const string Separator = " · ";
    private const string UndoLabel = "Undo";
    private const string DismissLabel = "x";

    /// <param name="id">Unique within the window; scopes both buttons.</param>
    /// <param name="label">What was removed, e.g. "Removed Cobalt Ingot".</param>
    /// <param name="itemSubmitted">Called right after each button is drawn with the result that button
    /// produces, so the caller can register it as the last item (for example with an agent bridge).</param>
    public static DalamudUndoStripResult Draw(string id, string label, Action<DalamudUndoStripResult>? itemSubmitted = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(label);
        var result = DalamudUndoStripResult.None;
        ImGui.PushID(id);

        var style = ImGui.GetStyle();
        // SmallButton drops only the vertical frame padding, so ButtonWidth measures it too.
        var buttons = DalamudUiLayout.ButtonWidth(UndoLabel) + style.ItemSpacing.X + DalamudUiLayout.ButtonWidth(DismissLabel);
        var room = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(Separator).X - buttons;
        var shown = DalamudUiLayout.ElideText(label, room);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(shown);
        if (shown.Length != label.Length && ImGui.IsItemHovered())
            ImGui.SetTooltip(label);
        ImGui.SameLine(0, 0);
        ImGui.TextDisabled(Separator);
        ImGui.SameLine(0, 0);
        if (ImGui.SmallButton(UndoLabel))
            result = DalamudUndoStripResult.Undo;
        itemSubmitted?.Invoke(DalamudUndoStripResult.Undo);
        ImGui.SameLine();
        if (ImGui.SmallButton($"{DismissLabel}##dismiss"))
            result = DalamudUndoStripResult.Dismiss;
        itemSubmitted?.Invoke(DalamudUndoStripResult.Dismiss);
        ImGui.PopID();
        return result;
    }
}
