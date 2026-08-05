namespace Franthropy.Dalamud.UI.Tables;

public sealed class TableSelectionModel<TKey>
    where TKey : notnull
{
    private readonly HashSet<TKey> selected;
    private readonly IEqualityComparer<TKey> comparer;
    private TKey anchor = default!;
    private bool hasAnchor;
    private int dragStart = -1;

    public TableSelectionModel(IEqualityComparer<TKey>? comparer = null)
    {
        this.comparer = comparer ?? EqualityComparer<TKey>.Default;
        selected = new HashSet<TKey>(this.comparer);
    }

    public IReadOnlySet<TKey> SelectedKeys => selected;
    public int Count => selected.Count;
    public bool IsDragging => dragStart >= 0;

    public bool IsSelected(TKey key) => selected.Contains(key);

    public bool SetSelected(TKey key, bool value) =>
        value ? selected.Add(key) : selected.Remove(key);

    public bool SelectOnly(TKey key)
    {
        var changed = selected.Count != 1 || !selected.Contains(key);
        selected.Clear();
        selected.Add(key);
        return changed;
    }

    public bool ApplyClick(
        IReadOnlyList<TKey> orderedKeys,
        int rowIndex,
        bool control,
        bool shift)
    {
        ValidateRowIndex(orderedKeys, rowIndex);
        var key = orderedKeys[rowIndex];
        var changed = false;
        if (shift && hasAnchor)
        {
            var anchorIndex = IndexOf(orderedKeys, anchor);
            if (anchorIndex >= 0)
            {
                if (!control && selected.Count > 0)
                {
                    selected.Clear();
                    changed = true;
                }
                changed |= SelectRange(orderedKeys, anchorIndex, rowIndex);
            }
            else
            {
                changed = SelectOnly(key);
                anchor = key;
                hasAnchor = true;
            }
        }
        else if (control)
        {
            changed = SetSelected(key, !selected.Contains(key));
            anchor = key;
            hasAnchor = true;
        }
        else
        {
            changed = SelectOnly(key);
            anchor = key;
            hasAnchor = true;
        }

        dragStart = rowIndex;
        return changed;
    }

    public bool ApplyClick(
        IReadOnlyList<TKey> orderedKeys,
        int rowIndex,
        DalamudTableSelectionMode mode,
        bool control,
        bool shift)
    {
        switch (mode)
        {
            case DalamudTableSelectionMode.None:
                return false;
            case DalamudTableSelectionMode.Multi:
                return ApplyClick(orderedKeys, rowIndex, control, shift);
            case DalamudTableSelectionMode.Single:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        ValidateRowIndex(orderedKeys, rowIndex);
        var key = orderedKeys[rowIndex];
        var changed = SelectOnly(key);
        anchor = key;
        hasAnchor = true;
        dragStart = -1;
        return changed;
    }

    public bool ApplyDrag(IReadOnlyList<TKey> orderedKeys, int rowIndex)
    {
        ValidateRowIndex(orderedKeys, rowIndex);
        if (dragStart < 0)
            return false;
        return SelectRange(orderedKeys, dragStart, rowIndex);
    }

    public void EndDrag() => dragStart = -1;

    public bool Retain(IEnumerable<TKey> availableKeys)
    {
        var available = availableKeys.ToHashSet(comparer);
        var changed = selected.RemoveWhere(key => !available.Contains(key)) > 0;
        if (hasAnchor && !available.Contains(anchor))
        {
            anchor = default!;
            hasAnchor = false;
            dragStart = -1;
        }
        return changed;
    }

    public void Clear()
    {
        selected.Clear();
        anchor = default!;
        hasAnchor = false;
        dragStart = -1;
    }

    private bool SelectRange(IReadOnlyList<TKey> orderedKeys, int first, int last)
    {
        var changed = false;
        var rangeFirst = Math.Min(first, last);
        var rangeLast = Math.Max(first, last);
        for (var index = rangeFirst; index <= rangeLast; index++)
            changed |= selected.Add(orderedKeys[index]);
        return changed;
    }

    private int IndexOf(IReadOnlyList<TKey> orderedKeys, TKey key)
    {
        for (var index = 0; index < orderedKeys.Count; index++)
            if (comparer.Equals(orderedKeys[index], key))
                return index;
        return -1;
    }

    private static void ValidateRowIndex(IReadOnlyList<TKey> orderedKeys, int rowIndex)
    {
        ArgumentNullException.ThrowIfNull(orderedKeys);
        if ((uint)rowIndex >= (uint)orderedKeys.Count)
            throw new ArgumentOutOfRangeException(nameof(rowIndex));
    }
}
