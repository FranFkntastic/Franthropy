using System.Globalization;
using Franthropy.Filtering.Semantics;

namespace Franthropy.Filtering.Compilation;

public sealed record FilterCompilationCacheKey(
    string Expression,
    string CatalogVersion,
    string ContextId,
    string ContextSchemaVersion,
    string Locale);

public sealed class FilterCompilationCache<TRecord>
{
    private readonly FilterContext<TRecord> context;
    private readonly FilterLimits? limits;
    private readonly int capacity;
    private readonly Dictionary<FilterCompilationCacheKey, LinkedListNode<CacheEntry>> entries = [];
    private readonly LinkedList<CacheEntry> recency = [];
    private readonly object gate = new();

    public FilterCompilationCache(FilterContext<TRecord> context, int capacity = 128, FilterLimits? limits = null)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Cache capacity must be positive.");
        this.capacity = capacity;
        this.limits = limits;
    }

    public FilterCompilation<TRecord> GetOrCompile(string? expression, CultureInfo? culture = null)
    {
        var key = CreateKey(expression ?? string.Empty, culture ?? CultureInfo.CurrentCulture);
        lock (gate)
        {
            if (entries.TryGetValue(key, out var existing))
            {
                recency.Remove(existing);
                recency.AddFirst(existing);
                return existing.Value.Compilation;
            }
        }

        var compilation = FilterCompiler.Compile(expression, context, limits);
        lock (gate)
        {
            if (entries.TryGetValue(key, out var raced))
                return raced.Value.Compilation;

            var node = recency.AddFirst(new CacheEntry(key, compilation));
            entries.Add(key, node);
            if (entries.Count > capacity)
            {
                var oldest = recency.Last!;
                recency.RemoveLast();
                entries.Remove(oldest.Value.Key);
            }
        }
        return compilation;
    }

    public FilterCompilationCacheKey CreateKey(string expression, CultureInfo culture) => new(
        expression,
        context.Catalog.Version,
        context.ContextId,
        context.SchemaVersion,
        culture.Name);

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            recency.Clear();
        }
    }

    private sealed record CacheEntry(FilterCompilationCacheKey Key, FilterCompilation<TRecord> Compilation);
}
