using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace Franthropy.Dalamud.UI.Items;

public static class DalamudItemAutocompleteRenderer
{
    public static bool DrawMultiSelect(
        string id,
        IReadOnlyList<DalamudItemOption> itemOptions,
        DalamudItemAutocompleteState state,
        IReadOnlySet<uint>? selectedItemIds,
        Vector4 mutedColor,
        Vector4 successColor,
        Vector4 errorColor,
        out IReadOnlySet<uint>? updatedItemIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(itemOptions);
        ArgumentNullException.ThrowIfNull(state);

        var selected = selectedItemIds is null ? new HashSet<uint>() : new HashSet<uint>(selectedItemIds);
        var changed = false;
        if (selected.Count > 0 && ImGui.BeginTable($"##{id}SelectedItems", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Remove", ImGuiTableColumnFlags.WidthFixed, 28);
            foreach (var itemId in selected.Order().ToArray())
            {
                var option = itemOptions.FirstOrDefault(value => value.ItemId == itemId);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(option is null
                    ? "Unavailable item"
                    : DalamudItemAutocompletePresenter.FormatDisplayName(itemOptions, option));
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"x##{id}Remove{itemId}"))
                {
                    selected.Remove(itemId);
                    changed = true;
                }
            }
            ImGui.EndTable();
        }

        DrawInline(id, itemOptions, state, mutedColor, successColor, errorColor);
        if (state.SelectedItem is { } selectedItem && selected.Add(selectedItem.ItemId))
        {
            state.SearchBuffer = string.Empty;
            state.SelectedItem = null;
            changed = true;
        }

        updatedItemIds = selected.Count == 0 ? null : selected;
        return changed;
    }

    public static bool DrawInline(
        string id,
        IReadOnlyList<DalamudItemOption> itemOptions,
        DalamudItemAutocompleteState state,
        Vector4 mutedColor,
        Vector4 successColor,
        Vector4 errorColor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(itemOptions);
        ArgumentNullException.ThrowIfNull(state);

        var selectionChanged = false;
        var previous = state.SearchBuffer;
        var current = state.SearchBuffer;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint($"##{id}ItemSearch", "Search item...", ref current, 160) &&
            !string.Equals(previous, current, StringComparison.Ordinal))
        {
            state.SearchBuffer = current;
            if (state.SelectedItem is not null &&
                !state.SelectedItem.Name.Equals(state.SearchBuffer.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                state.SelectedItem = null;
                selectionChanged = true;
            }
        }

        var inputActive = ImGui.IsItemActive();
        var inputHovered = ImGui.IsItemHovered();
        var snapshot = state.Resolve(itemOptions);
        var resolved = snapshot.ResolvedItem;
        if (resolved is not null &&
            (state.SelectedItem is null || state.SelectedItem.ItemId != resolved.ItemId))
        {
            state.SelectedItem = resolved;
            selectionChanged = true;
        }

        var results = snapshot.SearchResults;
        var popupId = $"##{id}ItemSuggestions";
        if (inputActive && results.Count > 0)
            ImGui.OpenPopup(popupId);

        ImGui.SetNextWindowSizeConstraints(new Vector2(260, 0), new Vector2(520, 260));
        if (ImGui.BeginPopup(popupId))
        {
            foreach (var result in results)
            {
                var label = DalamudItemAutocompletePresenter.FormatDisplayName(itemOptions, result);
                if (!ImGui.Selectable($"{label}##{id}Item{result.ItemId}"))
                    continue;

                state.SelectedItem = result;
                state.SearchBuffer = result.Name;
                selectionChanged = true;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (inputHovered)
        {
            var message = state.SelectedItem is not null
                ? $"Selected: {DalamudItemAutocompletePresenter.FormatDisplayName(itemOptions, state.SelectedItem)}"
                : itemOptions.Count == 0
                    ? "Item catalog unavailable."
                    : state.SearchBuffer.Trim().Length < 2
                        ? "Type at least two characters."
                        : results.Count == 0 ? "No matching items." : "Choose a matching item.";
            var color = state.SelectedItem is not null
                ? successColor
                : itemOptions.Count == 0 ? errorColor : mutedColor;
            ImGui.BeginTooltip();
            ImGui.TextColored(color, message);
            ImGui.EndTooltip();
        }

        return selectionChanged;
    }

    public static IReadOnlyList<DalamudItemOption> LoadItemOptions(IDataManager dataManager)
    {
        ArgumentNullException.ThrowIfNull(dataManager);
        try
        {
            return dataManager.GetExcelSheet<LuminaItem>()
                .Where(item => item.RowId > 0)
                .Select(item => new DalamudItemOption(item.RowId, item.Name.ToString().Trim()))
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .GroupBy(item => item.ItemId)
                .Select(group => group.First())
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ItemId)
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
