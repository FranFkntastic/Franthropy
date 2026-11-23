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
/// plugins. A scope removes only the owner entries it added, so nested product
/// automation cannot resume another consumer's paused UI driver.
/// Every scope holds a lease: a holder that neither renews nor disposes its scope
/// before the lease runs out is dropped with a Warning, so a crashed holder cannot
/// leave the other plugins paused forever. <see cref="ForceRelease"/> drops every holder.
/// </summary>
public sealed class DalamudExternalUiAutomationSuppression : IDisposable
{
    /// <summary>Lease applied by <see cref="Acquire()"/>. Holders that run longer call <see cref="Scope.Renew"/>.</summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(10);
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

    public Scope Acquire() => Acquire(DefaultLease);

    /// <summary>Pauses the external UI drivers until the scope is disposed or its lease runs out without renewal.</summary>
    public Scope Acquire(TimeSpan lease)
    {
        if (lease <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lease), lease, "A positive suppression lease is required.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (holders.Count == 0)
            {
                textAdvanceOwned = AddOwner(TextAdvanceStopRequests, "TextAdvance") || textAdvanceOwned;
                yesAlreadyOwned = AddOwner(YesAlreadyStopRequests, "YesAlready") || yesAlreadyOwned;
            }
            var scope = new Scope(this, lease, clock() + lease);
            holders.Add(scope);
            return scope;
        }
    }

    /// <summary>
    /// Drops holders whose lease ran out, logging each at Warning, and restores the paused drivers when none remain.
    /// Runs periodically on its own; callers may also invoke it. Returns the number of holders dropped.
    /// </summary>
    public int ReleaseExpired()
    {
        lock (gate)
        {
            if (disposed || holders.Count == 0)
                return 0;
            var now = clock();
            var expired = holders.Where(scope => scope.ExpiresAt <= now).ToArray();
            foreach (var scope in expired)
            {
                holders.Remove(scope);
                scope.Detach();
                warning($"[{owner}] SuppressionLeaseExpired: a UI automation holder did not renew its {scope.Lease.TotalSeconds:N0}s lease " +
                        $"(expired {scope.ExpiresAt:O}); dropped it.");
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
            RestoreAll(null);
            return dropped;
        }
    }

    private bool AddOwner(string key, string product)
    {
        if (!dataStore.TryGetData<HashSet<string>>(key, out var stopRequests) || stopRequests is null)
            return false;
        lock (stopRequests)
        {
            if (!stopRequests.Add(owner))
                return false;
        }
        diagnostic($"[{owner}] Temporarily paused {product} while UI automation is active.");
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
            scope.ExpiresAt = clock() + scope.Lease;
            return true;
        }
    }

    private void RestoreAll(Scope? scope)
    {
        RestoreOwned(TextAdvanceStopRequests, "TextAdvance", ref textAdvanceOwned, scope);
        RestoreOwned(YesAlreadyStopRequests, "YesAlready", ref yesAlreadyOwned, scope);
    }

    private void RestoreOwned(
        string key,
        string product,
        ref bool owned,
        Scope? scope)
    {
        if (!owned)
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

        internal Scope(DalamudExternalUiAutomationSuppression owner, TimeSpan lease, DateTimeOffset expiresAt)
        {
            this.owner = owner;
            Lease = lease;
            ExpiresAt = expiresAt;
        }

        public List<string> RestoreFailures { get; } = [];

        public TimeSpan Lease { get; }

        public DateTimeOffset ExpiresAt { get; internal set; }

        /// <summary>False once the scope was disposed, expired or force-released.</summary>
        public bool IsHeld => Volatile.Read(ref owner) is not null;

        /// <summary>Heartbeat: extends the lease by its full length. Returns false when the scope is no longer held.</summary>
        public bool Renew() => Volatile.Read(ref owner)?.Renew(this) ?? false;

        internal void Detach() => Interlocked.Exchange(ref owner, null);

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            current?.Release(this);
        }
    }
}
