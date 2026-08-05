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
    private readonly Func<IReadOnlyList<TRow>, int, bool, bool, bool>? applyClick;
    private readonly Func<IReadOnlyList<TRow>, int, bool>? applyDrag;
    private readonly Func<bool>? isDragging;
    private readonly Action? endDrag;

    private DalamudTableSelection(
        DalamudTableSelectionMode mode,
        Func<TRow, bool>? isSelected = null,
        Func<IReadOnlyList<TRow>, int, bool, bool, bool>? applyClick = null,
        Func<IReadOnlyList<TRow>, int, bool>? applyDrag = null,
        Func<bool>? isDragging = null,
        Action? endDrag = null)
    {
        Mode = mode;
        this.isSelected = isSelected;
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
        Func<TRow, TKey> keySelector)
        where TKey : notnull =>
        Create(DalamudTableSelectionMode.Multi, selection, keySelector);

    internal bool IsSelected(TRow row) => isSelected?.Invoke(row) == true;

    internal bool ApplyClick(
        IReadOnlyList<TRow> orderedRows,
        int rowIndex,
        bool control,
        bool shift) =>
        applyClick?.Invoke(orderedRows, rowIndex, control, shift) == true;

    internal bool ApplyDrag(IReadOnlyList<TRow> orderedRows, int rowIndex) =>
        Mode == DalamudTableSelectionMode.Multi &&
        applyDrag?.Invoke(orderedRows, rowIndex) == true;

    internal bool IsDragging =>
        Mode == DalamudTableSelectionMode.Multi && isDragging?.Invoke() == true;

    internal void EndDrag() => endDrag?.Invoke();

    private static DalamudTableSelection<TRow> Create<TKey>(
        DalamudTableSelectionMode mode,
        TableSelectionModel<TKey> selection,
        Func<TRow, TKey> keySelector)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(keySelector);

        return new DalamudTableSelection<TRow>(
            mode,
            row => selection.IsSelected(keySelector(row)),
            (rows, rowIndex, control, shift) => selection.ApplyClick(
                new ProjectedReadOnlyList<TKey>(rows, keySelector),
                rowIndex,
                mode,
                control,
                shift),
            (rows, rowIndex) => selection.ApplyDrag(
                new ProjectedReadOnlyList<TKey>(rows, keySelector),
                rowIndex),
            () => selection.IsDragging,
            selection.EndDrag);
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
