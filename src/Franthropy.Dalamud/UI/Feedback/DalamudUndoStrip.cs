using Dalamud.Bindings.ImGui;

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
/// while an undo is available. It takes one frame-height line where it is drawn.</remarks>
public static class DalamudUndoStrip
{
    /// <param name="id">Unique within the window; scopes both buttons.</param>
    /// <param name="label">What was removed, e.g. "Removed Cobalt Ingot".</param>
    public static DalamudUndoStripResult Draw(string id, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(label);
        var result = DalamudUndoStripResult.None;
        ImGui.PushID(id);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(label);
        ImGui.SameLine(0, 0);
        ImGui.TextDisabled(" · ");
        ImGui.SameLine(0, 0);
        if (ImGui.SmallButton("Undo"))
            result = DalamudUndoStripResult.Undo;
        ImGui.SameLine();
        if (ImGui.SmallButton("x##dismiss"))
            result = DalamudUndoStripResult.Dismiss;
        ImGui.PopID();
        return result;
    }
}
