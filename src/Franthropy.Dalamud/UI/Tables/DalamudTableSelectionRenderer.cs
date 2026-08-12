using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Tables;

public enum DalamudTableRowBackground
{
    None,
    Selected,
    Hovered,
    Active,
}

public readonly record struct DalamudTableRowInteraction(
    bool Activated,
    bool Hovered,
    bool Active);

public static class DalamudTableSelectionRenderer
{
    public static DalamudTableRowInteraction DrawRow(
        string id,
        bool selected,
        Vector2 size,
        bool enabled = true)
    {
        var targetSize = new Vector2(
            size.X > 0f ? size.X : Math.Max(1f, ImGui.GetContentRegionAvail().X),
            size.Y > 0f ? size.Y : ImGui.GetTextLineHeightWithSpacing());

        if (!enabled)
            ImGui.BeginDisabled();
        ImGui.InvisibleButton(id, targetSize, ImGuiButtonFlags.MouseButtonLeft);
        if (!enabled)
            ImGui.EndDisabled();

        var hovered = enabled && ImGui.IsItemHovered();
        var active = enabled && ImGui.IsItemActive();
        var activated = enabled && ImGui.IsItemClicked(ImGuiMouseButton.Left);
        ApplyRowBackground(ResolveBackground(selected, hovered, active));
        return new DalamudTableRowInteraction(activated, hovered, active);
    }

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
        var interaction = DrawRow(
            id,
            selection.IsSelected(key),
            size,
            enabled);

        var changed = false;
        if (interaction.Activated)
        {
            var io = ImGui.GetIO();
            changed |= selection.ApplyClick(orderedKeys, rowIndex, io.KeyCtrl, io.KeyShift, io.KeyAlt);
        }
        if (enabled &&
            selection.IsDragging &&
            interaction.Hovered &&
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

    public static DalamudTableRowBackground ResolveBackground(
        bool selected,
        bool hovered,
        bool active) =>
        active
            ? DalamudTableRowBackground.Active
            : hovered
                ? DalamudTableRowBackground.Hovered
                : selected
                    ? DalamudTableRowBackground.Selected
                    : DalamudTableRowBackground.None;

    private static void ApplyRowBackground(DalamudTableRowBackground background)
    {
        var color = background switch
        {
            DalamudTableRowBackground.Selected => ImGuiCol.Header,
            DalamudTableRowBackground.Hovered => ImGuiCol.HeaderHovered,
            DalamudTableRowBackground.Active => ImGuiCol.HeaderActive,
            _ => (ImGuiCol?)null,
        };
        if (color is not { } rowColor)
            return;

        ImGui.TableSetBgColor(
            ImGuiTableBgTarget.RowBg0,
            ImGui.GetColorU32(rowColor));
    }

}
