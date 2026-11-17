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
using Franthropy.Dalamud.Observations.Reporting;

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
    /// <summary>Drives retainer-visit proof settlement. Passive retainer captures are published only for proven visits.</summary>
    public required IFramework Framework { get; init; }
    /// <summary>Installs the read-only retainer-visit proof hooks; if they cannot install, passive retainer captures fail closed.</summary>
    public required IGameInteropProvider InteropProvider { get; init; }
    public required ISigScanner SigScanner { get; init; }
    /// <summary>Receives Warning-level retainer-visit proof failures.</summary>
    public required IPluginLog PluginLog { get; init; }
    public Action<string, Exception?>? Diagnostic { get; init; }
    public ObservationWriterQueueOptions WriterQueue { get; init; } = new();
    public int WriterCapability { get; init; } = DalamudSharedObservationHost.SellingSurfaceWriterCapability;
}

public sealed class DalamudSharedObservationHost : IDisposable
{
    public const int CaptureSessionWriterCapability = 3;
    public const int SellingSurfaceWriterCapability = 4;
    private readonly DalamudSharedObservationHostOptions options;
    private readonly SharedObservationPaths paths;
    private readonly ObservationStoreOptions storeOptions;
    private readonly ObservationCollectorCoordinator coordinator;
    public ObservationCaptureSessionRegistry CaptureSessions { get; }
    private DalamudCollector? collector;
    private Reporting.BaselineStalenessWatchdog? stalenessWatchdog;
    private bool started;
    private bool disposed;
    private string? reportedCollectorFault;

