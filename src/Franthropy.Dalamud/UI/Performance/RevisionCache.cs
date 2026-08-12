namespace Franthropy.Dalamud.UI.Performance;

/// <summary>
/// A single-revision cache for expensive immutable projections. Keeping exactly one revision
/// prevents an immediate-mode surface from accidentally growing an unbounded history.
/// </summary>
public sealed class RevisionCache<TKey, TValue>
    where TKey : notnull
{
    private readonly IEqualityComparer<TKey> comparer;
    private TKey key = default!;
    private TValue value = default!;
    private bool hasValue;

    public RevisionCache(IEqualityComparer<TKey>? comparer = null)
    {
        this.comparer = comparer ?? EqualityComparer<TKey>.Default;
    }

    public int BuildCount { get; private set; }

    public TValue GetOrCreate(TKey revision, Func<TKey, TValue> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (hasValue && comparer.Equals(key, revision))
            return value;

        var built = build(revision);
        key = revision;
        value = built;
        hasValue = true;
        BuildCount++;
        return value;
    }

    public void Clear()
    {
        key = default!;
        value = default!;
        hasValue = false;
    }
}
