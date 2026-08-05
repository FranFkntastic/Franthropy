using System.Threading.Channels;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Diagnostics;
using Franthropy.Observations.Hosting;
using Franthropy.Observations.Storage;
using Franthropy.Observations.V1;

namespace Franthropy.Dalamud.Observations;

public sealed record DalamudSharedObservationHostOptions
{
    public required string PluginConfigDirectory { get; init; }
    public required string PluginName { get; init; }
    public required string PluginInstanceId { get; init; }
    public required string GameBuild { get; init; }
    public required IGameInventory GameInventory { get; init; }
    public required IPlayerState PlayerState { get; init; }
    public required IAddonLifecycle AddonLifecycle { get; init; }
    public Action<string, Exception?>? Diagnostic { get; init; }
    public int WriterCapability { get; init; } = 2;
}

public sealed class DalamudSharedObservationHost : IDisposable
{
    public const string ApprovedGameBuild = "2026.07.16.0001.0000";
    private readonly DalamudSharedObservationHostOptions options;
    private readonly SharedObservationPaths paths;
    private readonly ObservationStoreOptions storeOptions;
    private readonly ObservationCollectorCoordinator coordinator;
    private DalamudCollector? collector;
    private bool started;
    private bool disposed;

    public DalamudSharedObservationHost(DalamudSharedObservationHostOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PluginName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PluginInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.GameBuild);
        if (string.Equals(options.GameBuild, "unknown", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The exact game build is unavailable; shared observation hosting is blocked.");
        GamePatchCompatibilityGate.Require("Franthropy.SharedObservations.V1", ApprovedGameBuild, options.GameBuild);
        paths = SharedObservationPaths.FromPluginConfigDirectory(options.PluginConfigDirectory);
        storeOptions = new ObservationStoreOptions
        {
            DatabasePath = paths.DatabasePath,
            BackupDirectory = paths.BackupsDirectory,
            MigrationLockPath = paths.MigrationLockPath,
            ChangeSignalPath = paths.ChangeSignalPath,
            WriterCapability = options.WriterCapability,
        };
        var version = typeof(DalamudSharedObservationHost).Assembly.GetName().Version ?? new Version(0, 0);
        coordinator = new ObservationCollectorCoordinator(new ObservationCollectorCoordinatorOptions
        {
            ProfileId = paths.ProfileId,
            CandidatesDirectory = paths.CandidatesDirectory,
            PluginName = options.PluginName,
            PluginInstanceId = options.PluginInstanceId,
            FranthropyVersion = version,
            WriterCapability = options.WriterCapability,
            DatabaseProbe = () => ObservationDatabaseProbe.ReadAsync(storeOptions).AsTask().GetAwaiter().GetResult(),
            StartCollector = () => StartCollector(version),
            StopCollector = StopCollector,
        });
        coordinator.LeadershipChanged += OnLeadershipChanged;
    }

    public event EventHandler<ObservationLeadershipSnapshot>? LeadershipChanged;
    public ObservationLeadershipSnapshot Leadership => coordinator.State;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
            return;
        coordinator.Start();
        started = true;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        coordinator.LeadershipChanged -= OnLeadershipChanged;
        coordinator.Dispose();
    }

    private void StartCollector(Version version)
    {
        var open = SqliteObservationStore.OpenAsync(storeOptions).AsTask().GetAwaiter().GetResult();
        if (!open.IsReady)
            throw new InvalidOperationException(open.Message);
        var provenance = new ObservationProvenance(
            options.PluginName,
            options.PluginInstanceId,
            version.ToString(),
            options.GameBuild);
        var next = new DalamudCollector(
            options.GameInventory,
            options.PlayerState,
            options.AddonLifecycle,
            open.Store!,
            provenance,
            ReportFault,
            options.Diagnostic);
        try
        {
            next.Start();
            collector = next;
        }
        catch
        {
            next.Dispose();
            throw;
        }
    }

    private void StopCollector()
    {
        var current = collector;
        collector = null;
        current?.Dispose();
    }

    private void ReportFault(string message)
    {
        options.Diagnostic?.Invoke(message, null);
        coordinator.ReportCollectorFault(message);
    }

