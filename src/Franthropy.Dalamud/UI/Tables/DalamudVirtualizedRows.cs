using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Tables;

/// <summary>
/// The safe row-iteration boundary for immediate-mode tables and lists. Consumers provide row
/// rendering, while this primitive owns the only loop and submits visible rows only.
/// </summary>
public static class DalamudVirtualizedRows
{
    public static unsafe int Draw<TRow>(
        IReadOnlyList<TRow> rows,
        Action<TRow, int> drawRow,
        float rowHeight = -1f)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(drawRow);
        if (rows.Count == 0)
            return 0;

        var rendered = 0;
        var clipper = ImGui.ImGuiListClipper();
        try
        {
            clipper.Begin(rows.Count, rowHeight);
            while (clipper.Step())
                rendered += DrawRange(rows, clipper.DisplayStart, clipper.DisplayEnd, drawRow);
        }
        finally
        {
            clipper.Destroy();
        }

        return rendered;
    }

    internal static int DrawRange<TRow>(
        IReadOnlyList<TRow> rows,
        int displayStart,
        int displayEnd,
        Action<TRow, int> drawRow)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(drawRow);
        var start = Math.Clamp(displayStart, 0, rows.Count);
        var end = Math.Clamp(displayEnd, start, rows.Count);
        for (var index = start; index < end; index++)
            drawRow(rows[index], index);
        return end - start;
    }
}