    public DalamudSharedObservationHost(DalamudSharedObservationHostOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PluginName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PluginInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.GameBuild);
        ArgumentNullException.ThrowIfNull(options.GameInventory);
        ArgumentNullException.ThrowIfNull(options.PlayerState);
        ArgumentNullException.ThrowIfNull(options.AddonLifecycle);
        paths = SharedObservationPaths.FromPluginConfigDirectory(options.PluginConfigDirectory);
        CaptureSessions = new ObservationCaptureSessionRegistry(paths.CaptureSessionsPath);
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
            CaptureSessions,
            ReportFault,
            options.Diagnostic,
            options.WriterQueue,
            options.Framework,
            options.InteropProvider,
            options.SigScanner,
            options.PluginLog);
        try
        {
            next.Start();
            collector = next;
            stalenessWatchdog = new Reporting.BaselineStalenessWatchdog(
                storeOptions,
                new Reporting.BaselineStalenessWatchdogOptions
                {
                    CurrentContentId = () => options.PlayerState.ContentId,
                    CurrentHomeWorldId = () => options.PlayerState.HomeWorld.IsValid
                        ? options.PlayerState.HomeWorld.Value.RowId
                        : 0,
                },
                message => options.Diagnostic?.Invoke($"[BaselineWatchdog] {message}", null));
            stalenessWatchdog.Start();
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
        stalenessWatchdog?.Dispose();
        stalenessWatchdog = null;
        current?.Dispose();
    }

    private void ReportFault(string message)
    {
        Volatile.Write(ref reportedCollectorFault, message);
        coordinator.ReportCollectorFault(message);
    }

    private void OnLeadershipChanged(object? sender, ObservationLeadershipSnapshot snapshot)
    {
        if ((snapshot.State is ObservationLeadershipState.Faulted or ObservationLeadershipState.Incompatible) &&
            !string.Equals(snapshot.Message, Volatile.Read(ref reportedCollectorFault), StringComparison.Ordinal))
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
        private readonly ObservationCaptureSessionRegistry? captureSessions;
        private readonly Action<string, Exception?>? diagnostic;
        private readonly ObservationWriterQueue queue;
        private readonly LatestByKeyBuffer<ObservationEnvelope> pendingListings = new();
        private readonly RetainerVisitProofTracker visit = new();
        private readonly IFramework? framework;
        private readonly IGameInteropProvider? interop;
        private readonly ISigScanner? scanner;
        private readonly IPluginLog? pluginLog;
        private RetainerVisitProofHooks? visitHooks;
        private ObservationOwner? visitOwner;
        private double nextVisitCheck;
        private bool settledInventoryPending;
        private bool settledGilPending;
        private readonly HashSet<string> reportedSuppressions = [];
        private long sourceRevision;
        private bool started;
        private bool disposed;

        public DalamudCollector(
            IGameInventory gameInventory,
            IPlayerState playerState,
            IAddonLifecycle addonLifecycle,
            SqliteObservationStore store,
            ObservationProvenance provenance,
            ObservationCaptureSessionRegistry? captureSessions,
            Action<string> fault,
            Action<string, Exception?>? diagnostic,
            ObservationWriterQueueOptions writerOptions,
            IFramework? framework,
            IGameInteropProvider? interop,
            ISigScanner? scanner,
            IPluginLog? pluginLog)
        {
            this.gameInventory = gameInventory;
            this.playerState = playerState;
            this.addonLifecycle = addonLifecycle;
            this.store = store;
            this.provenance = provenance;
            this.captureSessions = captureSessions;
            this.diagnostic = diagnostic;
            this.framework = framework;
            this.interop = interop;
            this.scanner = scanner;
            this.pluginLog = pluginLog;
            queue = new ObservationWriterQueue(writerOptions, fault, diagnostic);
        }

        public void Start()
        {
            if (started)
                return;
            started = true;
            try
            {
                if (framework is not null && pluginLog is not null)
                {
                    visitHooks = new RetainerVisitProofHooks(interop, scanner, OnVisitOpened, OnVisitBatch, ReportVisit);
                    framework.Update += OnVisitUpdate;
                }
                else
                    ReportVisit("[Warning] RetainerVisitHooksUnavailable: hook=SetActiveRetainer/ApplyRetainerMarketPriceBatch, signatureMatches=unknown; framework or plugin-log service missing; all passive retainer captures refused for this collector session.", null);
                gameInventory.InventoryChanged += OnInventoryChanged;
                addonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerList", OnRetainerListOpened);
                addonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerSellList", OnRetainerSellingListChanged);
                addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "RetainerSellList", OnRetainerSellingListChanged);
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
            if (framework is not null) framework.Update -= OnVisitUpdate;
            visitHooks?.Dispose();
            if (started)
            {
                gameInventory.InventoryChanged -= OnInventoryChanged;
                addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerList", OnRetainerListOpened);
                addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerSellList", OnRetainerSellingListChanged);
                addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, "RetainerSellList", OnRetainerSellingListChanged);
                addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "InventoryRetainerLarge", OnRetainerInventoryClosing);
                addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "InventoryRetainer", OnRetainerInventoryClosing);
            }
            queue.Dispose();
            store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
        {
            try
            {
                var flattened = events.SelectMany(FlattenEvent).ToArray();
                var player = CoalesceUpdates(flattened, PlayerContainers);
                var saddlebag = CoalesceUpdates(flattened, SaddlebagContainers);
                var retainer = CoalesceUpdates(flattened, RetainerContainers);
                var hasRetainerListings = flattened.Any(change => change.Item.ContainerType == GameInventoryType.RetainerMarket);
                var owner = CurrentOwnerOrDefault();
                var retainerId = owner is null || (!hasRetainerListings && retainer.Count == 0)
                    ? null
                    : CurrentRetainerIdOrDefault();
                if ((hasRetainerListings || retainer.Count > 0) && !AllowRetainerCapture(retainerId, "inventory-change"))
                    retainerId = null;
                var plan = SharedObservationCapturePlan.Create(
                    player.Count > 0,
                    saddlebag.Count > 0,
                    retainer.Count > 0,
                    hasRetainerListings,
                    owner,
                    retainerId);

                plan.Execute(
                    capturePlayerInventory: () => Enqueue(Delta(
                        new ObservationScope(owner!, ObservationSubject.Character(owner!), ObservationContainerKind.PlayerInventory),
                        player)),
                    captureSaddlebag: () => Enqueue(Delta(
                        new ObservationScope(owner!, ObservationSubject.Character(owner!), ObservationContainerKind.Saddlebag),
                        saddlebag)),
                    captureRetainerInventory: () => Enqueue(Delta(
                        new ObservationScope(owner!, ObservationSubject.Retainer(retainerId!.Value, owner!), ObservationContainerKind.RetainerInventory),
                        retainer)),
                    captureRetainerListings: () => EnqueueLatestListing(CaptureRetainerListings(owner!, retainerId!.Value)),
                    reportFailure: ex => diagnostic?.Invoke("A shared inventory observation event could not be captured.", ex));
            }
            catch (Exception ex)
            {
                diagnostic?.Invoke("A shared inventory observation event could not be captured.", ex);
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
                Enqueue(CaptureRetainerRoster());
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

        private void OnRetainerSellingListChanged(AddonEvent eventType, AddonArgs args)
        {
            try
            {
                var owner = CurrentOwnerOrDefault();
                var retainerId = owner is null ? null : CurrentRetainerIdOrDefault();
                if (owner is not null && retainerId is not null && AllowRetainerCapture(retainerId, "selling-list"))
                    EnqueueLatestListing(CaptureRetainerListings(owner, retainerId.Value));
            }
            catch (Exception ex)
            {
                diagnostic?.Invoke("The shared retainer selling list could not be captured.", ex);
            }
        }

        private void OnRetainerInventoryClosing(AddonEvent eventType, AddonArgs args)
        {
            try
            {
                var owner = CurrentOwnerOrDefault();
                var retainerId = owner is null ? null : CurrentRetainerIdOrDefault();
                if (!AllowRetainerCapture(retainerId, "inventory-close")) return;
                var plan = SharedObservationCapturePlan.Create(false, false, true, false, owner, retainerId);
                plan.Execute(
                    capturePlayerInventory: static () => { },
                    captureSaddlebag: static () => { },
                    captureRetainerInventory: () => EnqueueCompleteRetainerSnapshot(CaptureRetainerInventory(owner!, retainerId!.Value)),
                    captureRetainerListings: static () => { },
                    reportFailure: ex => diagnostic?.Invoke("The closing retainer observation could not be captured.", ex));
                if (owner is not null && retainerId is not null)
                    EnqueueCompleteRetainerSnapshot(CaptureRetainerGil(owner, retainerId.Value));
            }
            catch (Exception ex)
            {
                diagnostic?.Invoke("The closing retainer observation could not be captured.", ex);
            }
        }

        private void ReportVisit(string message, Exception? ex)
        {
            try
            {
                if (message.StartsWith("[Warning]", StringComparison.Ordinal))
                    pluginLog?.Warning(ex, "{Message}", message);
            }
            catch { }
            try { diagnostic?.Invoke(message, ex); } catch { }
        }

        private void OnVisitOpened(ulong previousId, ulong id, double now)
        {
            visit.Open(id, now);
            visitOwner = CurrentOwnerOrDefault();
            settledInventoryPending = settledGilPending = false;
            nextVisitCheck = now;
            reportedSuppressions.Clear();
            ReportVisit($"RetainerVisitOpened: generation={visit.Generation}, previous={previousId:X16}, retainer={id:X16}.", null);
        }

        private void OnVisitBatch(int[] slots, double now)
        {
            reportedSuppressions.Clear();
            var active = CurrentRetainerIdOrDefault() ?? 0;
            if (!visit.Apply(visit.Generation, active, now, slots))
                ReportVisit($"[Warning] RetainerVisitLateArrival: active={active:X16}, {VisitContext(now, null)}, arrivingCovered=[{string.Join(",", slots)}].", null);
            else if (visit.FirstBatchIsAmbiguous && visit.Applies.Count == 1)
                ReportVisit($"[Warning] RetainerVisitLateArrival: possible boundary-straddling first batch (no wire retainer identity); second apply required, {VisitContext(now, null)}.", null);
        }

        private bool AllowRetainerCapture(ulong? id, string path)
        {
            if (visitHooks?.Available == true && id is { } value && visit.CanCapture(value) &&
                visitOwner is not null && visitOwner == CurrentOwnerOrDefault())
                return true;
            if (reportedSuppressions.Add(path))
                ReportVisit($"RetainerVisitSuppressedUnproven: path={path}, requested={id:X16}, generation={visit.Generation}, retainer={visit.RetainerId:X16}; full settle-time capture supersedes suppressed deltas.", null);
            return false;
        }

        private unsafe HashSet<int>? OccupiedMarketSlots()
        {
            var manager = InventoryManager.Instance();
            var container = manager == null ? null : manager->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null || !container->IsLoaded) return null;
            var items = gameInventory.GetInventoryItems(GameInventoryType.RetainerMarket);
            if (items.Length != 20) return null;
            var occupied = new HashSet<int>();
            foreach (ref readonly var item in items)
                if (!item.IsEmpty && item.ItemId != 0 && item.Quantity > 0)
                    occupied.Add(checked((int)item.InventorySlot));
            return occupied;
        }

        private static unsafe bool ActiveRosterIsEmpty(ulong id)
        {
            var manager = RetainerManager.Instance();
            var retainer = manager == null ? null : manager->GetActiveRetainer();
            return retainer != null && retainer->RetainerId == id && retainer->MarketItemCount == 0;
        }

        private string VisitContext(double now, IReadOnlySet<int>? occupied) =>
            $"generation={visit.Generation}, retainer={visit.RetainerId:X16}, elapsedMs={now - visit.OpenedAt:F1}, " +
            $"applyElapsedMs=[{string.Join(",", visit.Applies.Select(time => (time - visit.OpenedAt).ToString("F1")))}], " +
            $"covered=[{string.Join(",", visit.CoveredSlots.Order())}], occupied=[{(occupied is null ? "unavailable" : string.Join(",", occupied.Order()))}]";

        private void OnVisitUpdate(IFramework _)
        {
            if (visitHooks?.Available != true || visit.RetainerId == 0 ||
                (visit.Proven && !settledInventoryPending && !settledGilPending)) return;
            try
            {
                var now = RetainerVisitProofHooks.Now;
                if (now < nextVisitCheck) return;
                nextVisitCheck = now + 50;
                var active = CurrentRetainerIdOrDefault() ?? 0;
                if (visit.Proven)
                {
                    CaptureDeferredRetainerState(active);
                    return;
                }
                var occupied = OccupiedMarketSlots();
                var outcome = visit.Evaluate(now, active, occupied, ActiveRosterIsEmpty(active));
                if (outcome is null) return;
                ReportVisit($"{(outcome == "RetainerVisitProofTimeout" ? "[Warning] " : string.Empty)}{outcome}: {VisitContext(now, occupied)}.", null);
                if (outcome != "RetainerVisitProven" || !AllowRetainerCapture(active, "settle")) return;
                var owner = visitOwner!;
                // These run together on the framework thread; no native selection can
                // interleave. Isolate capture failures so unavailable gil cannot lose listings.
                CaptureSettled(() => EnqueueLatestListing(CaptureRetainerListings(owner, active)));
                settledInventoryPending = settledGilPending = true;
                CaptureDeferredRetainerState(active);
            }
            catch (Exception ex) { ReportVisit("RetainerVisitCaptureUnavailable: settle-time observation failed.", ex); }
        }

        private void CaptureDeferredRetainerState(ulong active)
        {
            if (!AllowRetainerCapture(active, "settle-inventory")) return;
            // Some visits load these containers later than prices. Preserve the last
            // truthful snapshot and recover automatically, including before a close.
            if (settledInventoryPending)
                CaptureSettled(() => settledInventoryPending = !EnqueueCompleteRetainerSnapshot(CaptureRetainerInventory(visitOwner!, active)));
            if (settledGilPending)
                CaptureSettled(() => settledGilPending = !EnqueueCompleteRetainerSnapshot(CaptureRetainerGil(visitOwner!, active)));
        }

        private bool EnqueueCompleteRetainerSnapshot(ObservationEnvelope snapshot)
        {
            if (snapshot.Capture.Evidence.Completeness != ObservationCompleteness.Complete)
            {
                var path = $"incomplete-{snapshot.Scope.Container}";
                if (reportedSuppressions.Add(path))
                    ReportVisit($"RetainerVisitCaptureUnavailable: retainer={visit.RetainerId:X16}, container={snapshot.Scope.Container}; preserving the previous observation until the full container is available.", null);
                return false;
            }
            Enqueue(snapshot);
            return true;
        }

        private void CaptureSettled(Action capture)
        {
            try { capture(); }
            catch (Exception ex) { ReportVisit("RetainerVisitCaptureUnavailable: a settled observation could not be captured.", ex); }
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
                    rows.Add(new RetainerRosterObservation(
                        retainer->RetainerId,
                        retainer->NameString,
                        owner.HomeWorldId,
                        checked((int)index),
                        null,
                        retainer->ClassJob,
                        retainer->Level,
                        retainer->MarketItemCount,
                        retainer->Available));
                }
            }
            return Envelope(
                new ObservationScope(owner, ObservationSubject.Character(owner), ObservationContainerKind.RetainerRoster),
                Evidence(complete),
                ObservationPayload.Create(ObservationPayloadContracts.RetainerRoster, ObservationPayloadContracts.Version, new RetainerRosterPayload(rows)));
        }

        private ObservationEnvelope CaptureRetainerInventory(ObservationOwner owner, ulong retainerId)
        {
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

        private unsafe ObservationEnvelope CaptureRetainerGil(ObservationOwner owner, ulong retainerId)
        {
            var manager = InventoryManager.Instance();
            var container = manager is null ? null : manager->GetInventoryContainer(InventoryType.RetainerGil);
            var complete = container is not null && container->IsLoaded;
            ulong gil = 0;
            if (complete)
            {
                for (var index = 0; index < container->Size; index++)
                {
                    var slot = container->GetInventorySlot(index);
                    if (slot is not null)
                        gil = checked(gil + (ulong)Math.Max(0, slot->Quantity));
                }
            }
            return Envelope(
                new ObservationScope(owner, ObservationSubject.Retainer(retainerId, owner), ObservationContainerKind.RetainerGil),
                Evidence(complete),
                ObservationPayload.Create(
                    ObservationPayloadContracts.RetainerGil,
                    ObservationPayloadContracts.Version,
                    new RetainerGilPayload(gil)));
        }

        private unsafe ObservationEnvelope CaptureRetainerListings(ObservationOwner owner, ulong retainerId)
        {
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
                    evidence,
                    captureSessions?.Resolve(scope) ?? string.Empty),
                payload);

        private InventoryObservationDelta Delta(ObservationScope scope, IReadOnlyList<InventorySlotUpdate> updates) =>
            new(
                scope,
                new ObservationCapture(
                    Interlocked.Increment(ref sourceRevision),
                    DateTimeOffset.UtcNow,
                    provenance,
                    ObservationEvidence.CompleteAvailable,
                    captureSessions?.Resolve(scope) ?? string.Empty),
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
            var owner = CurrentOwnerOrDefault();
            if (owner is null)
                throw new InvalidOperationException("The current character identity is not stable.");
            return owner;
        }

        private ObservationOwner? CurrentOwnerOrDefault()
        {
            if (playerState.ContentId == 0 || !playerState.HomeWorld.IsValid)
                return null;
            return new ObservationOwner(playerState.ContentId, playerState.HomeWorld.Value.RowId);
        }

        private static unsafe ulong? CurrentRetainerIdOrDefault()
        {
            var manager = RetainerManager.Instance();
            if (manager is null || manager->LastSelectedRetainerId == 0)
                return null;
            return manager->LastSelectedRetainerId;
        }

        private void Enqueue(ObservationEnvelope observation)
        {
            if (!queue.TryWrite(SqliteObservationStore.CreateScopeKey(observation.Scope), "snapshot",
                    token => store.WriteAsync(observation, token)))
                diagnostic?.Invoke("The shared-observation queue was already closed; a snapshot was not collected.", null);
        }

        private void Enqueue(InventoryObservationDelta observation)
        {
            if (!queue.TryWrite(SqliteObservationStore.CreateScopeKey(observation.Scope), "delta",
                    token => store.WriteInventoryDeltaAsync(observation, token)))
                diagnostic?.Invoke("The shared-observation queue was already closed; an inventory change was not collected.", null);
        }

        private void EnqueueLatestListing(ObservationEnvelope observation)
        {
            var key = $"{observation.Scope.Owner.LocalContentId:X16}:{observation.Scope.Owner.HomeWorldId}:{observation.Scope.Subject.Id:X16}";
            if (!pendingListings.Offer(key, observation))
                return;
            if (!queue.TryWrite(SqliteObservationStore.CreateScopeKey(observation.Scope), "listing",
                    token => pendingListings.TryTake(key, out var snapshot)
                        ? store.WriteAsync(snapshot!, token)
                        : ValueTask.FromResult(new ObservationWriteResult(ObservationWriteStatus.IgnoredRepeatedRevision, "Listing already drained."))))
                diagnostic?.Invoke("The shared-observation queue was already closed; a listing snapshot was not collected.", null);
        }

    }
}
