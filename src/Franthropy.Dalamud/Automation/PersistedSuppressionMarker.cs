using Franthropy.Dalamud.Persistence;

namespace Franthropy.Dalamud.Automation;

/// <summary>
/// Ref-counted hold on a third-party suppression whose state outlives this process (for example another plugin's
/// feature switched off over IPC, or a global suppress flag). Before the first holder suppresses, a small marker file is
/// written under a caller-supplied directory; after the last holder restores, it is deleted. A marker found at load
/// therefore means a previous instance suppressed and never restored, and <see cref="RestoreLeftovers"/> restores it.
/// Overlapping holders inside one process share the suppression: only the last release restores.
/// The suppress and restore actions must be idempotent. One instance per marker name per process.
/// </summary>
public sealed class PersistedSuppressionMarker
{
    private readonly string markerPath;
    private readonly string name;
    private readonly Action suppress;
    private readonly Action restore;
    private readonly Action<string> warning;
    private readonly Action<string> diagnostic;
    private readonly object gate = new();
    private readonly HashSet<Scope> holders = [];

    /// <param name="markerDirectory">Directory that holds the marker file, normally the plugin's own config directory.</param>
    /// <param name="name">Stable name of the suppression; also names the marker file.</param>
    /// <param name="suppress">Applies the suppression. Must be idempotent.</param>
    /// <param name="restore">Undoes the suppression. Must be idempotent and safe to run when nothing is suppressed.</param>
    /// <param name="warning">Warning-level log sink.</param>
    /// <param name="diagnostic">Optional Debug-level log sink.</param>
    public PersistedSuppressionMarker(
        string markerDirectory,
        string name,
        Action suppress,
        Action restore,
        Action<string> warning,
        Action<string>? diagnostic = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markerDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.name = name.Trim();
        this.suppress = suppress ?? throw new ArgumentNullException(nameof(suppress));
        this.restore = restore ?? throw new ArgumentNullException(nameof(restore));
        this.warning = warning ?? throw new ArgumentNullException(nameof(warning));
        this.diagnostic = diagnostic ?? (_ => { });
        var fileName = string.Concat(this.name.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c));
        markerPath = Path.Combine(markerDirectory, $"{fileName}.suppression-marker.json");
    }

    /// <summary>Full path of the marker file.</summary>
    public string MarkerPath => markerPath;

    /// <summary>Number of live holders in this process.</summary>
    public int HolderCount
    {
        get
        {
            lock (gate)
                return holders.Count;
        }
    }

    /// <summary>True when a marker file exists, whether written by this instance or left by a previous one.</summary>
    public bool IsMarked => File.Exists(markerPath);

    /// <summary>
    /// Takes a hold. The first holder writes the marker, then suppresses. If suppressing throws, the hold is not taken,
    /// the restore action runs, and the exception propagates.
    /// </summary>
    public Scope Acquire()
    {
        lock (gate)
        {
            if (holders.Count == 0)
            {
                WriteMarker();
                try
                {
                    suppress();
                }
                catch
                {
                    TryRestoreAndClear("suppress failed");
                    throw;
                }

                diagnostic($"[{name}] Suppressed; marker written to {markerPath}.");
            }

            var scope = new Scope(this);
            holders.Add(scope);
            return scope;
        }
    }

    /// <summary>
    /// Load-time reconciliation: when a marker exists and this process holds nothing, runs the restore action, deletes
    /// the marker and logs a Warning. Returns true when a leftover suppression was restored. A failed restore keeps the
    /// marker so a later call retries.
    /// </summary>
    public bool RestoreLeftovers()
    {
        lock (gate)
        {
            if (holders.Count != 0 || !File.Exists(markerPath))
                return false;
            var leftover = DescribeMarker();
            if (!TryRestoreAndClear("leftover from a previous instance"))
                return false;
            warning($"[{name}] SuppressionLeftoverRestored: found a suppression marker with no live holder ({leftover}); restored it.");
            return true;
        }
    }

    /// <summary>
    /// Escape hatch: drops every holder, restores and deletes the marker now, whatever is holding it. Logged at Warning.
    /// Later disposal of a dropped scope is a no-op. Returns the number of holders dropped.
    /// </summary>
    public int ForceRelease(string reason)
    {
        lock (gate)
        {
            var dropped = holders.Count;
            foreach (var scope in holders)
                scope.Detach();
            holders.Clear();
            TryRestoreAndClear($"force release: {reason}");
            warning($"[{name}] SuppressionForceReleased: dropped {dropped} holder(s) and restored. reason={reason}");
            return dropped;
        }
    }

    private void Release(Scope scope)
    {
        lock (gate)
        {
            if (!holders.Remove(scope) || holders.Count != 0)
                return;
            TryRestoreAndClear("last holder released");
        }
    }

    private bool TryRestoreAndClear(string context)
    {
        try
        {
            restore();
        }
        catch (Exception exception)
        {
            warning($"[{name}] SuppressionRestoreFailed: restore threw ({context}); the marker stays so the next load " +
                    $"retries. {exception.Message}");
            return false;
        }

        try
        {
            if (File.Exists(markerPath))
                File.Delete(markerPath);
        }
        catch (Exception exception)
        {
            warning($"[{name}] SuppressionMarkerDeleteFailed: restored, but could not delete {markerPath} ({context}); " +
                    $"the next load restores again harmlessly. {exception.Message}");
        }

        diagnostic($"[{name}] Restored ({context}).");
        return true;
    }

    private void WriteMarker()
    {
        try
        {
            AtomicJsonFile.Write(markerPath, new MarkerContent(name, DateTimeOffset.UtcNow, Environment.ProcessId));
        }
        catch (Exception exception)
        {
            // Suppress anyway: refusing would stop the feature outright. Only crash recovery is lost.
            warning($"[{name}] SuppressionMarkerWriteFailed: could not write {markerPath}; suppressing without crash " +
                    $"recovery for this hold. {exception.Message}");
        }
    }

    private string DescribeMarker()
    {
        try
        {
            return AtomicJsonFile.Read<MarkerContent>(markerPath) is { } content
                ? $"written {content.SuppressedAtUtc:O} by process {content.ProcessId}"
                : "empty marker";
        }
        catch (Exception exception)
        {
            return $"unreadable marker: {exception.Message}";
        }
    }

    private sealed record MarkerContent(string Name, DateTimeOffset SuppressedAtUtc, int ProcessId);

    public sealed class Scope : IDisposable
    {
        private PersistedSuppressionMarker? owner;

        internal Scope(PersistedSuppressionMarker owner) => this.owner = owner;

        internal void Detach() => Interlocked.Exchange(ref owner, null);

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(this);
    }
}
