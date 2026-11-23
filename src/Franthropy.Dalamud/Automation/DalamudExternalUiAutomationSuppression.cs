using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Franthropy.Dalamud.Automation;

internal interface ISharedPluginDataStore
{
    bool TryGetData<T>(string key, out T? data)
        where T : class;
}

internal sealed class DalamudSharedPluginDataStore(IDalamudPluginInterface pluginInterface) : ISharedPluginDataStore
{
    public bool TryGetData<T>(string key, out T? data)
        where T : class =>
        pluginInterface.TryGetData(key, out data);
}

/// <summary>
/// Ref-counted ownership of the shared stop-request sets used by prompt-driving
/// plugins. Releases remove only this instance's owner name (including one a previous
/// instance left behind), so another consumer's paused UI driver is never resumed.
/// A live holder never times out: <see cref="Acquire()"/> holds until the scope is disposed,
/// <see cref="ForceRelease"/> runs or this instance is disposed (plugin unload). A holder that also wants
/// protection against its own owner stalling acquires with <see cref="Acquire(TimeSpan)"/> and calls
/// <see cref="Scope.Renew"/> from its tick; if renewals stop for a whole heartbeat lease, it is dropped with a
/// Warning. <see cref="ForceRelease"/> drops every holder.
/// </summary>
public sealed class DalamudExternalUiAutomationSuppression : IDisposable
{
    /// <summary>Suggested lease for <see cref="Acquire(TimeSpan)"/> holders that renew every tick.</summary>
    public static readonly TimeSpan RecommendedHeartbeatLease = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ExpiryCheckInterval = TimeSpan.FromSeconds(15);
    private const string TextAdvanceStopRequests = "TextAdvance.StopRequests";
    private const string YesAlreadyStopRequests = "YesAlready.StopRequests";
    private readonly ISharedPluginDataStore dataStore;
    private readonly Action<string> diagnostic;
    private readonly Action<string> warning;
    private readonly Func<DateTimeOffset> clock;
    private readonly string owner;
    private readonly object gate = new();
    private readonly HashSet<Scope> holders = [];
    private readonly Timer? expiryTimer;
    private bool textAdvanceOwned;
    private bool yesAlreadyOwned;
    private bool disposed;

