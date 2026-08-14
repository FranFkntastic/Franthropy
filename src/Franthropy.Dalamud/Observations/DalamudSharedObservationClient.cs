using System.Collections.Immutable;
using System.Threading.Channels;
using Franthropy.Observations.Storage;
using Franthropy.Observations.V1;

namespace Franthropy.Dalamud.Observations;

public sealed record SharedObservationDelivery(
    ObservationOwner Owner,
    long Revision,
    IReadOnlyList<TrustedObservation> PlayerBaselines,
    IReadOnlyList<InventoryChangeBatch> PlayerChanges,
    IReadOnlyList<TrustedObservation> RetainerObservations,
    IReadOnlyList<ObservationScope> InvalidatedScopes);

public sealed record SharedRetainerObservationSnapshot(
    ObservationOwner Owner,
    long Revision,
    ImmutableArray<TrustedObservation> Observations);

public sealed record DalamudSharedObservationClientOptions
{
    public required string PluginConfigDirectory { get; init; }
    public required Func<ObservationOwner?> CurrentOwner { get; init; }
    public Func<SharedObservationDelivery, CancellationToken, ValueTask>? Deliver { get; init; }
    public Action<string, Exception?>? Diagnostic { get; init; }
}

/// <summary>
/// Reads the profile-scoped Franthropy observation authority independently from local
/// collector eligibility. Consumers can therefore remain functional when their embedded
/// collector is patch-blocked, absent, or outranked by another plugin.
/// </summary>
public sealed class DalamudSharedObservationClient : IAsyncDisposable
{
    private static readonly TimeSpan MinimumRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(5);
    private readonly DalamudSharedObservationClientOptions options;
    private readonly ObservationStoreOptions storeOptions;
    private readonly ObservationDatabaseChangeMonitor monitor;
    private readonly Channel<bool> signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite,
    });
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, TrustedObservation> retainerObservations = new(StringComparer.Ordinal);
    private SqliteObservationReader? reader;
    private Task worker = Task.CompletedTask;
    private Task poller = Task.CompletedTask;
    private ObservationOwner? owner;
    private SharedRetainerObservationSnapshot? currentRetainers;
    private long revision;
    private long retainerRevision;
    private TimeSpan retryDelay = MinimumRetryDelay;
    private int resetRequested;
    private bool started;
    private bool disposed;

    public DalamudSharedObservationClient(DalamudSharedObservationClientOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PluginConfigDirectory);
        ArgumentNullException.ThrowIfNull(options.CurrentOwner);
        var paths = SharedObservationPaths.FromPluginConfigDirectory(options.PluginConfigDirectory);
        storeOptions = new ObservationStoreOptions
        {
            DatabasePath = paths.DatabasePath,
            BackupDirectory = paths.BackupsDirectory,
            MigrationLockPath = paths.MigrationLockPath,
            ChangeSignalPath = paths.ChangeSignalPath,
            WriterCapability = 2,
        };
        monitor = new ObservationDatabaseChangeMonitor(storeOptions);
        monitor.Changed += OnDatabaseChanged;
    }

    public event EventHandler<SharedRetainerObservationSnapshot>? RetainersChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
            return;
        started = true;
        worker = Task.Run(ProcessAsync);
        poller = Task.Run(PollAsync);
        monitor.StartAsync(lifetime.Token).AsTask().GetAwaiter().GetResult();
        Refresh();
    }

    public void Refresh()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        signals.Writer.TryWrite(true);
    }

    public bool TryGetRetainers(ObservationOwner expectedOwner, out SharedRetainerObservationSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        snapshot = Volatile.Read(ref currentRetainers);
        if (snapshot?.Owner != expectedOwner)
        {
            if (!disposed)
                signals.Writer.TryWrite(true);
            snapshot = null;
            return false;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        monitor.Changed -= OnDatabaseChanged;
        lifetime.Cancel();
        signals.Writer.TryComplete();
        try { await worker.ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        try { await poller.ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        await monitor.DisposeAsync().ConfigureAwait(false);
        if (reader is not null)
            await reader.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }

    private void OnDatabaseChanged(object? sender, ObservationDatabaseChanged change) => signals.Writer.TryWrite(true);

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
        {
            var checkpoint = Math.Max(Volatile.Read(ref revision), Volatile.Read(ref retainerRevision));
            if (checkpoint > 0)
            {
                var probe = await ObservationDatabaseProbe.ReadAsync(storeOptions, lifetime.Token).ConfigureAwait(false);
                if (probe.Status is ObservationDatabaseProbeStatus.Compatible or ObservationDatabaseProbeStatus.UpgradeRequired &&
                    probe.CurrentRevision < checkpoint)
                    Interlocked.Exchange(ref resetRequested, 1);
            }
            signals.Writer.TryWrite(true);
        }
    }

    private async Task ProcessAsync()
    {
        await foreach (var signal in signals.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
        {
            _ = signal;
            while (signals.Reader.TryRead(out var ignored)) { _ = ignored; }
            try
            {
                if (Interlocked.Exchange(ref resetRequested, 0) != 0)
                    await ResetReaderAsync().ConfigureAwait(false);
                if (await ConsumeAsync(lifetime.Token).ConfigureAwait(false))
                {
                    retryDelay = MinimumRetryDelay;
                    continue;
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                options.Diagnostic?.Invoke("The shared observation client could not consume current evidence.", exception);
            }

            try
            {
                await Task.Delay(retryDelay, lifetime.Token).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(
                    MaximumRetryDelay.TotalMilliseconds,
                    retryDelay.TotalMilliseconds * 2));
                Refresh();
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<bool> ConsumeAsync(CancellationToken cancellationToken)
    {
        if (reader is null)
        {
            var open = await SqliteObservationReader.OpenAsync(storeOptions, cancellationToken).ConfigureAwait(false);
            if (!open.IsReady)
                return open.Status == ObservationStoreOpenStatus.Missing;
            reader = open.Reader!;
        }

        var nextOwner = options.CurrentOwner();
        if (nextOwner is null || nextOwner.LocalContentId == 0 || nextOwner.HomeWorldId == 0)
            return true;
        if (owner != nextOwner)
        {
            owner = nextOwner;
            revision = 0;
            retainerRevision = 0;
            retainerObservations.Clear();
            Volatile.Write(ref currentRetainers, null);
        }

        var changes = await reader.ReadInventoryChangesAsync(nextOwner, revision, cancellationToken).ConfigureAwait(false);
        if (changes.CurrentRevision < revision)
        {
            await ResetReaderAsync().ConfigureAwait(false);
            return false;
        }
        IReadOnlyList<TrustedObservation> playerBaselines = [];
        IReadOnlyList<InventoryChangeBatch> playerChanges = [];
        var invalidatedScopes = new List<ObservationScope>();
        switch (changes.Status)
        {
            case InventoryChangeReadStatus.SnapshotRequired:
                break;
            case InventoryChangeReadStatus.Found:
                playerChanges = changes.Batches;
                break;
            case InventoryChangeReadStatus.NoChanges:
            case InventoryChangeReadStatus.NotObserved:
                break;
            case InventoryChangeReadStatus.Busy:
            case InventoryChangeReadStatus.Unavailable:
                return false;
            default:
                throw new ArgumentOutOfRangeException(nameof(changes.Status), changes.Status, null);
        }

        var player = await reader.ReadCurrentByOwnerAsync(nextOwner, ObservationContainerKind.PlayerInventory, cancellationToken).ConfigureAwait(false);
        var saddlebag = await reader.ReadCurrentByOwnerAsync(nextOwner, ObservationContainerKind.Saddlebag, cancellationToken).ConfigureAwait(false);
        if (player.Status is ObservationReadStatus.UnsupportedDatabaseVersion or ObservationReadStatus.Busy or ObservationReadStatus.Unavailable ||
            saddlebag.Status is ObservationReadStatus.UnsupportedDatabaseVersion or ObservationReadStatus.Busy or ObservationReadStatus.Unavailable)
            return false;
        var currentPlayer = player.Observations.Concat(saddlebag.Observations).ToArray();
        invalidatedScopes.AddRange(currentPlayer.Where(observation => observation.IsStale).Select(observation => observation.Scope));
        if (changes.Status == InventoryChangeReadStatus.SnapshotRequired)
            playerBaselines = currentPlayer.Where(observation => !observation.IsStale).ToArray();

        var retainerRead = await reader.ReadCurrentRetainerChangesAsync(nextOwner, retainerRevision, cancellationToken).ConfigureAwait(false);
        if (retainerRead.Status is ObservationReadStatus.UnsupportedDatabaseVersion or ObservationReadStatus.Busy or ObservationReadStatus.Unavailable)
            return false;
        if (retainerRead.CurrentRevision < retainerRevision)
        {
            await ResetReaderAsync().ConfigureAwait(false);
            return false;
        }

        foreach (var observation in retainerRead.Observations)
        {
            var key = CreateScopeKey(observation.Scope);
            if (observation.IsStale)
            {
                retainerObservations.Remove(key);
                invalidatedScopes.Add(observation.Scope);
            }
            else
                retainerObservations[key] = observation;
        }

        var nextRetainers = new SharedRetainerObservationSnapshot(
            nextOwner,
            retainerRead.CurrentRevision,
            retainerObservations.Values.OrderBy(observation => observation.Revision).ToImmutableArray());
        var previousRetainers = Volatile.Read(ref currentRetainers);
        Volatile.Write(ref currentRetainers, nextRetainers);
        if (previousRetainers is null ||
            previousRetainers.Owner != nextRetainers.Owner ||
            retainerRead.Observations.Count > 0)
            PublishRetainersChanged(nextRetainers);

        if (playerBaselines.Count > 0 || playerChanges.Count > 0 || retainerRead.Observations.Count > 0 || invalidatedScopes.Count > 0)
        {
            var delivery = new SharedObservationDelivery(
                nextOwner,
                Math.Max(changes.CurrentRevision, retainerRead.CurrentRevision),
                playerBaselines,
                playerChanges,
                retainerRead.Observations.Where(observation => !observation.IsStale).ToArray(),
                invalidatedScopes.Distinct().ToArray());
            if (options.Deliver is not null)
                await options.Deliver(delivery, cancellationToken).ConfigureAwait(false);
        }

        revision = changes.CurrentRevision;
        retainerRevision = retainerRead.CurrentRevision;
        return true;
    }

    private async ValueTask ResetReaderAsync()
    {
        if (reader is not null)
            await reader.DisposeAsync().ConfigureAwait(false);
        reader = null;
        revision = 0;
        retainerRevision = 0;
        retainerObservations.Clear();
        Volatile.Write(ref currentRetainers, null);
    }

    private void PublishRetainersChanged(SharedRetainerObservationSnapshot snapshot)
    {
        var subscribers = RetainersChanged;
        if (subscribers is null)
            return;
        foreach (var subscriber in subscribers.GetInvocationList().Cast<EventHandler<SharedRetainerObservationSnapshot>>())
        {
            try { subscriber(this, snapshot); }
            catch (Exception exception) { options.Diagnostic?.Invoke("A shared retainer observation subscriber failed.", exception); }
        }
    }

    private static string CreateScopeKey(ObservationScope scope) =>
        $"{scope.Owner.LocalContentId:X16}:{scope.Owner.HomeWorldId}:{(int)scope.Subject.Kind}:{scope.Subject.Id:X16}:{(int)scope.Container}";
}
