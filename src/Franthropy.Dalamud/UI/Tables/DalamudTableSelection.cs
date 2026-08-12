namespace Franthropy.Dalamud.UI.Tables;

public enum DalamudTableSelectionMode
{
    None,
    Single,
    Multi,
}

public sealed class DalamudTableSelection<TRow>
{
    private readonly Func<TRow, bool>? isSelected;
    private readonly Func<TRow, bool>? isSelectable;
    private readonly Func<IReadOnlyList<TRow>, int, bool, bool, bool, bool>? applyClick;
    private readonly Func<IReadOnlyList<TRow>, int, bool>? applyDrag;
    private readonly Func<bool>? isDragging;
    private readonly Action? endDrag;

    private DalamudTableSelection(
        DalamudTableSelectionMode mode,
        Func<TRow, bool>? isSelected = null,
        Func<TRow, bool>? isSelectable = null,
        Func<IReadOnlyList<TRow>, int, bool, bool, bool, bool>? applyClick = null,
        Func<IReadOnlyList<TRow>, int, bool>? applyDrag = null,
        Func<bool>? isDragging = null,
        Action? endDrag = null)
    {
        Mode = mode;
        this.isSelected = isSelected;
        this.isSelectable = isSelectable;
        this.applyClick = applyClick;
        this.applyDrag = applyDrag;
        this.isDragging = isDragging;
        this.endDrag = endDrag;
    }

    public DalamudTableSelectionMode Mode { get; }

    public static DalamudTableSelection<TRow> None { get; } = new(DalamudTableSelectionMode.None);

    public static DalamudTableSelection<TRow> Single<TKey>(
        TableSelectionModel<TKey> selection,
        Func<TRow, TKey> keySelector)
        where TKey : notnull =>
        Create(DalamudTableSelectionMode.Single, selection, keySelector);

    public static DalamudTableSelection<TRow> Multi<TKey>(
        TableSelectionModel<TKey> selection,
        Func<TRow, TKey> keySelector,
        Func<TRow, bool>? isSelectable = null)
        where TKey : notnull =>
        Create(DalamudTableSelectionMode.Multi, selection, keySelector, isSelectable);

    internal bool IsSelected(TRow row) => isSelected?.Invoke(row) == true;
    internal bool IsSelectable(TRow row) => isSelectable?.Invoke(row) != false;

    internal bool ApplyClick(
        IReadOnlyList<TRow> orderedRows,
        int rowIndex,
        bool control,
        bool shift,
        bool alt) =>
        IsSelectable(orderedRows[rowIndex]) &&
        applyClick?.Invoke(orderedRows, rowIndex, control, shift, alt) == true;

    internal bool ApplyDrag(IReadOnlyList<TRow> orderedRows, int rowIndex) =>
        Mode == DalamudTableSelectionMode.Multi &&
        IsSelectable(orderedRows[rowIndex]) &&
        applyDrag?.Invoke(orderedRows, rowIndex) == true;

    internal bool IsDragging =>
        Mode == DalamudTableSelectionMode.Multi && isDragging?.Invoke() == true;

    internal void EndDrag() => endDrag?.Invoke();

    private static DalamudTableSelection<TRow> Create<TKey>(
        DalamudTableSelectionMode mode,
        TableSelectionModel<TKey> selection,
        Func<TRow, TKey> keySelector,
        Func<TRow, bool>? isSelectable = null)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(keySelector);

        return new DalamudTableSelection<TRow>(
            mode,
            row => selection.IsSelected(keySelector(row)),
            isSelectable,
            (rows, rowIndex, control, shift, alt) => selection.ApplyClick(
                ProjectKeys(rows, keySelector, isSelectable, rowIndex, out var selectableIndex),
                selectableIndex,
                mode,
                control,
                shift,
                alt),
            (rows, rowIndex) => selection.ApplyDrag(
                ProjectKeys(rows, keySelector, isSelectable, rowIndex, out var selectableIndex),
                selectableIndex),
            () => selection.IsDragging,
            selection.EndDrag);
    }

    private static IReadOnlyList<TKey> ProjectKeys<TKey>(
        IReadOnlyList<TRow> rows,
        Func<TRow, TKey> keySelector,
        Func<TRow, bool>? isSelectable,
        int rowIndex,
        out int selectableIndex)
        where TKey : notnull
    {
        if (isSelectable == null)
        {
            selectableIndex = rowIndex;
            return new ProjectedReadOnlyList<TKey>(rows, keySelector);
        }

        var keys = new List<TKey>(rows.Count);
        selectableIndex = -1;
        for (var index = 0; index < rows.Count; index++)
        {
            if (isSelectable?.Invoke(rows[index]) == false)
                continue;
            if (index == rowIndex)
                selectableIndex = keys.Count;
            keys.Add(keySelector(rows[index]));
        }
        if (selectableIndex < 0)
            throw new InvalidOperationException("The selected table row is not eligible for selection.");
        return keys;
    }

    private sealed class ProjectedReadOnlyList<TKey>(
        IReadOnlyList<TRow> rows,
        Func<TRow, TKey> keySelector) : IReadOnlyList<TKey>
    {
        public int Count => rows.Count;

        public TKey this[int index] => keySelector(rows[index]);

        public IEnumerator<TKey> GetEnumerator()
        {
            for (var index = 0; index < rows.Count; index++)
                yield return keySelector(rows[index]);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
