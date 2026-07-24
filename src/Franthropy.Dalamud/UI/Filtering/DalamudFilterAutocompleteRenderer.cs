using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Franthropy.Filtering.Completion;
using Franthropy.Filtering.Semantics;

namespace Franthropy.Dalamud.UI.Filtering;

public static class DalamudFilterAutocompleteRenderer
{
    public const int DefaultMaximumItems = 128;
    public const int MaximumVisibleItems = 12;
    public const ImGuiInputTextFlags InputFlags =
        ImGuiInputTextFlags.CallbackAlways | ImGuiInputTextFlags.CallbackCompletion;
    public const ImGuiWindowFlags SuggestionWindowFlags =
        ImGuiWindowFlags.NoTitleBar |
        ImGuiWindowFlags.NoResize |
        ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoFocusOnAppearing |
        ImGuiWindowFlags.NoNav |
        ImGuiWindowFlags.NoDocking |
        ImGuiWindowFlags.AlwaysAutoResize;
    public static bool InputFlagsConsumeTab =>
        (InputFlags & ImGuiInputTextFlags.CallbackCompletion) != 0;
    public static bool SuggestionWindowAllowsMouseSelection =>
        (SuggestionWindowFlags & ImGuiWindowFlags.NoMouseInputs) == 0;
    public static bool SuggestionWindowAllowsScrolling =>
        (SuggestionWindowFlags & ImGuiWindowFlags.NoScrollbar) == 0;
    public static bool SuggestionWindowUsesInteractivePopupStyle =>
        (SuggestionWindowFlags & (ImGuiWindowFlags.NoMouseInputs | ImGuiWindowFlags.Tooltip)) == 0;

    public static bool Draw<TRecord>(
        string id,
        string hint,
        FilterContext<TRecord> context,
        DalamudFilterAutocompleteState state,
        float width = -1,
        int maximumItems = DefaultMaximumItems)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(state);

        var completion = FilterCompletionService.Complete(
            context,
            new FilterCompletionRequest(context.ContextId, state.Expression, state.CaretPosition),
            Math.Max(1, maximumItems));
        var items = completion.Items.ToArray();
        var changed = false;
        var scrollToSelection = false;
        if (state.IsInputActive)
        {
            var input = DalamudFilterAutocompleteInput.Decide(
                true,
                items.Length,
                ImGui.IsKeyPressed(ImGuiKey.DownArrow),
                ImGui.IsKeyPressed(ImGuiKey.UpArrow),
                false);
            if (input.SelectionDelta != 0)
            {
                state.MoveSelection(input.SelectionDelta, items.Length);
                scrollToSelection = true;
            }
        }

        if (state.FocusRequested)
            ImGui.SetKeyboardFocusHere();

        var expression = state.Expression;
        var completionApplied = false;
        ImGui.SetNextItemWidth(width);
        ImGui.ImGuiInputTextCallbackDelegate callback = (ref ImGuiInputTextCallbackData data) =>
        {
            if (data.EventFlag == ImGuiInputTextFlags.CallbackCompletion && state.TryApply(items))
            {
                ReplaceInputBuffer(ref data, state.Expression, state.CaretPosition);
                state.ConsumePendingCaret();
                completionApplied = true;
                changed = true;
                return 0;
            }

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

        if (ImGui.InputTextWithHint(
                $"##{id}Filter",
                hint,
                ref expression,
                512,
                InputFlags,
                callback))
        {
            if (!completionApplied)
                state.SetExpression(expression, state.CaretPosition);
            changed = true;
        }

        var inputActive = ImGui.IsItemActive();
        state.IsInputActive = inputActive;
        var inputMin = ImGui.GetItemRectMin();
        var inputMax = ImGui.GetItemRectMax();
        completion = FilterCompletionService.Complete(
            context,
            new FilterCompletionRequest(context.ContextId, state.Expression, state.CaretPosition),
            Math.Max(1, maximumItems));
        items = completion.Items.ToArray();
        state.IsEditingWithSuggestions = inputActive && items.Length > 0;

        var popupId = $"##{id}FilterSuggestions";
        if (state.IsEditingWithSuggestions)
            ImGui.OpenPopup(popupId);
        DrawSuggestionWindow(popupId, inputMin, inputMax, items, state, scrollToSelection, ref changed);

        return changed;
    }

