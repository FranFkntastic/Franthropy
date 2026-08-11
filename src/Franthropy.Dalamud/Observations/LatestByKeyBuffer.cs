namespace Franthropy.Dalamud.Observations;

internal sealed class LatestByKeyBuffer<T> where T : class
{
    private readonly object gate = new();
    private readonly Dictionary<string, T> latest = new(StringComparer.Ordinal);
    private readonly HashSet<string> scheduled = new(StringComparer.Ordinal);

    public bool Offer(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
        {
            latest[key] = value;
            return scheduled.Add(key);
        }
    }

    public bool TryTake(string key, out T? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (gate)
        {
            var found = latest.Remove(key, out value);
            scheduled.Remove(key);
            return found;
        }
    }
}
