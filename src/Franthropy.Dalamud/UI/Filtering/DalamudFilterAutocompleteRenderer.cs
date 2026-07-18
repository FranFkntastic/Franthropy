using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Filtering.Completion;
using Franthropy.Filtering.Semantics;

namespace Franthropy.Dalamud.UI.Filtering;

public static class DalamudFilterAutocompleteRenderer
{
    public static bool Draw<TRecord>(
        string id,
        string hint,
        FilterContext<TRecord> context,
        DalamudFilterAutocompleteState state,
        float width = -1,
        int maximumItems = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(state);

        if (state.FocusRequested)
            ImGui.SetKeyboardFocusHere();

        var expression = state.Expression;
        ImGui.SetNextItemWidth(width);
        ImGui.ImGuiInputTextCallbackDelegate callback = (ref ImGuiInputTextCallbackData data) =>
        {
            if (state.PendingCaretPosition is { } pending)
            {
                data.CursorPos = Math.Clamp(pending, 0, data.BufTextLen);
                data.SelectionStart = data.CursorPos;
                data.SelectionEnd = data.CursorPos;
                state.ConsumePendingCaret();
            }
            else
            {
                state.CaretPosition = Math.Clamp(data.CursorPos, 0, data.BufTextLen);
            }
            return 0;
        };

        var changed = ImGui.InputTextWithHint(
            $"##{id}Filter",
            hint,
            ref expression,
            512,
            ImGuiInputTextFlags.CallbackAlways,
            callback);
        if (changed)
            state.SetExpression(expression, state.CaretPosition);

        var inputActive = ImGui.IsItemActive();
        var completion = FilterCompletionService.Complete(
            context,
            new FilterCompletionRequest(context.ContextId, state.Expression, state.CaretPosition));
        var items = completion.Items.Take(Math.Max(1, maximumItems)).ToArray();

        if (inputActive && items.Length > 0)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
                state.MoveSelection(1, items.Length);
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
                state.MoveSelection(-1, items.Length);
            if (ImGui.IsKeyPressed(ImGuiKey.Tab) && state.TryApply(items))
                changed = true;
            ImGui.OpenPopup($"##{id}FilterSuggestions");
        }

        ImGui.SetNextWindowSizeConstraints(new Vector2(Math.Max(280, width), 0), new Vector2(620, 300));
        if (ImGui.BeginPopup($"##{id}FilterSuggestions"))
        {
            for (var index = 0; index < items.Length; index++)
            {
                var item = items[index];
                if (ImGui.Selectable($"{item.Label}##{id}Completion{index}", state.SelectedIndex == index))
                {
                    state.MoveSelection(index - state.SelectedIndex, items.Length);
                    if (state.TryApply(items))
                        changed = true;
                    ImGui.CloseCurrentPopup();
                    break;
                }

                if (ImGui.IsItemHovered() && (!string.IsNullOrWhiteSpace(item.Description) || !string.IsNullOrWhiteSpace(item.Detail)))
                {
                    ImGui.BeginTooltip();
                    if (!string.IsNullOrWhiteSpace(item.Description))
                        ImGui.TextWrapped(item.Description);
                    if (!string.IsNullOrWhiteSpace(item.Detail))
                        ImGui.TextDisabled(item.Detail);
                    ImGui.EndTooltip();
                }
            }
            ImGui.EndPopup();
        }

        return changed;
    }
}