    public DalamudExternalUiAutomationSuppression(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        string owner)
        : this(
            new DalamudSharedPluginDataStore(pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface))),
            message => (log ?? throw new ArgumentNullException(nameof(log))).Debug(message),
            owner,
            message => log.Warning("{Message}", message),
            startExpiryTimer: true)
    {
    }

    internal DalamudExternalUiAutomationSuppression(
        ISharedPluginDataStore dataStore,
        Action<string> diagnostic,
        string owner,
        Action<string>? warning = null,
        Func<DateTimeOffset>? clock = null,
        bool startExpiryTimer = false)
    {
        this.dataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
        this.diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        this.warning = warning ?? diagnostic;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.owner = string.IsNullOrWhiteSpace(owner)
            ? throw new ArgumentException("A stop-request owner is required.", nameof(owner))
            : owner.Trim();
        if (startExpiryTimer)
            expiryTimer = new Timer(_ => ReleaseExpired(), null, ExpiryCheckInterval, ExpiryCheckInterval);
    }

    /// <summary>Number of live (unexpired, undisposed) holders.</summary>
    public int HolderCount
    {
        get
        {
            lock (gate)
                return holders.Count;
        }
    }

    /// <summary>
    /// Pauses the external UI drivers until the scope is disposed, <see cref="ForceRelease"/> runs or this instance is
    /// disposed. Never times out, so it needs no renewal.
    /// </summary>
    public Scope Acquire() => AcquireCore(null);

    /// <summary>
    /// Heartbeat hold: pauses the external UI drivers like <see cref="Acquire()"/>, but the holder must call
    /// <see cref="Scope.Renew"/> at least once per <paramref name="heartbeatLease"/> (renewing every tick is the
    /// intended pattern). If renewals stop for a whole lease, the holder is dropped with a Warning.
    /// </summary>
    public Scope Acquire(TimeSpan heartbeatLease)
    {
        if (heartbeatLease <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(heartbeatLease), heartbeatLease, "A positive heartbeat lease is required.");
        return AcquireCore(heartbeatLease);
    }

    private Scope AcquireCore(TimeSpan? lease)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (holders.Count == 0)
            {
                textAdvanceOwned = AddOwner(TextAdvanceStopRequests, "TextAdvance") || textAdvanceOwned;
                yesAlreadyOwned = AddOwner(YesAlreadyStopRequests, "YesAlready") || yesAlreadyOwned;
            }
            var scope = new Scope(this, lease, lease is { } length ? clock() + length : null);
            holders.Add(scope);
            return scope;
        }
    }

    /// <summary>
    /// Drops heartbeat holders that stopped renewing for a whole lease, logging each at Warning, and restores the
    /// paused drivers when none remain. Holders from <see cref="Acquire()"/> never expire. Runs periodically on its
    /// own; callers may also invoke it. Returns the number of holders dropped.
    /// </summary>
    public int ReleaseExpired()
    {
        lock (gate)
        {
            if (disposed || holders.Count == 0)
                return 0;
            var now = clock();
            var expired = holders.Where(scope => scope.ExpiresAt is { } expiresAt && expiresAt <= now).ToArray();
            foreach (var scope in expired)
            {
                holders.Remove(scope);
                scope.Detach();
                warning($"[{owner}] SuppressionHeartbeatExpired: a UI automation holder stopped renewing its " +
                        $"{scope.Lease!.Value.TotalSeconds:N0}s heartbeat lease (expired {scope.ExpiresAt:O}); dropped it.");
            }
            if (expired.Length != 0 && holders.Count == 0)
                RestoreAll(null);
            return expired.Length;
        }
    }

    /// <summary>
    /// Escape hatch: drops every holder and restores the paused drivers now, whatever is holding them. Logged at
    /// Warning. Later disposal or renewal of a dropped scope is a no-op. Returns the number of holders dropped.
    /// </summary>
    public int ForceRelease(string reason)
    {
        lock (gate)
        {
            var dropped = holders.Count;
            foreach (var scope in holders)
                scope.Detach();
            holders.Clear();
            warning($"[{owner}] SuppressionForceReleased: dropped {dropped} holder(s) and restored paused UI automation. reason={reason}");
            RestoreAll(null, force: true);
            return dropped;
        }
    }

    private bool AddOwner(string key, string product)
    {
        if (!dataStore.TryGetData<HashSet<string>>(key, out var stopRequests) || stopRequests is null)
            return false;
        bool added;
        lock (stopRequests)
            added = stopRequests.Add(owner);
        // An entry under our owner name is ours even when we did not add it (a previous instance that never
        // disposed), so every release path removes it instead of leaving the pause stuck until a game restart.
        diagnostic(added
            ? $"[{owner}] Temporarily paused {product} while UI automation is active."
            : $"[{owner}] Took over an existing {product} pause under this owner name; it is released with this hold.");
        return true;
    }

    private void Release(Scope scope)
    {
        lock (gate)
        {
            if (!holders.Remove(scope) || holders.Count != 0)
                return;
            RestoreAll(scope);
        }
    }

    private bool Renew(Scope scope)
    {
        lock (gate)
        {
            if (disposed || !holders.Contains(scope))
                return false;
            if (scope.Lease is { } lease)
                scope.ExpiresAt = clock() + lease;
            return true;
        }
    }

    private void RestoreAll(Scope? scope, bool force = false)
    {
        RestoreOwned(TextAdvanceStopRequests, "TextAdvance", ref textAdvanceOwned, scope, force);
        RestoreOwned(YesAlreadyStopRequests, "YesAlready", ref yesAlreadyOwned, scope, force);
    }

    private void RestoreOwned(
        string key,
        string product,
        ref bool owned,
        Scope? scope,
        bool force)
    {
        // force (ForceRelease) removes our owner name even when this instance never recorded owning it.
        if (!owned && !force)
            return;
        try
        {
            if (dataStore.TryGetData<HashSet<string>>(key, out var stopRequests) && stopRequests is not null)
            {
                lock (stopRequests)
                    stopRequests.Remove(owner);
            }
            diagnostic($"[{owner}] Restored {product} after UI automation.");
            owned = false;
        }
        catch (Exception exception)
        {
            if (scope is not null)
                scope.RestoreFailures.Add($"{product}: {exception.Message}");
            else
                warning($"[{owner}] Could not restore {product}: {exception.Message}");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            expiryTimer?.Dispose();
            foreach (var scope in holders)
                scope.Detach();
            holders.Clear();
            RestoreAll(null);
        }
    }

    public sealed class Scope : IDisposable
    {
        private DalamudExternalUiAutomationSuppression? owner;

        internal Scope(DalamudExternalUiAutomationSuppression owner, TimeSpan? lease, DateTimeOffset? expiresAt)
        {
            this.owner = owner;
            Lease = lease;
            ExpiresAt = expiresAt;
        }

        public List<string> RestoreFailures { get; } = [];

        /// <summary>Heartbeat lease, or null for a hold that never expires.</summary>
        public TimeSpan? Lease { get; }

        /// <summary>When a heartbeat hold is dropped unless renewed first; null for a hold that never expires.</summary>
        public DateTimeOffset? ExpiresAt { get; internal set; }

        /// <summary>False once the scope was disposed, expired or force-released.</summary>
        public bool IsHeld => Volatile.Read(ref owner) is not null;

        /// <summary>Heartbeat: extends a leased hold by its full length (no-op for an unleased hold). Returns false
        /// when the scope is no longer held, so a caller can tell its pause was dropped.</summary>
        public bool Renew() => Volatile.Read(ref owner)?.Renew(this) ?? false;

        internal void Detach() => Interlocked.Exchange(ref owner, null);

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            current?.Release(this);
        }
    }
}
