using System.Globalization;

namespace Franthropy.Observations.Storage;

public sealed record ObservationDatabaseChanged(long Revision);

public sealed class ObservationDatabaseChangeMonitor : IAsyncDisposable
{
    private readonly ObservationStoreOptions options;
    private readonly string signalPath;
    private readonly object gate = new();
    private FileSystemWatcher? watcher;
    private long lastRevision;
    private bool disposed;

    public ObservationDatabaseChangeMonitor(ObservationStoreOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        var databasePath = Path.GetFullPath(options.DatabasePath);
        signalPath = Path.GetFullPath(options.ChangeSignalPath ??
            Path.Combine(Path.GetDirectoryName(databasePath)!, "changes.signal"));
    }

    public event EventHandler<ObservationDatabaseChanged>? Changed;
    public string? LastNotificationError { get; private set; }
    public long LastRevision => Interlocked.Read(ref lastRevision);

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gate)
        {
            if (watcher is not null)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(signalPath)!);
            watcher = new FileSystemWatcher(Path.GetDirectoryName(signalPath)!, Path.GetFileName(signalPath))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            watcher.Created += OnSignal;
            watcher.Changed += OnSignal;
            watcher.Renamed += OnSignalRenamed;
            watcher.Error += OnWatcherError;
        }

        var probe = await ObservationDatabaseProbe.ReadAsync(options, cancellationToken).ConfigureAwait(false);
        if (probe.Status is ObservationDatabaseProbeStatus.Compatible or ObservationDatabaseProbeStatus.UpgradeRequired)
            Interlocked.Exchange(ref lastRevision, probe.CurrentRevision);
        ReadAndPublishSignal();
    }

    public ValueTask DisposeAsync()
    {
        if (disposed)
            return ValueTask.CompletedTask;
        disposed = true;
        lock (gate)
        {
            watcher?.Dispose();
            watcher = null;
        }
        return ValueTask.CompletedTask;
    }

    private void OnSignal(object sender, FileSystemEventArgs args) => ReadAndPublishSignal();
    private void OnSignalRenamed(object sender, RenamedEventArgs args) => ReadAndPublishSignal();

    private void ReadAndPublishSignal()
    {
        try
        {
            if (!File.Exists(signalPath))
                return;
            var text = File.ReadAllText(signalPath).Trim();
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
                return;
            var previous = Interlocked.Read(ref lastRevision);
            while (revision > previous)
            {
                var observed = Interlocked.CompareExchange(ref lastRevision, revision, previous);
                if (observed == previous)
                {
                    Publish(new ObservationDatabaseChanged(revision));
                    return;
                }
                previous = observed;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastNotificationError = ex.Message;
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs args) => LastNotificationError = args.GetException().Message;

    private void Publish(ObservationDatabaseChanged change)
    {
        var subscribers = Changed;
        if (subscribers is null)
            return;
        foreach (var subscriber in subscribers.GetInvocationList().Cast<EventHandler<ObservationDatabaseChanged>>())
        {
            try { subscriber(this, change); }
            catch (Exception ex) { LastNotificationError = ex.Message; }
        }
    }
}
