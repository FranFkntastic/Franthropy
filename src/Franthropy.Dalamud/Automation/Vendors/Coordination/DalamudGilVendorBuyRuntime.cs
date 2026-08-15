using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Travel;
using Lumina.Excel.Sheets;

namespace Franthropy.Dalamud.Automation.Vendors.Coordination;

public sealed class DalamudGilVendorBuyRuntime : IGilVendorBuyRuntime
{
    private static readonly TimeSpan ApproachTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ActionThrottle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AethernetRetryDelay = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan FlightNavigationStartTimeout = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlySet<string> PendingAethernetOwnedAddonProtection =
        new HashSet<string>(StringComparer.Ordinal) { "SelectString" };
    private const int MaximumAethernetSubmissions = 3;
    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];
    private const float DirectInteractionDistance = 4.25f;
    private const float NavigationStopDistance = 3.5f;
    private readonly DalamudGilVendorAccessReader access;
    private readonly DalamudOrdinaryGilShop shop;
    private readonly DalamudVNavmeshTravel vnavmesh;
    private readonly DalamudLocalTravelRunner localTravel;
    private readonly DalamudLifestreamAetheryteTravel aetheryteTravel;
    private readonly DalamudLifestreamAethernetTravel aethernetTravel;
    private readonly DalamudLifestreamObjectInteractor objectInteractor;
    private readonly DalamudTravelReadiness travelReadiness;
    private readonly IDataManager dataManager;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly GilVendorPrePurchaseRecovery recovery = new();
    private readonly System.Action beginAutomation;
    private readonly System.Action endAutomation;
    private readonly Func<DateTimeOffset> utcNow;
    private DateTimeOffset approachStartedAt;
    private DateTimeOffset nextActionAt;
    private uint activeNpcId;
    private uint? requestedAetheryteId;
    private uint? requestedAethernetId;
    private DateTimeOffset? aethernetSubmittedAt;
    private int aethernetSubmissionCount;
    private bool ownsNavigation;
    private LocalTravelMode? activeTravelMode;
    private DateTimeOffset? navigationSubmittedAt;
    private bool navigationObservedRunning;
    private bool automationActive;

    public DalamudGilVendorBuyRuntime(
        DalamudGilVendorAccessReader access,
        DalamudOrdinaryGilShop shop,
        DalamudVNavmeshTravel vnavmesh,
        DalamudLifestreamAetheryteTravel aetheryteTravel,
        DalamudLifestreamAethernetTravel aethernetTravel,
        DalamudLifestreamObjectInteractor objectInteractor,
        DalamudTravelReadiness travelReadiness,
        IDataManager dataManager,
        IClientState clientState,
        IObjectTable objectTable,
        ICondition condition,
        System.Action? beginAutomation = null,
        System.Action? endAutomation = null,
        Func<DateTimeOffset>? utcNow = null)
        : this(
            access,
            shop,
            vnavmesh,
            aetheryteTravel,
            aethernetTravel,
            objectInteractor,
            travelReadiness,
            dataManager,
            clientState,
            objectTable,
            condition,
            commandManager: null,
            beginAutomation: beginAutomation,
            endAutomation: endAutomation,
            utcNow: utcNow)
    {
    }

    public DalamudGilVendorBuyRuntime(
        DalamudGilVendorAccessReader access,
        DalamudOrdinaryGilShop shop,
        DalamudVNavmeshTravel vnavmesh,
        DalamudLifestreamAetheryteTravel aetheryteTravel,
        DalamudLifestreamAethernetTravel aethernetTravel,
        DalamudLifestreamObjectInteractor objectInteractor,
        DalamudTravelReadiness travelReadiness,
        IDataManager dataManager,
        IClientState clientState,
        IObjectTable objectTable,
        ICondition condition,
        ICommandManager? commandManager,
        System.Action? beginAutomation = null,
        System.Action? endAutomation = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.access = access ?? throw new ArgumentNullException(nameof(access));
        this.shop = shop ?? throw new ArgumentNullException(nameof(shop));
        this.vnavmesh = vnavmesh ?? throw new ArgumentNullException(nameof(vnavmesh));
        this.aetheryteTravel = aetheryteTravel ?? throw new ArgumentNullException(nameof(aetheryteTravel));
        this.aethernetTravel = aethernetTravel ?? throw new ArgumentNullException(nameof(aethernetTravel));
        this.objectInteractor = objectInteractor ?? throw new ArgumentNullException(nameof(objectInteractor));
        this.travelReadiness = travelReadiness ?? throw new ArgumentNullException(nameof(travelReadiness));
        this.dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        this.clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        this.objectTable = objectTable ?? throw new ArgumentNullException(nameof(objectTable));
        localTravel = commandManager is null
            ? new DalamudLocalTravelRunner(
                condition ?? throw new ArgumentNullException(nameof(condition)),
                objectTable)
            : new DalamudLocalTravelRunner(
                condition ?? throw new ArgumentNullException(nameof(condition)),
                objectTable,
                commandManager,
                dataManager,
                clientState);
        this.beginAutomation = beginAutomation ?? (() => { });
        this.endAutomation = endAutomation ?? (() => { });
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public GilVendorInventorySnapshot CaptureInventory(IReadOnlyCollection<uint> itemIds)
    {
        if (!TryCaptureBags(out var stacks, out _, out var message))
            return new(false, null, new Dictionary<uint, int>(), message);
        var counts = itemIds.ToDictionary(
            itemId => itemId,
            itemId => stacks.Where(stack => stack.ItemId == itemId).Sum(stack => stack.Quantity));
        var gil = ScanPlayerGil();
        return gil is null
            ? new(false, null, counts, "Player gil is temporarily unavailable.")
            : new(true, gil, counts, "Player inventory and gil are ready.");
    }

    public bool HasCapacity(IReadOnlyDictionary<uint, int> quantities, out string message)
    {
        if (!TryCaptureBags(out var stacks, out var freeSlots, out message))
            return false;
        foreach (var (itemId, quantity) in quantities.Where(pair => pair.Value > 0))
        {
            var item = dataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);
            var maxStack = Math.Max(1, checked((int)(item?.StackSize ?? 1)));
            var existingSpace = stacks
                .Where(stack => stack.ItemId == itemId && !stack.IsHighQuality)
                .Sum(stack => Math.Max(0, maxStack - stack.Quantity));
            var requiringSlots = Math.Max(0, quantity - existingSpace);
            freeSlots -= (requiringSlots + maxStack - 1) / maxStack;
            if (freeSlots < 0)
            {
                var itemName = item?.Name.ToString();
                message = $"Not enough player-inventory stack capacity remains for {(!string.IsNullOrWhiteSpace(itemName) ? itemName : $"item {itemId}")}; free a bag slot and resume.";
                return false;
            }
        }
        message = "Player inventory has enough stack capacity for the requested quantities.";
        return true;
    }

    public GilVendorReachResult AdvanceToOpenShop(GilVendorOffer offer)
    {
        if (shop.IsOpen)
        {
            recovery.ResetMenu();
            return new(GilVendorReachState.ShopOpen, $"Opened {offer.NpcName}'s shop.");
        }
        if (activeNpcId != offer.NpcId)
        {
            ResetVendorApproach();
            activeNpcId = offer.NpcId;
            approachStartedAt = utcNow();
        }
        var assessment = access.Assess(offer);
        if (!assessment.IsEligible)
            return assessment.State == GilVendorAccessState.Unknown
                ? new(GilVendorReachState.Waiting, assessment.Message)
                : new(GilVendorReachState.Unavailable, assessment.Message);
        if (utcNow() - approachStartedAt > ApproachTimeout)
            return new(GilVendorReachState.Unavailable, $"Could not reach {offer.NpcName} within two minutes.");
        var readiness = travelReadiness.Advance(ProtectedOwnedAddonsForPendingAethernet(requestedAethernetId));
        if (readiness.State is TravelReadinessState.Repairing or TravelReadinessState.Waiting)
            return new(GilVendorReachState.Waiting, readiness.Message);
        if (readiness.State == TravelReadinessState.Blocked)
        {
            if (ShouldWaitForPendingTravelUi(readiness, requestedAetheryteId is not null || requestedAethernetId is not null))
                return new(GilVendorReachState.Waiting, "Waiting for the in-progress vendor travel to release the game UI.");
            return new(GilVendorReachState.Failed, readiness.Message);
        }
        if (clientState.TerritoryType != offer.TerritoryId)
        {
            if (assessment.RouteAetheryteId is not { } route)
                return new(GilVendorReachState.Unavailable, "No live owner-accessible route reaches this vendor.");
            var aethernetRecovery = DetermineAethernetRecovery(
                clientState.TerritoryType,
                offer.TerritoryId,
                assessment.RouteAetheryteTerritoryId,
                assessment.RouteAethernetId,
                requestedAethernetId,
                aethernetSubmittedAt,
                aethernetSubmissionCount,
                utcNow());
            if (aethernetRecovery == GilVendorAethernetRecoveryState.Exhausted)
            {
                return new(
                    GilVendorReachState.Unavailable,
                    $"Lifestream did not complete the aethernet leg to {offer.NpcName} after {MaximumAethernetSubmissions} attempts.");
            }
            if (aethernetRecovery == GilVendorAethernetRecoveryState.Retry)
            {
                requestedAethernetId = null;
                aethernetSubmittedAt = null;
                nextActionAt = utcNow();
                return new(
                    GilVendorReachState.Waiting,
                    $"Lifestream released the incomplete aethernet leg; retrying travel to {offer.NpcName} ({aethernetSubmissionCount + 1}/{MaximumAethernetSubmissions}).");
            }
            switch (DetermineTravelLeg(clientState.TerritoryType, offer.TerritoryId, route,
                        assessment.RouteAethernetId, assessment.RouteAetheryteTerritoryId,
                        requestedAetheryteId, requestedAethernetId))
            {
                case GilVendorTravelLeg.InvalidRoute:
                    return new(GilVendorReachState.Unavailable, "The vendor's aethernet route is missing the main aetheryte territory needed to confirm arrival.");
                case GilVendorTravelLeg.SubmitAetheryte:
                    if (utcNow() >= nextActionAt)
                    {
                        var submission = aetheryteTravel.TrySubmit(route);
                        switch (submission.State)
                        {
                            case AetheryteTravelSubmissionState.Submitted:
                                requestedAetheryteId = route;
                                nextActionAt = utcNow().Add(ActionThrottle);
                                travelReadiness.Reset();
                                break;
                            case AetheryteTravelSubmissionState.Busy:
                                return new(GilVendorReachState.Waiting, submission.Message);
                            case AetheryteTravelSubmissionState.Rejected:
                            case AetheryteTravelSubmissionState.Unavailable:
                            case AetheryteTravelSubmissionState.InvalidRequest:
                                return new(GilVendorReachState.Failed, submission.Message);
                        }
                    }
                    break;
                case GilVendorTravelLeg.AwaitAetheryteArrival:
                    return new(GilVendorReachState.Waiting, "Waiting to arrive at the main aetheryte before entering the destination network.");
                case GilVendorTravelLeg.SubmitAethernet:
                    if (requestedAetheryteId != route)
                        requestedAetheryteId = route;
                    if (assessment.RouteAethernetId is { } aethernetId && utcNow() >= nextActionAt)
                    {
                        var submission = aethernetTravel.TrySubmit(aethernetId);
                        switch (submission.State)
                        {
                            case AetheryteTravelSubmissionState.Submitted:
                                requestedAethernetId = aethernetId;
                                aethernetSubmittedAt = utcNow();
                                aethernetSubmissionCount++;
                                nextActionAt = utcNow().Add(ActionThrottle);
                                travelReadiness.Reset();
                                break;
                            case AetheryteTravelSubmissionState.Busy:
                                return new(GilVendorReachState.Waiting, submission.Message);
                            case AetheryteTravelSubmissionState.Rejected:
                                nextActionAt = utcNow().Add(ActionThrottle);
                                return new(GilVendorReachState.Waiting, "Waiting for the destination aethernet network to accept travel.");
                            case AetheryteTravelSubmissionState.Unavailable:
                            case AetheryteTravelSubmissionState.InvalidRequest:
                                return new(GilVendorReachState.Failed, submission.Message);
                        }
                    }
                    break;
            }
            return new(GilVendorReachState.Waiting, $"Traveling to {offer.NpcName}.");
        }
        if (requestedAetheryteId is not null)
        {
            requestedAetheryteId = null;
            requestedAethernetId = null;
            aethernetSubmittedAt = null;
            aethernetSubmissionCount = 0;
            approachStartedAt = utcNow();
            nextActionAt = DateTimeOffset.MinValue;
            recovery.ResetNavigation();
            localTravel.Reset();
            activeTravelMode = null;
            ResetNavigationTracking();
        }
        var npc = access.FindLiveNpc(offer);
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (playerPosition is null)
            return new(GilVendorReachState.Waiting, "Waiting for the player's position after travel.");
        var destination = npc?.Position ?? offer.Position;
        var distance = HorizontalDistance(playerPosition.Value, destination);
        if (distance <= DirectInteractionDistance)
        {
            if (npc is null)
                return new(GilVendorReachState.Waiting, $"Waiting for {offer.NpcName} to become targetable.");
            StopOwnedNavigation();
            recovery.ResetNavigation();
            localTravel.Reset();
            if (utcNow() < nextActionAt)
                return new(GilVendorReachState.Waiting, $"Opening {offer.NpcName}'s shop.");
            return InteractWithVendor(npc, offer);
        }
        var navigation = vnavmesh.Observe();
        if (navigation.State == VNavmeshLifecycleState.Loading)
            return new(GilVendorReachState.Waiting, navigation.Message);
        if (navigation.State is VNavmeshLifecycleState.Unavailable or VNavmeshLifecycleState.IpcFailure)
            return new(GilVendorReachState.Failed, navigation.Message);
        if (ownsNavigation && navigation.IsRunning && !navigationObservedRunning)
        {
            navigationObservedRunning = true;
            recovery.RecordNavigationStarted(utcNow(), distance);
        }
        switch (DetermineFlightNavigationState(
                    ownsNavigation,
                    activeTravelMode,
                    navigation.IsRunning,
                    navigationObservedRunning,
                    navigationSubmittedAt,
                    utcNow()))
        {
            case GilVendorFlightNavigationState.AwaitingStart:
                return new(GilVendorReachState.Waiting, $"Waiting for the flight route to {offer.NpcName} to start.");
            case GilVendorFlightNavigationState.Downgrade:
                if (!vnavmesh.TryStop())
                    return new(GilVendorReachState.Failed, $"Could not cancel the failed flight route to {offer.NpcName} before retrying by ground.");
                ownsNavigation = false;
                activeTravelMode = null;
                ResetNavigationTracking();
                localTravel.DowngradeFlight();
                recovery.ResetNavigation();
                nextActionAt = DateTimeOffset.MinValue;
                return new(GilVendorReachState.Waiting, $"The flight route to {offer.NpcName} did not remain active; retrying the same destination by ground.");
        }
        var decision = DecideApproach(distance, npc is not null,
            navigation.State == VNavmeshLifecycleState.Ready,
            navigation.State == VNavmeshLifecycleState.Running, ownsNavigation);
        switch (decision)
        {
            case GilVendorApproachDecision.WaitForOwnedRoute:
                switch (recovery.ObserveOwnedNavigation(utcNow(), distance))
                {
                    case GilVendorOwnedNavigationDecision.Restart:
                        var stalledMode = activeTravelMode;
                        if (!TryStopOwnedNavigation())
                            return new(GilVendorReachState.Failed, $"Could not stop the stalled route to {offer.NpcName}.");
                        nextActionAt = DateTimeOffset.MinValue;
                        if (stalledMode == LocalTravelMode.Flight)
                        {
                            localTravel.DowngradeFlight();
                            recovery.ResetNavigation();
                            return new(
                                GilVendorReachState.Waiting,
                                $"The flight route to {offer.NpcName} stalled; retrying the same destination by ground.");
                        }
                        return new(
                            GilVendorReachState.Waiting,
                            $"Restarting the stalled route to {offer.NpcName} ({recovery.NavigationRecoveryCount}/{recovery.MaximumNavigationRecoveries}).");
                    case GilVendorOwnedNavigationDecision.Exhausted:
                        return new(
                            GilVendorReachState.Unavailable,
                            $"Could not make progress toward {offer.NpcName} after {recovery.MaximumNavigationRecoveries} automatic route restarts.");
                }
                return new(GilVendorReachState.Waiting, DescribeApproach(activeTravelMode, offer.NpcName, distance));
            case GilVendorApproachDecision.BlockedByAnotherRoute:
                return new(GilVendorReachState.Failed, "Another vnavmesh route is already active.");
            case GilVendorApproachDecision.NavigationUnavailable:
                return new(GilVendorReachState.Waiting, navigation.Message);
        }
        if (utcNow() >= nextActionAt)
        {
            var preparation = localTravel.Advance(distance, utcNow());
            if (!preparation.IsReady)
                return new(
                    preparation.State == LocalTravelPreparationState.Unavailable
                        ? GilVendorReachState.Unavailable
                        : GilVendorReachState.Waiting,
                    preparation.Message);
            var submissionDecision = recovery.ClassifyNavigationSubmission();
            if (submissionDecision == GilVendorNavigationSubmissionDecision.Exhausted)
            {
                return new(
                    GilVendorReachState.Unavailable,
                    $"Could not keep a route active toward {offer.NpcName} after {recovery.MaximumNavigationRecoveries} automatic restarts.");
            }
            var movement = vnavmesh.TryMoveCloseTo(
                destination,
                NavigationStopDistance,
                preparation.VNavmeshMode);
            if (movement.State == VNavmeshPathSubmissionState.Loading)
                return new(GilVendorReachState.Waiting, movement.Message);
            if (!movement.Submitted)
                return new(GilVendorReachState.Failed, movement.Message);
            recovery.RecordNavigationSubmission(utcNow(), distance);
            ownsNavigation = true;
            activeTravelMode = preparation.Mode;
            navigationSubmittedAt = utcNow();
            navigationObservedRunning = false;
            nextActionAt = utcNow().Add(ActionThrottle);
        }
        return new(GilVendorReachState.Waiting, DescribeApproach(activeTravelMode, offer.NpcName, distance));
    }

    public void ResetVendorApproach()
    {
        StopOwnedNavigation();
        approachStartedAt = utcNow();
        nextActionAt = DateTimeOffset.MinValue;
        activeNpcId = 0;
        requestedAetheryteId = null;
        requestedAethernetId = null;
        aethernetSubmittedAt = null;
        aethernetSubmissionCount = 0;
        travelReadiness.Reset();
        recovery.Reset();
        localTravel.Reset();
        activeTravelMode = null;
        ResetNavigationTracking();
    }

    public GilVendorShopReadResult ReadShopRows() => shop.ReadRows();
    public bool TrySubmitPurchase(GilVendorShopRow row, uint quantity, out string error) => shop.TrySubmitPurchase(row, quantity, out error);
    public bool TryConfirmPurchasePrompt() => shop.TryConfirmOwnedPrompt();
    public int ResolveMaximumBatch(uint itemId) => (dataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.StackSize ?? 1) <= 1 ? 1 : 99;
    public void CloseShop() => shop.Close();
    public void BeginAutomation()
    {
        if (automationActive)
            return;
        beginAutomation();
        automationActive = true;
    }

    public void EndAutomation()
    {
        if (!automationActive)
            return;
        try
        {
            try
            {
                StopOwnedNavigation();
            }
            finally
            {
                localTravel.Reset();
                endAutomation();
            }
        }
        finally
        {
            automationActive = false;
        }
    }

    private GilVendorReachResult InteractWithVendor(IGameObject npc, GilVendorOffer offer)
    {
        var menu = shop.TryAdvanceOfferMenu(offer);
        switch (recovery.ObserveMenu(utcNow(), menu.MenuPresented, menu.Advanced))
        {
            case GilVendorMenuRecoveryDecision.Advanced:
                nextActionAt = utcNow().Add(ActionThrottle);
                return new(GilVendorReachState.Waiting, $"Choosing {offer.NpcName}'s shop.");
            case GilVendorMenuRecoveryDecision.Wait:
                return new(GilVendorReachState.Waiting, $"Waiting for {offer.NpcName}'s reviewed shop option.");
            case GilVendorMenuRecoveryDecision.Reinteract:
                shop.TryCloseOfferMenu();
                nextActionAt = utcNow().Add(ActionThrottle);
                return new(
                    GilVendorReachState.Waiting,
                    $"Reopening {offer.NpcName}'s shop ({recovery.MenuReinteractionCount}/{recovery.MaximumMenuReinteractions}).");
            case GilVendorMenuRecoveryDecision.Exhausted:
                return new(
                    GilVendorReachState.Unavailable,
                    $"{offer.NpcName} did not present the reviewed shop after {recovery.MaximumMenuReinteractions} automatic reinteractions.");
        }
        var interaction = objectInteractor.TryEnqueue(offer.NpcId, NavigationStopDistance, "Gil vendor interaction");
        if (!interaction.Success)
            return new(GilVendorReachState.Failed, interaction.Message);
        nextActionAt = utcNow().Add(ActionThrottle);
        return new(GilVendorReachState.Waiting, $"Opening {offer.NpcName}'s shop.");
    }

    private void StopOwnedNavigation()
    {
        _ = TryStopOwnedNavigation();
    }

    private bool TryStopOwnedNavigation()
    {
        if (!ownsNavigation)
            return true;
        // An accepted asynchronous submission is ours before vnavmesh reports it running.
        // Cancel it unconditionally so a reset cannot release a path that starts later.
        if (!vnavmesh.TryStop())
            return false;
        ownsNavigation = false;
        activeTravelMode = null;
        ResetNavigationTracking();
        return true;
    }

    private void ResetNavigationTracking()
    {
        navigationSubmittedAt = null;
        navigationObservedRunning = false;
    }

    private static string DescribeApproach(LocalTravelMode? mode, string npcName, float distance) => mode switch
    {
        LocalTravelMode.Flight => $"Flying to {npcName} ({distance:0.0} yalms away).",
        LocalTravelMode.GroundMount => $"Riding to {npcName} ({distance:0.0} yalms away).",
        LocalTravelMode.Sprint => $"Sprinting to {npcName} ({distance:0.0} yalms away).",
        _ => $"Walking to {npcName} ({distance:0.0} yalms away).",
    };

    private static float HorizontalDistance(Vector3 first, Vector3 second)
    {
        var dx = first.X - second.X;
        var dz = first.Z - second.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private static unsafe bool TryCaptureBags(out IReadOnlyList<DalamudInventoryStack> stacks, out int freeSlots, out string message)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            stacks = [];
            freeSlots = 0;
            message = "Player inventory is still loading.";
            return false;
        }
        freeSlots = 0;
        foreach (var type in PlayerBags)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
            {
                stacks = [];
                message = $"Player inventory is still loading ({type} unavailable).";
                return false;
            }
            var occupied = 0;
            for (var index = 0; index < container->Size; index++)
            {
                var slot = container->GetInventorySlot(index);
                if (slot != null && slot->ItemId != 0 && slot->Quantity != 0) occupied++;
            }
            freeSlots += container->Size - occupied;
        }
        stacks = DalamudInventoryStackScanner.ScanLoadedStacks(PlayerBags);
        message = "Player inventory and stack capacity are ready.";
        return true;
    }

    private static unsafe ulong? ScanPlayerGil()
    {
        try
        {
            var manager = InventoryManager.Instance();
            return manager == null ? null : manager->GetGil();
        }
        catch { return null; }
    }

    internal static GilVendorApproachDecision DecideApproach(float distance, bool npcAvailable, bool navigationReady, bool navigationRunning, bool ownsNavigation)
    {
        if (distance <= DirectInteractionDistance) return npcAvailable ? GilVendorApproachDecision.Interact : GilVendorApproachDecision.WaitForNpc;
        if (navigationRunning) return ownsNavigation ? GilVendorApproachDecision.WaitForOwnedRoute : GilVendorApproachDecision.BlockedByAnotherRoute;
        return navigationReady ? GilVendorApproachDecision.StartNavigation : GilVendorApproachDecision.NavigationUnavailable;
    }

    internal static GilVendorTravelLeg DetermineTravelLeg(uint currentTerritoryId, uint targetTerritoryId, uint routeAetheryteId, uint? routeAethernetId, uint? routeAetheryteTerritoryId, uint? requestedAetheryteId, uint? requestedAethernetId)
    {
        if (currentTerritoryId == targetTerritoryId) return GilVendorTravelLeg.AwaitDestination;
        if (routeAethernetId is null) return requestedAetheryteId == routeAetheryteId ? GilVendorTravelLeg.AwaitDestination : GilVendorTravelLeg.SubmitAetheryte;
        if (routeAetheryteTerritoryId is not { } aetheryteTerritoryId) return GilVendorTravelLeg.InvalidRoute;
        if (currentTerritoryId != aetheryteTerritoryId) return requestedAetheryteId == routeAetheryteId ? GilVendorTravelLeg.AwaitAetheryteArrival : GilVendorTravelLeg.SubmitAetheryte;
        return requestedAethernetId == routeAethernetId ? GilVendorTravelLeg.AwaitDestination : GilVendorTravelLeg.SubmitAethernet;
    }

    internal static bool ShouldWaitForPendingTravelUi(TravelReadinessResult readiness, bool travelRequestPending) =>
        readiness.State == TravelReadinessState.Blocked && readiness.Code == "UnknownUiOwner" && travelRequestPending;

    internal static IReadOnlySet<string>? ProtectedOwnedAddonsForPendingAethernet(uint? requestedAethernetId) =>
        requestedAethernetId is null ? null : PendingAethernetOwnedAddonProtection;

    internal static GilVendorAethernetRecoveryState DetermineAethernetRecovery(
        uint currentTerritoryId,
        uint targetTerritoryId,
        uint? routeAetheryteTerritoryId,
        uint? routeAethernetId,
        uint? requestedAethernetId,
        DateTimeOffset? submittedAt,
        int submissionCount,
        DateTimeOffset observedAt)
    {
        if (currentTerritoryId == targetTerritoryId ||
            routeAetheryteTerritoryId != currentTerritoryId ||
            routeAethernetId is null ||
            requestedAethernetId != routeAethernetId ||
            submittedAt is null ||
            observedAt - submittedAt.Value < AethernetRetryDelay)
        {
            return GilVendorAethernetRecoveryState.Continue;
        }

        return submissionCount >= MaximumAethernetSubmissions
            ? GilVendorAethernetRecoveryState.Exhausted
            : GilVendorAethernetRecoveryState.Retry;
    }

    internal static GilVendorFlightNavigationState DetermineFlightNavigationState(
        bool ownsNavigation,
        LocalTravelMode? activeTravelMode,
        bool navigationRunning,
        bool navigationObservedRunning,
        DateTimeOffset? navigationSubmittedAt,
        DateTimeOffset observedAt)
    {
        if (!ownsNavigation || activeTravelMode != LocalTravelMode.Flight || navigationRunning)
            return GilVendorFlightNavigationState.Continue;
        if (navigationObservedRunning || navigationSubmittedAt is null ||
            observedAt - navigationSubmittedAt.Value >= FlightNavigationStartTimeout)
            return GilVendorFlightNavigationState.Downgrade;
        return GilVendorFlightNavigationState.AwaitingStart;
    }
}

internal enum GilVendorApproachDecision { Interact, WaitForNpc, StartNavigation, WaitForOwnedRoute, BlockedByAnotherRoute, NavigationUnavailable }
internal enum GilVendorTravelLeg { InvalidRoute, SubmitAetheryte, AwaitAetheryteArrival, SubmitAethernet, AwaitDestination }
internal enum GilVendorFlightNavigationState { Continue, AwaitingStart, Downgrade }
internal enum GilVendorAethernetRecoveryState { Continue, Retry, Exhausted }
