using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Tables;

public static class DalamudTableSelectionRenderer
{
    public static bool DrawRow<TKey>(
        TableSelectionModel<TKey> selection,
        IReadOnlyList<TKey> orderedKeys,
        int rowIndex,
        string id,
        Vector2 size,
        bool enabled = true)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(orderedKeys);
        if ((uint)rowIndex >= (uint)orderedKeys.Count)
            throw new ArgumentOutOfRangeException(nameof(rowIndex));

        var key = orderedKeys[rowIndex];
        ImGui.Selectable(
            id,
            selection.IsSelected(key),
            ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap,
            size);

        var changed = false;
        if (enabled && ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            var io = ImGui.GetIO();
            changed |= selection.ApplyClick(orderedKeys, rowIndex, io.KeyCtrl, io.KeyShift);
        }
        if (enabled &&
            selection.IsDragging &&
            ImGui.IsItemHovered() &&
            ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            changed |= selection.ApplyDrag(orderedKeys, rowIndex);
        }
        return changed;
    }

    public static void EndRows<TKey>(TableSelectionModel<TKey> selection)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.IsDragging && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            selection.EndDrag();
    }
}
