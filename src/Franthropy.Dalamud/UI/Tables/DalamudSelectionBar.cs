using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Franthropy.Dalamud.UI.Styling;

namespace Franthropy.Dalamud.UI.Tables;

/// <summary>
/// A tinted, bordered bar above a selectable table: the selection count, then the caller's bulk actions.
/// The bar grows to as many lines as the actions wrap onto, so it never shows a scrollbar.
/// </summary>
/// <remarks>
/// Keep one instance per bar. It remembers last frame's line count to size this frame's child,
/// so a change in wrapping settles after one frame without jitter. Lay the actions out with
/// <see cref="Layout.DalamudUiLayout.SameLineIfFits(string)"/> so they wrap instead of clipping.
/// </remarks>
public sealed class DalamudSelectionBar
{
    private static readonly Vector2 Padding = new(8f, 5f);
    private readonly string id;
    private int lineCount = 1;

    /// <param name="id">Child window ID; unique within the parent window.</param>
    public DalamudSelectionBar(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        this.id = id;
    }

    /// <summary>Label shown while nothing is selected.</summary>
    public string EmptyText { get; init; } = "No items selected";

    /// <summary>Label for a non-zero selection; "N item(s) selected" when null.</summary>
    public Func<int, string>? FormatCount { get; init; }

    /// <summary>Lines the bar occupied when last drawn.</summary>
    public int LineCount => lineCount;

    /// <summary>Draws the bar across the available width.</summary>
    /// <param name="selectedCount">Selected item count; zero draws the bar in its quiet state.</param>
    /// <param name="accent">Tint for the bar and the count label.</param>
    /// <param name="drawActions">Draws everything after the count label, inside the bar; receives whether
    /// anything is selected. Start with a same-line call to continue the label's line.</param>
    public void Draw(int selectedCount, Vector4 accent, Action<bool>? drawActions = null)
    {
        var hasSelection = selectedCount > 0;
        var background = DalamudUiPalette.Scale(accent, hasSelection ? .16f : .09f, .96f);
        var border = DalamudUiPalette.Scale(accent, hasSelection ? .65f : .38f, 1f);
        var height = (ImGui.GetFrameHeightWithSpacing() * lineCount) + (Padding.Y * 2f);

        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, background)
            .Push(ImGuiCol.Border, border);
        using var styles = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 2f)
            .Push(ImGuiStyleVar.WindowPadding, Padding);
        using var child = ImRaii.Child(
            id,
            new Vector2(0f, height),
            true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!child)
            return;

        var top = ImGui.GetCursorPosY();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(
            hasSelection ? accent : ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled],
            hasSelection ? (FormatCount ?? DefaultCount)(selectedCount) : EmptyText);
        drawActions?.Invoke(hasSelection);

        // The cursor ends at the start of the line after the last one drawn.
        var lines = (int)MathF.Round((ImGui.GetCursorPosY() - top) / ImGui.GetFrameHeightWithSpacing());
        lineCount = Math.Max(1, lines);
    }

    private static string DefaultCount(int count) =>
        $"{count:N0} item{(count == 1 ? string.Empty : "s")} selected";
}
