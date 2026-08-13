namespace Franthropy.Dalamud.UI.Styling;

public sealed class DalamudUiMotionStateStore
{
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private ulong frame;

    public int Count => entries.Count;

    public ulong BeginFrame() => ++frame;

    public float Track(
        string key,
        bool active,
        float elapsedSeconds,
        float transitionSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var target = active ? 1f : 0f;
        var value = entries.TryGetValue(key, out var existing)
            ? existing.Value
            : target;

        if (transitionSeconds <= 0f)
        {
            value = target;
        }
        else
        {
            var distance = Math.Max(0f, elapsedSeconds) / transitionSeconds;
            value = target > value
                ? Math.Min(target, value + distance)
                : Math.Max(target, value - distance);
        }

        entries[key] = new(value, frame);
        return value;
    }

    public int Prune(ulong maximumIdleFrames = 180)
    {
        var cutoff = frame > maximumIdleFrames ? frame - maximumIdleFrames : 0;
        var stale = entries
            .Where(pair => pair.Value.LastTouchedFrame < cutoff)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in stale)
            entries.Remove(key);
        return stale.Length;
    }

    public void Clear() => entries.Clear();

    private readonly record struct Entry(float Value, ulong LastTouchedFrame);
}
