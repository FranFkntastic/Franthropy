using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Tables;

/// <summary>Rank-ordered slot columns whose overflow hides behind a slim reveal handle column.</summary>
/// <remarks>
/// Each row places its own entries into the slots by rank, so a slot column holds different entities per row.
/// The set always declares <see cref="ColumnCount"/> columns (every slot plus the handle) so ImGui's saved
/// layout for the table ID never shifts meaning. Hidden slots use <see cref="ImGuiTableColumnFlags.Disabled"/>,
/// which applies on the same frame, is not persisted, and stays out of the table context menu.
/// Left-clicking the handle reveals one more slot; right-clicking returns to <see cref="DefaultSlots"/>.
/// </remarks>
public sealed class DalamudRankedColumns
{
    private readonly Action<int>? preferredSlotsChanged;
    private int preferredSlots;
    private int firstColumn = -1;
    private int requiredSlots;

    /// <param name="defaultSlots">Slots shown before any reveal, and the collapse target.</param>
    /// <param name="maximumSlots">Fixed number of declared slot columns. Changing it changes the table layout.</param>
    /// <param name="preferredSlotsChanged">Called after a handle click changes <see cref="PreferredSlots"/>, e.g. to persist it.</param>
    public DalamudRankedColumns(int defaultSlots, int maximumSlots, Action<int>? preferredSlotsChanged = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(defaultSlots, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSlots, defaultSlots);
        DefaultSlots = defaultSlots;
        MaximumSlots = maximumSlots;
        this.preferredSlotsChanged = preferredSlotsChanged;
        preferredSlots = defaultSlots;
    }

    public int DefaultSlots { get; }

    public int MaximumSlots { get; }

    /// <summary>Columns this set declares: every slot plus the handle.</summary>
    public int ColumnCount => MaximumSlots + 1;

    /// <summary>The caller's persisted slot preference, clamped to [<see cref="DefaultSlots"/>, <see cref="MaximumSlots"/>].</summary>
    public int PreferredSlots
    {
        get => preferredSlots;
        set => preferredSlots = Math.Clamp(value, DefaultSlots, MaximumSlots);
    }

    /// <summary>Slots shown this frame: the preference, never more than the most entries any row has.</summary>
    public int VisibleSlots { get; private set; }

    /// <summary>Whether the handle column is shown this frame (entries are hidden, or slots were revealed).</summary>
    public bool HandleVisible { get; private set; }

    /// <summary>Table column index of the handle; valid after <see cref="SetupColumns"/>.</summary>
    public int HandleColumn => firstColumn + MaximumSlots;

    /// <summary>Whether a left-click on the handle reveals another slot this frame.</summary>
    public bool CanReveal => VisibleSlots < Math.Min(requiredSlots, MaximumSlots);

    /// <summary>Whether more than <see cref="DefaultSlots"/> are shown, so the handle can collapse.</summary>
    public bool CanCollapse => VisibleSlots > DefaultSlots;

    /// <summary>Entries a row hides beyond the visible slots.</summary>
    public int HiddenCount(int rowEntries) => Math.Max(0, rowEntries - VisibleSlots);

    /// <summary>Declares the slot and handle columns, starting at table column <paramref name="firstColumn"/>.</summary>
    /// <param name="firstColumn">Index of the first slot column; declare the preceding columns first.</param>
    /// <param name="requiredSlots">The most ranked entries any row has this frame.</param>
    /// <param name="slotLabel">Header label for a zero-based rank, e.g. <see cref="Ordinal"/>.</param>
    public void SetupColumns(int firstColumn, int requiredSlots, Func<int, string> slotLabel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstColumn);
        ArgumentOutOfRangeException.ThrowIfNegative(requiredSlots);
        ArgumentNullException.ThrowIfNull(slotLabel);
        this.firstColumn = firstColumn;
        this.requiredSlots = requiredSlots;
        VisibleSlots = Math.Min(preferredSlots, Math.Min(requiredSlots, MaximumSlots));
        HandleVisible = requiredSlots > VisibleSlots || VisibleSlots > DefaultSlots;

        const ImGuiTableColumnFlags fixedPlacement = ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.NoReorder |
                                                     ImGuiTableColumnFlags.NoSort;
        for (var rank = 0; rank < MaximumSlots; rank++)
        {
            var flags = ImGuiTableColumnFlags.WidthStretch | fixedPlacement |
                        (rank < VisibleSlots ? ImGuiTableColumnFlags.None : ImGuiTableColumnFlags.Disabled);
            // The ### suffix keeps the column ID stable if a caller's label for a rank changes.
            ImGui.TableSetupColumn($"{slotLabel(rank)}###rank{rank}", flags, 1f);
        }