    private static void DrawSuggestionWindow(
        string popupId,
        Vector2 inputMin,
        Vector2 inputMax,
        IReadOnlyList<FilterCompletionItem> items,
        DalamudFilterAutocompleteState state,
        bool scrollToSelection,
        ref bool changed)
    {
        state.MoveSelection(0, items.Count);
        var viewport = ImGui.GetWindowViewport();
        var panelWidth = Math.Clamp(inputMax.X - inputMin.X, 280f, 620f);
        var maximumPanelHeight = 38f + (MaximumVisibleItems * ImGui.GetFrameHeightWithSpacing());
        var estimatedHeight = Math.Min(maximumPanelHeight, 38f + (items.Count * ImGui.GetFrameHeightWithSpacing()));
        var workRight = viewport.WorkPos.X + viewport.WorkSize.X;
        var workBottom = viewport.WorkPos.Y + viewport.WorkSize.Y;
        var panelX = Math.Clamp(inputMin.X, viewport.WorkPos.X, Math.Max(viewport.WorkPos.X, workRight - panelWidth));
        var panelY = inputMax.Y + estimatedHeight <= workBottom
            ? inputMax.Y
            : Math.Max(viewport.WorkPos.Y, inputMin.Y - estimatedHeight);

        ImGui.SetNextWindowPos(new Vector2(panelX, panelY), ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(new Vector2(panelWidth, 0), new Vector2(panelWidth, maximumPanelHeight));
        ImGui.SetNextWindowBgAlpha(0.98f);
        if (!ImGui.BeginPopup(popupId, SuggestionWindowFlags))
            return;

        ImGui.TextDisabled($"{GetSuggestionHeading(items)} ({items.Count:N0})");
        ImGui.SameLine();
        ImGui.TextDisabled("Up/Down select | Tab insert");
        ImGui.Separator();

        var tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.NoSavedSettings;
        if (ImGui.BeginTable($"{popupId}Rows", 2, tableFlags))
        {
            ImGui.TableSetupColumn("Token", ImGuiTableColumnFlags.WidthStretch, 0.34f);
            ImGui.TableSetupColumn("Meaning", ImGuiTableColumnFlags.WidthStretch, 0.66f);
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Selectable(
                    $"{item.Label}{popupId}Completion{index}",
                    state.SelectedIndex == index,
                    ImGuiSelectableFlags.SpanAllColumns))
                {
                    state.MoveSelection(index - state.SelectedIndex, items.Count);
                    if (state.TryApply(items))
                        changed = true;
                    ImGui.CloseCurrentPopup();
                    break;
                }
                if (scrollToSelection && state.SelectedIndex == index)
                    ImGui.SetScrollHereY(0.5f);

                ImGui.TableNextColumn();
                ImGui.TextDisabled(FirstMeaningful(item.Description, item.Detail));
                if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(item.Detail))
                    ImGui.SetTooltip(item.Detail);
            }
            ImGui.EndTable();
        }

        ImGui.EndPopup();
    }

    private static void ReplaceInputBuffer(
        ref ImGuiInputTextCallbackData data,
        string expression,
        int caretPosition)
    {
        data.DeleteChars(0, data.BufTextLen);
        data.InsertChars(0, expression);
        var caretBytes = Encoding.UTF8.GetByteCount(expression.AsSpan(0, Math.Clamp(caretPosition, 0, expression.Length)));
        data.CursorPos = caretBytes;
        data.SelectionStart = caretBytes;
        data.SelectionEnd = caretBytes;
    }

    private static string GetSuggestionHeading(IReadOnlyList<FilterCompletionItem> items)
    {
        if (items.All(item => item.Kind == FilterCompletionKind.Operator))
            return "Choose an operator";
        if (items.All(item => item.Kind == FilterCompletionKind.Value))
            return "Choose a value";
        return "Suggestions";
    }

    private static string FirstMeaningful(string? primary, string? secondary) =>
        !string.IsNullOrWhiteSpace(primary)
            ? primary
            : secondary ?? string.Empty;
}
