namespace Franthropy.Web.Tables;

public sealed class WebTableSortRule<TItem, TColumnId>
    where TColumnId : struct, Enum
{
    private readonly Func<IEnumerable<TItem>, bool, IOrderedEnumerable<TItem>> sort;

    private WebTableSortRule(
        TColumnId column,
        Func<IEnumerable<TItem>, bool, IOrderedEnumerable<TItem>> sort)
    {
        Column = column;
        this.sort = sort;
    }

    public TColumnId Column { get; }

    public static WebTableSortRule<TItem, TColumnId> Create<TKey>(
        TColumnId column,
        Func<TItem, TKey> selector,
        IComparer<TKey>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return new(column, (items, descending) => ApplySelector(items, selector, comparer, descending));
    }

    public static WebTableSortRule<TItem, TColumnId> CreateCustom(
        TColumnId column,
        Func<IEnumerable<TItem>, bool, IOrderedEnumerable<TItem>> sort)
    {
        ArgumentNullException.ThrowIfNull(sort);
        return new(column, sort);
    }

    public IOrderedEnumerable<TItem> Apply(IEnumerable<TItem> items, bool descending)
    {
        ArgumentNullException.ThrowIfNull(items);
        return sort(items, descending);
    }

    private static IOrderedEnumerable<TItem> ApplySelector<TKey>(
        IEnumerable<TItem> items,
        Func<TItem, TKey> selector,
        IComparer<TKey>? comparer,
        bool descending)
    {
        if (comparer is null)
            return descending ? items.OrderByDescending(selector) : items.OrderBy(selector);
        return descending
            ? items.OrderByDescending(selector, comparer)
            : items.OrderBy(selector, comparer);
    }
}
