using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Inputs;

/// <summary>Text-backed integer inputs that preserve grouping separators while editing.</summary>
public static class DalamudGroupedIntegerInput
{
    // Only active edits are retained. Context + scoped ImGui ID isolates windows and contexts.
    private static readonly Dictionary<(ImGuiContextPtr Context, uint Id), EditBuffer> Buffers = new();
    private static readonly ImGui.ImGuiInputTextCallbackDelegate CharacterFilter = FilterCharacter;

    /// <summary>Draws an Int32 input; returns true only when the numeric value changes.</summary>
    public static bool InputGroupedInt(string label, ref int value, int step = 0, int stepFast = 0,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        long number = value;
        var changed = Draw(label, ref number, step, stepFast, flags, int.MinValue, int.MaxValue);
        value = (int)number;
        return changed;
    }

    /// <summary>Draws an Int64 input; returns true only when the numeric value changes.</summary>
    /// <remarks>
    /// Uses invariant grouping when inactive and retains raw text during an edit. Invalid or
    /// empty text preserves the last valid value. Numeric/whitespace character-filter flags
    /// are replaced by the grouped-decimal filter; other flags, including AutoSelectAll,
    /// ReadOnly and EnterReturnsTrue, are forwarded. Call on the ImGui thread.
    /// </remarks>
    public static bool InputGroupedInt(string label, ref long value, long step = 0, long stepFast = 0,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None) =>
        Draw(label, ref value, step, stepFast, flags, long.MinValue, long.MaxValue);

    private static bool Draw(string label, ref long value, long step, long stepFast,
        ImGuiInputTextFlags flags, long minimum, long maximum)
    {
        var context = ImGui.GetCurrentContext();
        var frame = ImGui.GetFrameCount();
        // An input omitted from the next frame must not resurrect an abandoned raw edit.
        foreach (var key in Buffers.Where(pair => pair.Key.Context.Equals(context) &&
                     (pair.Value.Frame < frame - 1 || pair.Value.Frame > frame)).Select(pair => pair.Key).ToArray())
            Buffers.Remove(key);
        var keyId = (context, ImGui.GetID(label));
        var text = Buffers.TryGetValue(keyId, out var buffer)
            ? buffer.Text : value.ToString("N0", CultureInfo.InvariantCulture);
        var original = value;
        var hasButtons = step > 0 || stepFast > 0;
        var spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var buttonSize = ImGui.GetFrameHeight();
        if (hasButtons)
        {
            // Match InputScalar: the empty label under PushID(label) has the original ID,
            // and EndGroup forwards active/deactivated state from the input or step buttons.
            ImGui.BeginGroup();
            ImGui.PushID(label);
            ImGui.SetNextItemWidth(Math.Max(1, ImGui.CalcItemWidth() - 2 * (buttonSize + spacing)));
        }

        var inputFlags = flags & ~(ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.CharsHexadecimal |
                                  ImGuiInputTextFlags.CharsScientific | ImGuiInputTextFlags.CharsNoBlank);
        // Parse the buffer rather than InputText's return, which EnterReturnsTrue changes.
        ImGui.InputText(hasButtons ? "" : label, ref text, 1024,
            inputFlags | ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.CallbackCharFilter, CharacterFilter);
        if ((flags & ImGuiInputTextFlags.ReadOnly) == 0 && GroupedIntegerParser.TryParse(text, out long parsed))
            value = Math.Clamp(parsed, minimum, maximum);
        if (ImGui.IsItemActive()) Buffers[keyId] = new EditBuffer(text, frame);
        else Buffers.Remove(keyId);

        if (hasButtons)
        {
            ImGui.BeginDisabled((flags & ImGuiInputTextFlags.ReadOnly) != 0);
            ImGui.PushButtonRepeat(true);
            var amount = ImGui.GetIO().KeyCtrl && stepFast > 0 ? stepFast : Math.Max(0, step);
            ImGui.SameLine(0, spacing);
            if (ImGui.Button("-", new Vector2(buttonSize)))
                value = (long)Math.Clamp((decimal)value - amount, minimum, maximum);
            ImGui.SameLine(0, spacing);
            if (ImGui.Button("+", new Vector2(buttonSize)))
                value = (long)Math.Clamp((decimal)value + amount, minimum, maximum);
            ImGui.PopButtonRepeat();
            ImGui.EndDisabled();
            var end = label.IndexOf("##", StringComparison.Ordinal);
            var visibleLabel = end < 0 ? label : label[..end];
            if (visibleLabel.Length > 0)
            {
                ImGui.SameLine(0, spacing);
                ImGui.TextUnformatted(visibleLabel);
            }
            ImGui.PopID();
            ImGui.EndGroup();
        }
        return value != original;
    }

    private static int FilterCharacter(ref ImGuiInputTextCallbackData data) =>
        data.EventFlag != ImGuiInputTextFlags.CallbackCharFilter ||
        data.EventChar is >= '0' and <= '9' or '+' or '-' ||
        GroupedIntegerParser.IsSeparator((char)data.EventChar) ? 0 : 1;

    private sealed record EditBuffer(string Text, int Frame);
}
