using System.Text.Json;

namespace Franthropy.Observations.V1;

/// <summary>
/// Correlates an explicitly requested retainer observation with the capture emitted by
/// the shared collector. Only one requester may own an owner/retainer key at a time.
/// </summary>
public sealed class ObservationCaptureSessionRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<SessionKey, string> active = [];
    private readonly string? sharedPath;

    public ObservationCaptureSessionRegistry(string? sharedPath = null)
    {
        this.sharedPath = string.IsNullOrWhiteSpace(sharedPath) ? null : Path.GetFullPath(sharedPath);
        if (this.sharedPath is not null)
            Directory.CreateDirectory(Path.GetDirectoryName(this.sharedPath)!);
    }

    public ObservationCaptureSession Begin(ObservationOwner owner, ulong retainerId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.LocalContentId == 0 || owner.HomeWorldId == 0 || retainerId == 0)
            throw new InvalidOperationException("Stable owner and retainer identities are required.");

        var key = new SessionKey(owner.LocalContentId, owner.HomeWorldId, retainerId);
        var sessionId = Guid.NewGuid().ToString("N");
        if (sharedPath is null)
        {
            lock (gate)
            {
                if (active.ContainsKey(key))
                    throw new InvalidOperationException("An observation session already owns this retainer.");
                active[key] = sessionId;
            }
        }
        else
            MutateShared(entries =>
            {
                RemoveExpired(entries);
                if (entries.Any(entry => entry.Matches(key)))
                    throw new InvalidOperationException("An observation session already owns this retainer.");
                entries.Add(new SharedSession(key.LocalContentId, key.HomeWorldId, key.RetainerId, sessionId, DateTimeOffset.UtcNow.AddMinutes(2)));
                return 0;
            });
        return new ObservationCaptureSession(sessionId, () => End(key, sessionId));
    }

    public string Resolve(ObservationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Subject.Kind != ObservationSubjectKind.Retainer)
            return string.Empty;
        var key = new SessionKey(scope.Owner.LocalContentId, scope.Owner.HomeWorldId, scope.Subject.Id);
        if (sharedPath is null)
        {
            lock (gate)
                return active.GetValueOrDefault(key) ?? string.Empty;
        }
        return MutateShared(entries =>
        {
            RemoveExpired(entries);
            return entries.FirstOrDefault(entry => entry.Matches(key))?.SessionId ?? string.Empty;
        });
    }

    private void End(SessionKey key, string sessionId)
    {
        if (sharedPath is null)
        {
            lock (gate)
            {
                if (active.GetValueOrDefault(key) == sessionId)
                    active.Remove(key);
            }
            return;
        }
        MutateShared(entries =>
        {
            entries.RemoveAll(entry => entry.Matches(key) && entry.SessionId == sessionId);
            RemoveExpired(entries);
            return 0;
        });
    }

    private T MutateShared<T>(Func<List<SharedSession>, T> mutate)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(sharedPath!, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var entries = stream.Length == 0
                    ? []
                    : JsonSerializer.Deserialize<List<SharedSession>>(stream) ?? [];
                var result = mutate(entries);
                stream.Position = 0;
                stream.SetLength(0);
                JsonSerializer.Serialize(stream, entries);
                stream.Flush(flushToDisk: true);
                return result;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(5);
            }
        }
    }

    private static void RemoveExpired(List<SharedSession> entries) =>
        entries.RemoveAll(entry => entry.ExpiresAtUtc <= DateTimeOffset.UtcNow);

    private readonly record struct SessionKey(ulong LocalContentId, uint HomeWorldId, ulong RetainerId);
    private sealed record SharedSession(
        ulong LocalContentId,
        uint HomeWorldId,
        ulong RetainerId,
        string SessionId,
        DateTimeOffset ExpiresAtUtc)
    {
        public bool Matches(SessionKey key) =>
            LocalContentId == key.LocalContentId &&
            HomeWorldId == key.HomeWorldId &&
            RetainerId == key.RetainerId;
    }
}

public sealed class ObservationCaptureSession(string sessionId, Action release) : IDisposable
{
    private int disposed;
    public string SessionId { get; } = sessionId;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            release();
    }
}
