namespace Franthropy.Web.Tables;

public readonly record struct WebTableSortState<TColumnId>(
    TColumnId? Column,
    bool Descending)
    where TColumnId : struct, Enum
{
    public static WebTableSortState<TColumnId> Unsorted { get; } = new(null, false);

    public bool IsSortedBy(TColumnId column) =>
        Column.HasValue && EqualityComparer<TColumnId>.Default.Equals(Column.Value, column);

    public WebTableSortState<TColumnId> Toggle(TColumnId column) =>
        new(column, IsSortedBy(column) && !Descending);

    public string GetAriaSort(TColumnId column) =>
        !IsSortedBy(column) ? "none" : Descending ? "descending" : "ascending";
}
