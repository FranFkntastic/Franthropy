using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.UI.Windows;

namespace Franthropy.Dalamud.UI.Inputs;

public static class DalamudSuggestionInputRenderer
{
    public static bool Draw<T>(string id, DalamudSuggestionInputState state,
        IReadOnlyList<DalamudSuggestion<T>> suggestions, float width, string hint,
        int maxVisibleRows, out T? picked)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(suggestions);
        picked = default;
        state.Clamp(suggestions.Count);
        var moved = false;
        var selected = false;
        var dismissed = false;
        if (state.Active)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) { state.Move(1, suggestions.Count); moved = true; }
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) { state.Move(-1, suggestions.Count); moved = true; }
            if (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.Tab))
                selected = state.TryPick(suggestions, out picked);
            dismissed = ImGui.IsKeyPressed(ImGuiKey.Escape);
        }

        var text = state.Text;
        ImGui.SetNextItemWidth(width);
        // Keyboard picks above replace the widget immediately; mouse picks replace it next
        // frame. Neither path reuses the active input that still owns the previous query.
        if (ImGui.InputTextWithHint($"##{id}:{state.InputGeneration}", hint, ref text, 160))
        {
            state.Text = text;
            state.Dismissed = false;
            state.ResetSelection();
            moved = true;
        }
        // Picking/escape must not reopen the popup from the still-active input.
        if (ImGui.IsItemActivated()) state.Dismissed = false;
        if (selected || dismissed) state.Dismissed = true;
        state.Active = ImGui.IsItemActive() && !state.Dismissed;
        var owner = DalamudOwnedWindowSurface.Capture();
        var anchor = new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y);
        var popupId = $"##{id}Suggestions";
        if (state.Active && suggestions.Count > 0) ImGui.OpenPopup(popupId);
        ImGui.SetNextWindowPos(anchor, ImGuiCond.Always);
        owner.ApplyToNextWindow();
        var height = ImGui.GetTextLineHeightWithSpacing() * Math.Min(suggestions.Count, Math.Max(1, maxVisibleRows))
                     + ImGui.GetStyle().WindowPadding.Y * 2;
        var contentWidth = suggestions.Select(s => ImGui.CalcTextSize(s.Label).X +
            (string.IsNullOrEmpty(s.Note) ? 0 : ImGui.CalcTextSize(s.Note).X + 16)).DefaultIfEmpty(0).Max();
        var popupWidth = Math.Clamp(contentWidth + ImGui.GetStyle().WindowPadding.X * 2 + ImGui.GetStyle().ScrollbarSize, width, Math.Max(width, 520));
        ImGui.SetNextWindowSizeConstraints(new Vector2(popupWidth, 0), new Vector2(popupWidth, height));
        if (ImGui.BeginPopup(popupId, ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav))
        {
            owner.KeepCurrentWindowAboveOwner();
            var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
            if (selected || dismissed || suggestions.Count == 0 || (!state.Active && !hovered))
                ImGui.CloseCurrentPopup();
            else for (var index = 0; index < suggestions.Count; index++)
            {
                var option = suggestions[index];
                var pos = ImGui.GetCursorPos();
                var availableWidth = ImGui.GetContentRegionAvail().X;
                var labelWidth = availableWidth - (string.IsNullOrEmpty(option.Note) ? 0 : ImGui.CalcTextSize(option.Note).X + 16);
                var label = option.Label;
                while (label.Length > 0 && ImGui.CalcTextSize(label + "…").X > labelWidth) label = label[..^1];
                if (label.Length < option.Label.Length) label += "…";
                if (ImGui.Selectable($"{label}##{id}{index}", state.HighlightedIndex == index))
                {
                    state.Move(index - state.HighlightedIndex, suggestions.Count);
                    selected = state.TryPick(suggestions, out picked);
                    state.Dismissed = true;
                    ImGui.CloseCurrentPopup();
                }
                if (ImGui.IsItemHovered() && label != option.Label) ImGui.SetTooltip(option.Label);
                if (moved && state.HighlightedIndex == index) ImGui.SetScrollHereY();
                if (!string.IsNullOrEmpty(option.Note))
                {
                    var after = ImGui.GetCursorPos();
                    ImGui.SetCursorPos(new Vector2(pos.X + availableWidth - ImGui.CalcTextSize(option.Note).X, pos.Y));
                    ImGui.TextDisabled(option.Note);
                    ImGui.SetCursorPos(after);
                }
            }
            ImGui.EndPopup();
        }
        return selected;
    }
}
