using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Tables;

/// <summary>Border-safe flags for tables containing full-width details and their fixed-width nested tables.</summary>
public static class DalamudNestedTableFlags
{
    /// <summary>Fixed-fit child defaults; use fixed-width columns to keep figures beside their labels.</summary>
    public const ImGuiTableFlags Child = ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerH |
                                         ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit;

    public static ImGuiTableFlags Parent(bool resizable = false) => Child |
        // ImGui forces BordersInnerV for Resizable tables; suppress body borders so
        // parent column separators do not cut through full-width detail rows.
        (resizable ? ImGuiTableFlags.Resizable | ImGuiTableFlags.NoBordersInBodyUntilResize : ImGuiTableFlags.None);
}
