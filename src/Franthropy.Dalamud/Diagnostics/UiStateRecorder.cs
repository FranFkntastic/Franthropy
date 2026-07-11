using System.Collections.ObjectModel;

namespace Franthropy.Dalamud.Diagnostics;

public enum UiStateEventKind
{
    SessionStarted,
    SessionStopped,
    AddonLifecycle,
    AddonReceiveEvent,
    StateChanged,
    Marker,
    Diagnostic,
}

public sealed record UiStateCaptureEvent(
    long Sequence,
    DateTimeOffset TimestampUtc,
    UiStateEventKind Kind,
    string Source,
    string Name,
    IReadOnlyDictionary<string, string?> Details);

public sealed record UiStateCaptureSession(
    Guid SessionId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? StoppedAtUtc,
    string Name,
    IReadOnlyList<UiStateCaptureEvent> Events,
    bool Truncated);

/// <summary>
/// Dependency-free, bounded event recorder for UI-state diagnostics. Runtime adapters decide
/// which UI framework events and state fields to observe; this class owns ordering, deduplication,
/// bounds, and immutable session output.
/// </summary>
public sealed class UiStateRecorder
{
    private readonly object gate = new();
    private readonly int maximumEvents;
    private readonly List<UiStateCaptureEvent> events = [];
    private Dictionary<string, string?> lastState = new(StringComparer.Ordinal);
    private Guid sessionId;
    private DateTimeOffset startedAtUtc;
    private DateTimeOffset? stoppedAtUtc;
    private string name = string.Empty;
    private long sequence;
    private bool truncated;

    public UiStateRecorder(int maximumEvents = 25_000)
    {
        if (maximumEvents < 100)
            throw new ArgumentOutOfRangeException(nameof(maximumEvents));
        this.maximumEvents = maximumEvents;
    }

    public bool IsRecording { get; private set; }

    public UiStateCaptureSession Start(string sessionName, DateTimeOffset timestampUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionName))
            throw new ArgumentException("A capture session name is required.", nameof(sessionName));
        lock (gate)
        {
            if (IsRecording)
                throw new InvalidOperationException("A UI-state capture session is already active.");
            events.Clear();
            lastState = new Dictionary<string, string?>(StringComparer.Ordinal);
            sessionId = Guid.NewGuid();
            startedAtUtc = timestampUtc;
            stoppedAtUtc = null;
            name = sessionName.Trim();
            sequence = 0;
            truncated = false;
            IsRecording = true;
            Append(timestampUtc, UiStateEventKind.SessionStarted, "recorder", name, EmptyDetails);
            return Snapshot();
        }
    }

    public void Record(DateTimeOffset timestampUtc, UiStateEventKind kind, string source, string eventName, IReadOnlyDictionary<string, string?>? details = null)
    {
        lock (gate)
        {
            if (!IsRecording)
                return;
            Append(timestampUtc, kind, source, eventName, details ?? EmptyDetails);
        }
    }

    public bool RecordStateChange(DateTimeOffset timestampUtc, string source, IReadOnlyDictionary<string, string?> state)
    {
        lock (gate)
        {
            if (!IsRecording)
                return false;
            var normalized = new Dictionary<string, string?>(state, StringComparer.Ordinal);
            if (DictionaryEqual(lastState, normalized))
                return false;
            var changed = normalized
                .Where(pair => !lastState.TryGetValue(pair.Key, out var previous) || !string.Equals(previous, pair.Value, StringComparison.Ordinal))
                .Concat(lastState.Keys.Where(key => !normalized.ContainsKey(key)).Select(key => new KeyValuePair<string, string?>(key, null)))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            lastState = normalized;
            Append(timestampUtc, UiStateEventKind.StateChanged, source, "state-diff", new ReadOnlyDictionary<string, string?>(changed));
            return true;
        }
    }

    public UiStateCaptureSession Stop(DateTimeOffset timestampUtc)
    {
        lock (gate)
        {
            if (!IsRecording)
                return Snapshot();
            Append(timestampUtc, UiStateEventKind.SessionStopped, "recorder", name, EmptyDetails);
            stoppedAtUtc = timestampUtc;
            IsRecording = false;
            return Snapshot();
        }
    }

    public UiStateCaptureSession Snapshot()
    {
        lock (gate)
            return new(sessionId, startedAtUtc, stoppedAtUtc, name, events.ToArray(), truncated);
    }

    private void Append(DateTimeOffset timestampUtc, UiStateEventKind kind, string source, string eventName, IReadOnlyDictionary<string, string?> details)
    {
        if (events.Count >= maximumEvents)
        {
            truncated = true;
            return;
        }
        events.Add(new(++sequence, timestampUtc, kind, source, eventName, new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(details, StringComparer.Ordinal))));
    }

    private static bool DictionaryEqual(IReadOnlyDictionary<string, string?> left, IReadOnlyDictionary<string, string?> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static IReadOnlyDictionary<string, string?> EmptyDetails { get; } = new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>());
}
