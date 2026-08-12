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
/// </summary>
public sealed class DalamudExternalUiAutomationSuppression : IDisposable
{
    private const string TextAdvanceStopRequests = "TextAdvance.StopRequests";
    private const string YesAlreadyStopRequests = "YesAlready.StopRequests";
    private readonly ISharedPluginDataStore dataStore;
    private readonly Action<string> diagnostic;
    private readonly string owner;
    private readonly object gate = new();
    private int holders;
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
            owner)
    {
    }

    internal DalamudExternalUiAutomationSuppression(
        ISharedPluginDataStore dataStore,
        Action<string> diagnostic,
        string owner)
    {
        this.dataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
        this.diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        this.owner = string.IsNullOrWhiteSpace(owner)
            ? throw new ArgumentException("A stop-request owner is required.", nameof(owner))
            : owner.Trim();
    }

    public Scope Acquire()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (holders == 0)
            {
                textAdvanceOwned = AddOwner(TextAdvanceStopRequests, "TextAdvance") || textAdvanceOwned;
                yesAlreadyOwned = AddOwner(YesAlreadyStopRequests, "YesAlready") || yesAlreadyOwned;
            }
            holders++;
            return new Scope(this);
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
            if (holders <= 0)
                return;
            holders--;
            if (holders != 0)
                return;
            RestoreOwned(TextAdvanceStopRequests, "TextAdvance", ref textAdvanceOwned, scope);
            RestoreOwned(YesAlreadyStopRequests, "YesAlready", ref yesAlreadyOwned, scope);
        }
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
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            holders = 0;
            RestoreOwned(TextAdvanceStopRequests, "TextAdvance", ref textAdvanceOwned, null);
            RestoreOwned(YesAlreadyStopRequests, "YesAlready", ref yesAlreadyOwned, null);
        }
    }

    public sealed class Scope : IDisposable
    {
        private DalamudExternalUiAutomationSuppression? owner;

        internal Scope(DalamudExternalUiAutomationSuppression owner) => this.owner = owner;

        public List<string> RestoreFailures { get; } = [];

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            current?.Release(this);
        }
    }
}