        // Named for the table context menu; NoHeaderLabel keeps the header itself empty.
        ImGui.TableSetupColumn("More###rank-handle",
            ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize | ImGuiTableColumnFlags.NoHeaderLabel |
            fixedPlacement | (HandleVisible ? ImGuiTableColumnFlags.None : ImGuiTableColumnFlags.Disabled),
            HandleWidth());
    }

    /// <summary>Replaces <c>TableHeadersRow</c> for the whole table; draws the handle header as a chevron button.</summary>
    public void HeadersRow()
    {
        EnsureSetup();
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        for (var column = 0; column < ImGui.TableGetColumnCount(); column++)
        {
            if (!ImGui.TableSetColumnIndex(column))
                continue;
            if (column == HandleColumn)
            {
                DrawHandleHeader();
                continue;
            }

            var label = (ImGui.TableGetColumnFlags(column) & ImGuiTableColumnFlags.NoHeaderLabel) != 0
                ? string.Empty
                : ImGui.TableGetColumnName(column);
            ImGui.PushID(column);
            ImGui.TableHeader(label);
            ImGui.PopID();
        }
    }

    /// <summary>Moves to the slot column for a zero-based rank. Returns false when that rank is hidden.</summary>
    public bool SlotColumn(int rank)
    {
        EnsureSetup();
        return rank >= 0 && rank < VisibleSlots && ImGui.TableSetColumnIndex(firstColumn + rank);
    }

    /// <summary>Draws this row's handle cell showing "+N" hidden entries. Returns true while hovered.</summary>
    /// <remarks>Requires a unique ImGui ID scope per row. The caller may add a tooltip when this returns true.</remarks>
    public bool HandleCell(int rowEntries)
    {
        EnsureSetup();
        if (!HandleVisible || !ImGui.TableSetColumnIndex(HandleColumn))
            return false;

        var hidden = HiddenCount(rowEntries);
        var hovered = HandleButton("##rank-handle-cell", ImGui.GetTextLineHeight());
        if (hidden > 0)
        {
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var text = $"+{hidden}";
            var size = ImGui.CalcTextSize(text);
            ImGui.GetWindowDrawList().AddText(
                new Vector2(min.X + MathF.Max(0, (max.X - min.X - size.X) / 2), min.Y),
                ImGui.GetColorU32(hovered || ColumnHovered() ? ImGuiCol.Text : ImGuiCol.TextDisabled), text);
        }

        return hovered;
    }

    /// <summary>English ordinal for a one-based position: 1st, 2nd, 3rd, 4th, 11th, 21st.</summary>
    public static string Ordinal(int position)
    {
        var suffix = (position % 100) is >= 11 and <= 13 ? "th" : (position % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th",
        };
        return $"{position}{suffix}";
    }

    private void DrawHandleHeader()
    {
        var hovered = HandleButton("##rank-handle-header", ImGui.GetTextLineHeight());
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        // Drawn as geometry: the chevron glyph is not guaranteed in every game font atlas.
        var center = (min + max) / 2;
        var half = ImGui.GetTextLineHeight() * 0.22f;
        var direction = CanReveal ? 1f : -1f;
        var color = ImGui.GetColorU32(hovered || ColumnHovered() ? ImGuiCol.Text : ImGuiCol.TextDisabled);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddLine(center + new Vector2(-half * 0.5f * direction, -half), center + new Vector2(half * 0.5f * direction, 0), color, 1.6f);
        drawList.AddLine(center + new Vector2(half * 0.5f * direction, 0), center + new Vector2(-half * 0.5f * direction, half), color, 1.6f);
        if (hovered && (CanReveal || CanCollapse))
        {
            ImGui.BeginTooltip();
            HandleHint();
            ImGui.EndTooltip();
        }
    }

    /// <summary>Draws the handle's available click actions as muted lines, e.g. inside a caller's row tooltip.</summary>
    public void HandleHint()
    {
        var color = ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];
        if (CanReveal)
            ImGui.TextColored(color, "Click: show the next ranked column");
        else if (CanCollapse)
            ImGui.TextColored(color, $"Click: back to {DefaultSlots}");
        if (CanCollapse)
            ImGui.TextColored(color, $"Right-click: back to {DefaultSlots}");
    }

    private bool HandleButton(string id, float height)
    {
        // A faint strip at rest so the handle reads as a divider; full strength while the column is hovered.
        ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ColumnHovered()
            ? ImGui.GetColorU32(ImGuiCol.FrameBgHovered)
            : ImGui.GetColorU32(ImGuiCol.FrameBg, 0.55f));
        ImGui.InvisibleButton(id, new Vector2(Math.Max(1f, ImGui.GetContentRegionAvail().X), height));
        var hovered = ImGui.IsItemHovered();
        if (!hovered)
            return false;

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            ChangePreferred(CanReveal ? VisibleSlots + 1 : DefaultSlots);
        else if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ChangePreferred(DefaultSlots);
        return true;
    }

    private void ChangePreferred(int slots)
    {
        var next = Math.Clamp(slots, DefaultSlots, MaximumSlots);
        if (next == preferredSlots)
            return;
        preferredSlots = next;
        preferredSlotsChanged?.Invoke(next);
    }

    private bool ColumnHovered() =>
        (ImGui.TableGetColumnFlags(HandleColumn) & ImGuiTableColumnFlags.IsHovered) != 0;

    private void EnsureSetup()
    {
        if (firstColumn < 0)
            throw new InvalidOperationException("Call SetupColumns before drawing ranked columns.");
    }

    // Room for "+99" in the current font, so the handle scales with it.
    private static float HandleWidth() =>
        MathF.Max(ImGui.CalcTextSize("+99").X, ImGui.GetTextLineHeight()) + 4f;
}
