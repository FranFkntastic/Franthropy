using System.Collections.Concurrent;

namespace Franthropy.Dalamud.UI.Performance;

/// <summary>
/// A thread-safe expiring cache with a hard entry ceiling. Expiration is enforced on reads and
/// writes; over-capacity pruning removes expired and then oldest observations.
/// </summary>
public sealed class BoundedTtlCache<TKey, TValue>
    where TKey : notnull
{
    private sealed record Entry(DateTimeOffset StoredAt, TValue Value);

    private readonly ConcurrentDictionary<TKey, Entry> entries;
    private readonly TimeSpan lifetime;
    private readonly int maximumEntries;
    private readonly TimeProvider timeProvider;
    private readonly object pruneLock = new();

    public BoundedTtlCache(
        int maximumEntries,
        TimeSpan lifetime,
        TimeProvider? timeProvider = null,
        IEqualityComparer<TKey>? comparer = null)
    {
        if (maximumEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        this.maximumEntries = maximumEntries;
        this.lifetime = lifetime;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        entries = new ConcurrentDictionary<TKey, Entry>(comparer ?? EqualityComparer<TKey>.Default);
    }

    public int Count => entries.Count;

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (entries.TryGetValue(key, out var entry))
        {
            if (timeProvider.GetUtcNow() - entry.StoredAt < lifetime)
            {
                value = entry.Value;
                return true;
            }
            entries.TryRemove(new KeyValuePair<TKey, Entry>(key, entry));
        }

        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        entries[key] = new(timeProvider.GetUtcNow(), value);
        if (entries.Count > maximumEntries)
            Prune();
    }

    public void Clear() => entries.Clear();

    private void Prune()
    {
        lock (pruneLock)
        {
            var now = timeProvider.GetUtcNow();
            foreach (var pair in entries)
                if (now - pair.Value.StoredAt >= lifetime)
                    entries.TryRemove(new KeyValuePair<TKey, Entry>(pair.Key, pair.Value));

            if (entries.Count <= maximumEntries)
                return;
            foreach (var pair in entries
                         .OrderBy(pair => pair.Value.StoredAt)
                         .Take(entries.Count - maximumEntries))
            {
                entries.TryRemove(new KeyValuePair<TKey, Entry>(pair.Key, pair.Value));
            }
        }
    }
}
