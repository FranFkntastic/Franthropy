using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Tables;

/// <summary>Variable-height detail content in the current table's visible viewport.</summary>
internal static class DalamudTableDetailRow
{
    // The table remains the layout owner: finishing the cell measures the callback's
    // cursor maximum and advances the next row. No child window or height cache.
    internal static unsafe void Draw(string id, Action draw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(draw);
        var table = ImGuiP.GetCurrentTable();
        if (table.IsNull)
            throw new InvalidOperationException("Detail content requires an active table.");

        ImGui.TableNextRow();
        // Select an enabled column, not logical column zero (which may be hidden).
        for (var column = 0; column < ImGui.TableGetColumnCount(); column++)
        {
            if ((ImGui.TableGetColumnFlags(column) & ImGuiTableColumnFlags.IsEnabled) == 0)
                continue;
            ImGui.TableSetColumnIndex(column);
            break;
        }

        var window = ImGuiP.GetCurrentWindow();
        var work = window.WorkRect;
        var parentWork = window.ParentWorkRect;
        var clip = window.ClipRect;
        var skip = window.SkipItems;
        var cursorMaxX = window.DC.CursorMaxPos.X;
        var columnsOffset = window.DC.ColumnsOffset;
        var viewport = table.InnerClipRect;
        // Background channel two follows the unfrozen region when headers/rows freeze.
        viewport.Min.Y = Math.Max(viewport.Min.Y, table.Bg2ClipRectForDrawCmd.Min.Y);
        viewport.Max.Y = Math.Min(viewport.Max.Y, table.Bg2ClipRectForDrawCmd.Max.Y);
        var bounds = ContentBounds(viewport.Min.X, viewport.Max.X, ImGui.GetStyle().CellPadding.X);
        ImGuiP.TablePushBackgroundChannel();
        ImGui.PushID(id);
        try
        {
            window.WorkRect.Min.X = bounds.Min;
            window.WorkRect.Max.X = bounds.Max;
            window.ParentWorkRect = window.WorkRect;
            // Indent/Unindent recompute X from this offset, not from WorkRect.
            window.DC.ColumnsOffset.X = bounds.Min - window.Pos.X - window.DC.Indent.X;
            window.SkipItems = false;
            ImGui.PushClipRect(viewport.Min, viewport.Max, false);
            try
            {
                // Direct cell positioning avoids leaving ImGui's SetCursorPos assertion
                // marker behind if the callback throws before submitting its first item.
                window.DC.CursorPos.X = bounds.Min;
                draw();
            }
            finally
            {
                ImGui.PopClipRect();
            }
        }
        finally
        {
            ImGui.PopID();
            ImGuiP.TablePopBackgroundChannel();
            window.WorkRect = work;
            window.ParentWorkRect = parentWork;
            window.ClipRect = clip;
            window.SkipItems = skip;
            window.DC.ColumnsOffset = columnsOffset;
            // Detail width is not evidence for this individual column's auto width.
            window.DC.CursorMaxPos.X = cursorMaxX;
        }
    }

    internal static (float Min, float Max) ContentBounds(float left, float right, float padding)
    {
        var inset = Math.Min(Math.Max(0, padding), Math.Max(0, right - left) / 2);
        return (left + inset, Math.Max(left + inset, right - inset));
    }
}
