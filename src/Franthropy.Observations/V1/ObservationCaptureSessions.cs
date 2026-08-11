using System.Text.Json;

namespace Franthropy.Observations.V1;

/// <summary>
/// Correlates an explicitly requested retainer observation with the capture emitted by
/// the shared collector. Only one requester may own an owner/retainer key at a time.
/// </summary>
public sealed class ObservationCaptureSessionRegistry
{
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultRenewalInterval = TimeSpan.FromSeconds(30);
    private readonly object gate = new();
    private readonly Dictionary<SessionKey, string> active = [];
    private readonly string? sharedPath;
    private readonly TimeSpan leaseDuration;
    private readonly TimeSpan renewalInterval;

    public ObservationCaptureSessionRegistry(string? sharedPath = null)
        : this(sharedPath, DefaultLeaseDuration, DefaultRenewalInterval)
    {
    }

    internal ObservationCaptureSessionRegistry(
        string? sharedPath,
        TimeSpan leaseDuration,
        TimeSpan renewalInterval)
    {
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (renewalInterval <= TimeSpan.Zero || renewalInterval >= leaseDuration)
            throw new ArgumentOutOfRangeException(nameof(renewalInterval));

        this.sharedPath = string.IsNullOrWhiteSpace(sharedPath) ? null : Path.GetFullPath(sharedPath);
        this.leaseDuration = leaseDuration;
        this.renewalInterval = renewalInterval;
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
                entries.Add(new SharedSession(key.LocalContentId, key.HomeWorldId, key.RetainerId, sessionId, DateTimeOffset.UtcNow.Add(leaseDuration)));
                return 0;
            });
        return new ObservationCaptureSession(
            sessionId,
            () => End(key, sessionId),
            sharedPath is null ? null : () => Renew(key, sessionId),
            renewalInterval);
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

    private void Renew(SessionKey key, string sessionId) =>
        MutateShared(entries =>
        {
            var index = entries.FindIndex(entry => entry.Matches(key) && entry.SessionId == sessionId);
            if (index >= 0)
                entries[index] = entries[index] with { ExpiresAtUtc = DateTimeOffset.UtcNow.Add(leaseDuration) };
            RemoveExpired(entries);
            return 0;
        });

    private T MutateShared<T>(Func<List<SharedSession>, T> mutate)
    {
        for (var attempt = 0; ; attempt++)
        {
            string? temporaryPath = null;
            try
            {
                using var lease = new FileStream($"{sharedPath}.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var entries = ReadSharedEntries();
                var result = mutate(entries);
                temporaryPath = $"{sharedPath}.{Guid.NewGuid():N}.tmp";
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, entries);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, sharedPath!, overwrite: true);
                temporaryPath = null;
                return result;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(5);
            }
            finally
            {
                if (temporaryPath is not null)
                    File.Delete(temporaryPath);
            }
        }
    }

    private List<SharedSession> ReadSharedEntries()
    {
        if (!File.Exists(sharedPath!))
            return [];

        try
        {
            using var stream = new FileStream(sharedPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length == 0
                ? []
                : JsonSerializer.Deserialize<List<SharedSession>>(stream) ?? [];
        }
        catch (JsonException)
        {
            // Capture sessions are transient leases. A torn legacy file has no durable
            // authority and must not poison all future observation capture.
            return [];
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

public sealed class ObservationCaptureSession : IDisposable
{
    private readonly Action release;
    private readonly Timer? renewalTimer;
    private int disposed;

    internal ObservationCaptureSession(
        string sessionId,
        Action release,
        Action? renew,
        TimeSpan renewalInterval)
    {
        SessionId = sessionId;
        this.release = release;
        if (renew is not null)
            renewalTimer = new Timer(_ =>
            {
                if (Volatile.Read(ref disposed) != 0)
                    return;
                try
                {
                    renew();
                }
                catch (IOException)
                {
                    // A later heartbeat can recover from a transient lock collision.
                }
            }, null, renewalInterval, renewalInterval);
    }

    public string SessionId { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            renewalTimer?.Dispose();
            release();
        }
    }
}