    private void OnLeadershipChanged(object? sender, ObservationLeadershipSnapshot snapshot)
    {
        if (snapshot.State is ObservationLeadershipState.Faulted or ObservationLeadershipState.Incompatible)
            options.Diagnostic?.Invoke(snapshot.Message, null);
        var subscribers = LeadershipChanged;
        if (subscribers is null)
            return;
        foreach (var subscriber in subscribers.GetInvocationList().Cast<EventHandler<ObservationLeadershipSnapshot>>())
        {
            try { subscriber(this, snapshot); }
            catch (Exception ex) { options.Diagnostic?.Invoke("A shared-observation leadership subscriber failed.", ex); }
        }
    }

    private sealed class DalamudCollector : IDisposable
    {
        private static readonly GameInventoryType[] PlayerContainers =
        [
            GameInventoryType.Inventory1,
            GameInventoryType.Inventory2,
            GameInventoryType.Inventory3,
            GameInventoryType.Inventory4,
            GameInventoryType.EquippedItems,
            GameInventoryType.Crystals,
            GameInventoryType.ArmoryOffHand,
            GameInventoryType.ArmoryHead,
            GameInventoryType.ArmoryBody,
            GameInventoryType.ArmoryHands,
            GameInventoryType.ArmoryLegs,
            GameInventoryType.ArmoryFeets,
            GameInventoryType.ArmoryEar,
            GameInventoryType.ArmoryNeck,
            GameInventoryType.ArmoryWrist,
            GameInventoryType.ArmoryRings,
            GameInventoryType.ArmorySoulCrystal,
            GameInventoryType.ArmoryMainHand,
        ];
        private static readonly GameInventoryType[] SaddlebagContainers =
        [
            GameInventoryType.SaddleBag1,
            GameInventoryType.SaddleBag2,
            GameInventoryType.PremiumSaddleBag1,
            GameInventoryType.PremiumSaddleBag2,
        ];
        private static readonly GameInventoryType[] RequiredSaddlebagContainers =
        [
            GameInventoryType.SaddleBag1,
            GameInventoryType.SaddleBag2,
        ];
        private static readonly GameInventoryType[] OptionalSaddlebagContainers =
        [
            GameInventoryType.PremiumSaddleBag1,
            GameInventoryType.PremiumSaddleBag2,
        ];
        private static readonly GameInventoryType[] RetainerContainers =
        [
            GameInventoryType.RetainerPage1,
            GameInventoryType.RetainerPage2,
            GameInventoryType.RetainerPage3,
            GameInventoryType.RetainerPage4,
            GameInventoryType.RetainerPage5,
            GameInventoryType.RetainerPage6,
            GameInventoryType.RetainerPage7,
            GameInventoryType.RetainerCrystals,
        ];

        private readonly IGameInventory gameInventory;
        private readonly IPlayerState playerState;
        private readonly IAddonLifecycle addonLifecycle;
        private readonly SqliteObservationStore store;
        private readonly ObservationProvenance provenance;
        private readonly Action<string> fault;
        private readonly Action<string, Exception?>? diagnostic;
        private readonly Channel<PendingObservation> queue = Channel.CreateBounded<PendingObservation>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        private Task worker = Task.CompletedTask;
        private long sourceRevision;
        private bool started;
        private bool disposed;

        public DalamudCollector(
            IGameInventory gameInventory,
            IPlayerState playerState,
            IAddonLifecycle addonLifecycle,
            SqliteObservationStore store,
            ObservationProvenance provenance,
            Action<string> fault,
            Action<string, Exception?>? diagnostic)
        {
            this.gameInventory = gameInventory;
            this.playerState = playerState;
            this.addonLifecycle = addonLifecycle;
            this.store = store;
            this.provenance = provenance;
            this.fault = fault;
            this.diagnostic = diagnostic;
        }

        public void Start()
        {
            if (started)
                return;
            started = true;
            try
            {
                worker = Task.Run(ProcessAsync);
                gameInventory.InventoryChanged += OnInventoryChanged;
                addonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerList", OnRetainerListOpened);
                addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "InventoryRetainerLarge", OnRetainerInventoryClosing);
                addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "InventoryRetainer", OnRetainerInventoryClosing);
                CaptureInitialCharacterState();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            if (started)
            {
                gameInventory.InventoryChanged -= OnInventoryChanged;
                addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerList", OnRetainerListOpened);
                addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "InventoryRetainerLarge", OnRetainerInventoryClosing);
                addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "InventoryRetainer", OnRetainerInventoryClosing);
            }
            queue.Writer.TryComplete();
            worker.GetAwaiter().GetResult();
            store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
        {
            try
            {
                var flattened = events.SelectMany(FlattenEvent).ToArray();
                foreach (var delta in CaptureInventoryDeltas(flattened))
                    Enqueue(delta);
                if (flattened.Any(change => change.Item.ContainerType == GameInventoryType.RetainerMarket))
                    Enqueue(CaptureRetainerListings());
            }
            catch (Exception ex)
            {
                diagnostic?.Invoke("A shared inventory observation event could not be captured.", ex);
            }
        }

        private IEnumerable<InventoryObservationDelta> CaptureInventoryDeltas(IReadOnlyList<InventoryEventArgs> events)
        {
            var player = CoalesceUpdates(events, PlayerContainers);
            if (player.Count > 0)
            {
                var owner = CurrentOwner();
                yield return Delta(
                    new ObservationScope(owner, ObservationSubject.Character(owner), ObservationContainerKind.PlayerInventory),
                    player);
            }

            var saddlebag = CoalesceUpdates(events, SaddlebagContainers);
            if (saddlebag.Count > 0)
            {
                var owner = CurrentOwner();
                yield return Delta(
                    new ObservationScope(owner, ObservationSubject.Character(owner), ObservationContainerKind.Saddlebag),
                    saddlebag);
            }

            var retainer = CoalesceUpdates(events, RetainerContainers);
            if (retainer.Count > 0)
            {
                var (owner, retainerId) = CurrentRetainer();
                yield return Delta(
                    new ObservationScope(owner, ObservationSubject.Retainer(retainerId, owner), ObservationContainerKind.RetainerInventory),
                    retainer);
            }
        }

        private static IReadOnlyList<InventorySlotUpdate> CoalesceUpdates(
            IReadOnlyList<InventoryEventArgs> events,
            IReadOnlyCollection<GameInventoryType> containers)
        {
            var accepted = containers.ToHashSet();
            var updates = new Dictionary<(int ContainerId, int SlotIndex), InventorySlotUpdate>();
            foreach (var change in events)
            {
                var item = change.Item;
                if (!accepted.Contains(item.ContainerType))
                    continue;
                var key = ((int)item.ContainerType, checked((int)item.InventorySlot));
                var current = change.Type == GameInventoryEvent.Removed || item.IsEmpty || item.ItemId == 0 || item.Quantity <= 0
                    ? null
                    : new InventorySlotValue(item.BaseItemId, item.Quantity, item.IsHq);
                updates[key] = new InventorySlotUpdate(key.Item1, key.Item2, current);
            }
            return updates.Values.OrderBy(update => update.ContainerId).ThenBy(update => update.SlotIndex).ToArray();
        }

        private static IEnumerable<InventoryEventArgs> FlattenEvent(InventoryEventArgs change)
        {
            if (change is InventoryComplexEventArgs complex)
            {
                foreach (var source in FlattenEvent(complex.SourceEvent))
                    yield return source;
                foreach (var target in FlattenEvent(complex.TargetEvent))
                    yield return target;
                yield break;
            }
            yield return change;
        }

        private void CaptureInitialCharacterState()
        {
            try
            {
                Enqueue(CaptureCharacterInventory(PlayerContainers, ObservationContainerKind.PlayerInventory, ObservationPayloadContracts.PlayerInventory));
                Enqueue(CaptureCharacterInventory(
                    RequiredSaddlebagContainers,
                    ObservationContainerKind.Saddlebag,
                    ObservationPayloadContracts.Saddlebag,
                    OptionalSaddlebagContainers));
            }
            catch (Exception ex)
            {
                diagnostic?.Invoke("Initial shared character observations are unavailable until the next inventory event.", ex);
            }
        }

        private void OnRetainerListOpened(AddonEvent eventType, AddonArgs args)
        {
            try { Enqueue(CaptureRetainerRoster()); }
            catch (Exception ex) { diagnostic?.Invoke("The shared retainer roster could not be captured.", ex); }
        }

        private void OnRetainerInventoryClosing(AddonEvent eventType, AddonArgs args)
        {
            try
            {
                Enqueue(CaptureRetainerInventory());
                Enqueue(CaptureRetainerListings());
            }
            catch (Exception ex)
            {
                diagnostic?.Invoke("The closing retainer observation could not be captured.", ex);
            }
        }

        private ObservationEnvelope CaptureCharacterInventory(
            IReadOnlyList<GameInventoryType> requested,
            ObservationContainerKind kind,
            string payloadContract,
            IReadOnlyList<GameInventoryType>? optional = null)
        {
            var owner = CurrentOwner();
            var observed = new List<int>();
            var rows = new List<InventoryItemObservation>();
            foreach (var type in requested.Concat(optional ?? []).Distinct())
            {
                var items = gameInventory.GetInventoryItems(type);
                if (items.Length == 0)
                    continue;
                observed.Add((int)type);
                foreach (ref readonly var item in items)
                {
                    if (item.IsEmpty || item.ItemId == 0 || item.Quantity <= 0)
                        continue;
                    rows.Add(new InventoryItemObservation((int)type, checked((int)item.InventorySlot), item.BaseItemId, item.Quantity, item.IsHq));
                }
            }
            var requiredIds = requested.Select(type => (int)type).ToHashSet();
            var complete = requiredIds.All(observed.Contains);
            var requestedIds = requiredIds
                .Concat((optional ?? []).Select(type => (int)type).Where(observed.Contains))
                .Order()
                .ToArray();
            return Envelope(
                new ObservationScope(owner, ObservationSubject.Character(owner), kind),
                Evidence(complete),
                ObservationPayload.Create(
                    payloadContract,
                    ObservationPayloadContracts.Version,
                    new InventoryObservationPayload(requestedIds, observed, rows)));
        }

        private unsafe ObservationEnvelope CaptureRetainerRoster()
        {
            var owner = CurrentOwner();
            var manager = RetainerManager.Instance();
            var rows = new List<RetainerRosterObservation>();
            var complete = manager is not null && manager->IsReady;
            if (complete)
            {
                var count = manager->GetRetainerCount();
                for (uint index = 0; index < count; index++)
                {
                    var retainer = manager->GetRetainerBySortedIndex(index);
                    if (retainer is null || retainer->RetainerId == 0)
                    {
                        complete = false;
                        continue;
                    }
                    rows.Add(new RetainerRosterObservation(retainer->RetainerId, retainer->NameString, owner.HomeWorldId));
                }
            }
            return Envelope(
                new ObservationScope(owner, ObservationSubject.Character(owner), ObservationContainerKind.RetainerRoster),
                Evidence(complete),
                ObservationPayload.Create(ObservationPayloadContracts.RetainerRoster, ObservationPayloadContracts.Version, new RetainerRosterPayload(rows)));
        }

        private ObservationEnvelope CaptureRetainerInventory()
        {
            var (owner, retainerId) = CurrentRetainer();
            var observed = new List<int>();
            var rows = new List<InventoryItemObservation>();
            foreach (var type in RetainerContainers)
            {
                var items = gameInventory.GetInventoryItems(type);
                if (items.Length == 0)
                    continue;
                observed.Add((int)type);
                foreach (ref readonly var item in items)
                {
                    if (item.IsEmpty || item.ItemId == 0 || item.Quantity <= 0)
                        continue;
                    rows.Add(new InventoryItemObservation((int)type, checked((int)item.InventorySlot), item.BaseItemId, item.Quantity, item.IsHq));
                }
            }
            var complete = observed.Count == RetainerContainers.Length;
            return Envelope(
                new ObservationScope(owner, ObservationSubject.Retainer(retainerId, owner), ObservationContainerKind.RetainerInventory),
                Evidence(complete),
                ObservationPayload.Create(
                    ObservationPayloadContracts.RetainerInventory,
                    ObservationPayloadContracts.Version,
                    new InventoryObservationPayload(RetainerContainers.Select(type => (int)type).ToArray(), observed, rows)));
        }

        private unsafe ObservationEnvelope CaptureRetainerListings()
        {
            var (owner, retainerId) = CurrentRetainer();
            var items = gameInventory.GetInventoryItems(GameInventoryType.RetainerMarket);
            var manager = InventoryManager.Instance();
            var complete = items.Length > 0 && manager is not null;
            var rows = new List<RetainerMarketListingObservation>();
            if (complete)
            {
                foreach (ref readonly var item in items)
                {
                    if (item.IsEmpty || item.ItemId == 0 || item.Quantity <= 0)
                        continue;
                    var slot = checked((int)item.InventorySlot);
                    var price = manager->GetRetainerMarketPrice(checked((short)slot));
                    if (price is <= 0 or > int.MaxValue)
                    {
                        complete = false;
                        continue;
                    }
                    rows.Add(new RetainerMarketListingObservation(slot, item.BaseItemId, item.Quantity, checked((int)price), item.IsHq));
                }
            }
            return Envelope(
                new ObservationScope(owner, ObservationSubject.Retainer(retainerId, owner), ObservationContainerKind.RetainerMarketListings),
                Evidence(complete),
                ObservationPayload.Create(
                    ObservationPayloadContracts.RetainerMarketListings,
                    ObservationPayloadContracts.Version,
                    new RetainerMarketListingsPayload(rows)));
        }

        private ObservationEnvelope Envelope(ObservationScope scope, ObservationEvidence evidence, ObservationPayload payload) =>
            new(
                scope,
                new ObservationCapture(
                    Interlocked.Increment(ref sourceRevision),
                    DateTimeOffset.UtcNow,
                    provenance,
                    evidence),
                payload);

        private InventoryObservationDelta Delta(ObservationScope scope, IReadOnlyList<InventorySlotUpdate> updates) =>
            new(
                scope,
                new ObservationCapture(
                    Interlocked.Increment(ref sourceRevision),
                    DateTimeOffset.UtcNow,
                    provenance,
                    ObservationEvidence.CompleteAvailable),
                updates);

        private static ObservationEvidence Evidence(bool complete) => ObservationEvidence.CompleteAvailable with
        {
            Availability = complete ? ObservationAvailability.Available : ObservationAvailability.Unavailable,
            Completeness = complete ? ObservationCompleteness.Complete : ObservationCompleteness.Partial,
            ContainerLoaded = complete,
            ObservationWindowCoherent = complete,
        };

        private ObservationOwner CurrentOwner()
        {
            if (playerState.ContentId == 0 || !playerState.HomeWorld.IsValid)
                throw new InvalidOperationException("The current character identity is not stable.");
            return new ObservationOwner(playerState.ContentId, playerState.HomeWorld.Value.RowId);
        }

        private unsafe (ObservationOwner Owner, ulong RetainerId) CurrentRetainer()
        {
            var owner = CurrentOwner();
            var manager = RetainerManager.Instance();
            var retainer = manager is null ? null : manager->GetActiveRetainer();
            if (retainer is null || retainer->RetainerId == 0)
                throw new InvalidOperationException("No stable active retainer identity is available.");
            return (owner, retainer->RetainerId);
        }

        private void Enqueue(ObservationEnvelope observation)
        {
            if (!queue.Writer.TryWrite(new PendingObservation(observation, null)))
                fault("The bounded shared-observation queue is full; collection stopped before evidence could be dropped silently.");
        }

        private void Enqueue(InventoryObservationDelta observation)
        {
            if (!queue.Writer.TryWrite(new PendingObservation(null, observation)))
                fault("The bounded shared-observation queue is full; collection stopped before evidence could be dropped silently.");
        }

        private async Task ProcessAsync()
        {
            try
            {
                await foreach (var observation in queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    var result = observation.Snapshot is not null
                        ? await store.WriteAsync(observation.Snapshot).ConfigureAwait(false)
                        : await store.WriteInventoryDeltaAsync(observation.Delta!).ConfigureAwait(false);
                    if (result.Status is ObservationWriteStatus.Busy or ObservationWriteStatus.Unavailable or ObservationWriteStatus.UnsupportedDatabaseVersion)
                    {
                        fault($"Shared observation writer failed: {result.Message}");
                        return;
                    }
                    if (result.Status == ObservationWriteStatus.Rejected)
                        diagnostic?.Invoke($"Shared observation evidence was rejected: {result.Message}", null);
                }
            }
            catch (Exception ex)
            {
                fault($"The shared observation writer stopped unexpectedly: {ex.Message}");
            }
        }

        private sealed record PendingObservation(
            ObservationEnvelope? Snapshot,
            InventoryObservationDelta? Delta);
    }
}
