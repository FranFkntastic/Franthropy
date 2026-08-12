using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Franthropy.Dalamud.Travel;

public enum LocalTravelMode
{
    Flight,
    GroundMount,
    Sprint,
    Walk,
}

public enum LocalTravelPreparationState
{
    Ready,
    Waiting,
    Unavailable,
}

public sealed record LocalTravelPreparationResult(
    LocalTravelPreparationState State,
    LocalTravelMode Mode,
    string Code,
    string Message)
{
    public bool IsReady => State == LocalTravelPreparationState.Ready;
    public VNavmeshTravelMode VNavmeshMode => Mode == LocalTravelMode.Flight
        ? VNavmeshTravelMode.Flight
        : VNavmeshTravelMode.Ground;
}

/// <summary>
/// Product-neutral preparation for local travel. It chooses a useful movement mode, waits for
/// authoritative mount and flight state, and bounds every action request before degrading.
/// </summary>
public sealed class DalamudLocalTravelRunner
{
    internal const float FlightDistance = 60f;
    internal const float GroundMountDistance = 25f;
    private static readonly TimeSpan MountPreparationTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan MountRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TakeoffPreparationTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TakeoffRetryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DismountConfirmationTimeout = TimeSpan.FromSeconds(5);

    private readonly ILocalTravelActions actions;
    private DateTimeOffset? mountPreparationStartedAt;
    private DateTimeOffset? nextMountRequestAt;
    private DateTimeOffset? takeoffPreparationStartedAt;
    private DateTimeOffset? nextTakeoffRequestAt;
    private DateTimeOffset? dismountRequestedAt;
    private bool accelerationRequested;
    private bool flightDisabled;
    private bool mountDisabled;
    private bool requiresDismount;

    public DalamudLocalTravelRunner(ICondition condition, IObjectTable objectTable)
        : this(new DalamudLocalTravelActions(condition, objectTable))
    {
    }

    public DalamudLocalTravelRunner(
        ICondition condition,
        IObjectTable objectTable,
        ICommandManager commandManager,
        IDataManager dataManager,
        IClientState clientState)
        : this(new DalamudLocalTravelActions(condition, objectTable, commandManager, dataManager, clientState))
    {
    }

    internal DalamudLocalTravelRunner(ILocalTravelActions actions)
    {
        this.actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    public LocalTravelPreparationResult Advance(float distance, DateTimeOffset observedAt)
    {
        if (!float.IsFinite(distance) || distance < 0)
            throw new ArgumentOutOfRangeException(nameof(distance));

        var observation = actions.Observe();
        if (flightDisabled && requiresDismount)
        {
            var dismount = PrepareDismount(observation, observedAt);
            if (dismount is not null)
                return dismount;
            observation = actions.Observe();
        }
        if (observation.InFlight)
        {
            ResetMountPreparation();
            ResetTakeoffPreparation();
            return Ready(LocalTravelMode.Flight, "AlreadyFlying", "Flying to the destination.");
        }

        if (!flightDisabled && distance >= FlightDistance && observation.FlightUnlocked)
        {
            var flight = PrepareFlight(observation, observedAt);
            if (flight is not null)
                return flight;

            observation = actions.Observe();
        }

        if (observation.Mounted)
        {
            ResetMountPreparation();
            return Ready(LocalTravelMode.GroundMount, "GroundMountReady", "Riding to the destination.");
        }

        if (distance >= GroundMountDistance)
        {
            var mount = PrepareGroundMount(observation, observedAt);
            if (mount is not null)
                return mount;

            observation = actions.Observe();
        }

        return PrepareGroundAcceleration(observation);
    }

    public void DowngradeFlight()
    {
        flightDisabled = true;
        ResetTakeoffPreparation();
        dismountRequestedAt = null;
        requiresDismount = true;
    }

    public void Reset()
    {
        ResetMountPreparation();
        ResetTakeoffPreparation();
        dismountRequestedAt = null;
        accelerationRequested = false;
        flightDisabled = false;
        mountDisabled = false;
        requiresDismount = false;
    }

    private LocalTravelPreparationResult? PrepareFlight(LocalTravelObservation observation, DateTimeOffset observedAt)
    {
        if (!observation.Mounted)
        {
            var mount = PrepareGroundMount(observation, observedAt);
            if (mount is not null)
                return mount with
                {
                    Mode = LocalTravelMode.Flight,
                    Code = mount.Code == "MountRequested" ? "FlightMountRequested" : mount.Code,
                    Message = mount.Code == "MountRequested"
                        ? "Mounting before flying to the destination."
                        : mount.Message,
                };

            flightDisabled = true;
            return null;
        }

        ResetMountPreparation();
        var firstAttempt = takeoffPreparationStartedAt is null;
        takeoffPreparationStartedAt ??= observedAt;
        nextTakeoffRequestAt ??= observedAt;

        if (observedAt - takeoffPreparationStartedAt.Value >= TakeoffPreparationTimeout)
            return Unavailable(LocalTravelMode.Flight, "TakeoffTimeout", "Could not take off after repeated automatic attempts.");

        if (observation.MountTransition || observation.Casting ||
            observedAt < nextTakeoffRequestAt.Value ||
            !observation.CanTakeOff)
            return Waiting(LocalTravelMode.Flight, "AwaitingTakeoff", "Waiting for takeoff before starting the flight path.");

        var takeoffAccepted = actions.TryTakeOff();
        nextTakeoffRequestAt = observedAt.Add(TakeoffRetryInterval);
        return Waiting(
            LocalTravelMode.Flight,
            takeoffAccepted
                ? firstAttempt ? "TakeoffRequested" : "TakeoffRetryRequested"
                : firstAttempt ? "TakeoffRequestPending" : "TakeoffRetryPending",
            takeoffAccepted
                ? firstAttempt
                    ? "Taking off before starting the flight path."
                    : "Retrying takeoff before starting the flight path."
                : "Waiting to retry takeoff before starting the flight path.");
    }

    private LocalTravelPreparationResult? PrepareGroundMount(LocalTravelObservation observation, DateTimeOffset observedAt)
    {
        if (observation.Mounted)
        {
            ResetMountPreparation();
            return Ready(LocalTravelMode.GroundMount, "GroundMountReady", "Riding to the destination.");
        }

        if (mountDisabled)
            return null;

        if (!observation.MountAllowed)
        {
            mountDisabled = true;
            return null;
        }

        var firstAttempt = mountPreparationStartedAt is null;
        mountPreparationStartedAt ??= observedAt;
        nextMountRequestAt ??= observedAt;

        if (observedAt - mountPreparationStartedAt.Value >= MountPreparationTimeout)
            return Unavailable(LocalTravelMode.GroundMount, "MountTimeout", "Could not summon a mount after repeated automatic attempts.");

        if (observation.MountTransition || observation.Casting ||
            observedAt < nextMountRequestAt.Value ||
            !observation.CanMount)
            return Waiting(LocalTravelMode.GroundMount, "AwaitingMount", "Waiting for the mount before starting the route.");

        var mountAccepted = actions.TryMount();
        nextMountRequestAt = observedAt.Add(MountRetryInterval);
        return Waiting(
            LocalTravelMode.GroundMount,
            mountAccepted
                ? firstAttempt ? "MountRequested" : "MountRetryRequested"
                : firstAttempt ? "MountRequestPending" : "MountRetryPending",
            mountAccepted
                ? firstAttempt
                    ? "Mounting before starting the route."
                    : "Retrying the mount before starting the route."
                : "Waiting to retry the mount before starting the route.");
    }

    private LocalTravelPreparationResult? PrepareDismount(LocalTravelObservation observation, DateTimeOffset observedAt)
    {
        if (!observation.Mounted && !observation.InFlight)
        {
            requiresDismount = false;
            dismountRequestedAt = null;
            ResetMountPreparation();
            mountDisabled = false;
            return null;
        }

        if (dismountRequestedAt is { } requestedAt)
        {
            return observedAt - requestedAt < DismountConfirmationTimeout
                ? Waiting(LocalTravelMode.GroundMount, "AwaitingDismount", "Landing before retrying the route by ground.")
                : Unavailable(LocalTravelMode.GroundMount, "DismountTimeout", "Could not land and dismount for the ground-route retry.");
        }

        if (!observation.CanDismount || !actions.TryDismount())
            return Unavailable(LocalTravelMode.GroundMount, "DismountUnavailable", "Could not dismount for the ground-route retry.");

        dismountRequestedAt = observedAt;
        return Waiting(LocalTravelMode.GroundMount, "DismountRequested", "Landing before retrying the route by ground.");
    }

    private LocalTravelPreparationResult PrepareGroundAcceleration(LocalTravelObservation observation)
    {
        if (observation.AccelerationActive)
            return Ready(LocalTravelMode.Sprint, "AccelerationActive", "Sprinting to the destination.");

        if (accelerationRequested)
            return Ready(LocalTravelMode.Sprint, "AccelerationRequested", "Sprinting to the destination.");

        if (!accelerationRequested && observation.CanAccelerate && actions.TryAccelerate())
        {
            accelerationRequested = true;
            return Ready(LocalTravelMode.Sprint, "AccelerationRequested", "Sprinting to the destination.");
        }

        return Ready(LocalTravelMode.Walk, "WalkingFallback", "Walking to the destination because no faster local movement is available.");
    }

    private void ResetMountPreparation()
    {
        mountPreparationStartedAt = null;
        nextMountRequestAt = null;
    }

    private void ResetTakeoffPreparation()
    {
        takeoffPreparationStartedAt = null;
        nextTakeoffRequestAt = null;
    }

    private static LocalTravelPreparationResult Ready(LocalTravelMode mode, string code, string message) =>
        new(LocalTravelPreparationState.Ready, mode, code, message);

    private static LocalTravelPreparationResult Waiting(LocalTravelMode mode, string code, string message) =>
        new(LocalTravelPreparationState.Waiting, mode, code, message);

    private static LocalTravelPreparationResult Unavailable(LocalTravelMode mode, string code, string message) =>
        new(LocalTravelPreparationState.Unavailable, mode, code, message);
}

internal sealed record LocalTravelObservation(
    bool FlightUnlocked,
    bool Mounted,
    bool InFlight,
    bool MountTransition,
    bool Casting,
    bool AccelerationActive,
    bool MountAllowed,
    bool CanMount,
    bool CanTakeOff,
    bool CanDismount,
    bool CanAccelerate);

internal interface ILocalTravelActions
{
    LocalTravelObservation Observe();
    bool TryMount();
    bool TryTakeOff();
    bool TryDismount();
    bool TryAccelerate();
}

internal sealed unsafe class DalamudLocalTravelActions : ILocalTravelActions
{
    private const uint JumpGeneralActionId = 2;
    private const uint MountRouletteGeneralActionId = 9;
    private const uint DismountGeneralActionId = 23;
    private const uint SprintActionId = 3;
    private const uint PelotonActionId = 7557;
    private static readonly uint[] AccelerationStatusIds = [50, 1199, 4209];

    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly ICommandManager? commandManager;
    private readonly IDataManager? dataManager;
    private readonly IClientState? clientState;

    public DalamudLocalTravelActions(ICondition condition, IObjectTable objectTable)
    {
        this.condition = condition ?? throw new ArgumentNullException(nameof(condition));
        this.objectTable = objectTable ?? throw new ArgumentNullException(nameof(objectTable));
    }

    public DalamudLocalTravelActions(
        ICondition condition,
        IObjectTable objectTable,
        ICommandManager commandManager,
        IDataManager dataManager,
        IClientState clientState)
    {
        this.condition = condition ?? throw new ArgumentNullException(nameof(condition));
        this.objectTable = objectTable ?? throw new ArgumentNullException(nameof(objectTable));
        this.commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        this.dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        this.clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
    }

    public LocalTravelObservation Observe()
    {
        try
        {
            var playerState = PlayerState.Instance();
            var manager = ActionManager.Instance();
            var mounted = condition[ConditionFlag.Mounted];
            var canMount = manager != null && manager->GetActionStatus(ActionType.GeneralAction, MountRouletteGeneralActionId) == 0;
            var mountAllowed = dataManager is not null && clientState is not null
                ? dataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(clientState.TerritoryType)?.Mount ?? true
                : canMount;
            return new(
                FlightUnlocked: playerState != null && playerState->CanFly,
                Mounted: mounted,
                InFlight: condition[ConditionFlag.InFlight],
                MountTransition: condition[ConditionFlag.MountOrOrnamentTransition],
                Casting: condition[ConditionFlag.Casting],
                AccelerationActive: objectTable.LocalPlayer?.StatusList.Any(status =>
                    AccelerationStatusIds.Contains(status.StatusId)) == true,
                MountAllowed: mountAllowed,
                CanMount: canMount,
                CanTakeOff: mounted && Control.GetFlightAllowedStatus() == Control.FlightAllowedStatus.CanFly &&
                    manager != null && manager->GetActionStatus(ActionType.GeneralAction, JumpGeneralActionId) == 0,
                CanDismount: mounted && manager != null &&
                    manager->GetActionStatus(ActionType.GeneralAction, DismountGeneralActionId) == 0,
                CanAccelerate: manager != null &&
                    (manager->GetActionStatus(ActionType.Action, SprintActionId) == 0 ||
                     manager->GetActionStatus(ActionType.Action, PelotonActionId) == 0));
        }
        catch
        {
            return new(false, false, false, false, false, false, false, false, false, false, false);
        }
    }

    public bool TryMount() => TryUseGeneralAction(MountRouletteGeneralActionId);

    public bool TryTakeOff() => TryUseGeneralAction(JumpGeneralActionId);

    public bool TryDismount() => TryUseGeneralAction(DismountGeneralActionId);

    public bool TryAccelerate()
    {
        try
        {
            var manager = ActionManager.Instance();
            if (manager == null)
                return false;
            if (manager->GetActionStatus(ActionType.Action, SprintActionId) == 0)
                return manager->UseAction(ActionType.Action, SprintActionId);
            return manager->GetActionStatus(ActionType.Action, PelotonActionId) == 0 &&
                   manager->UseAction(ActionType.Action, PelotonActionId);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryUse(ActionType type, uint id)
    {
        try
        {
            var manager = ActionManager.Instance();
            return manager != null && manager->GetActionStatus(type, id) == 0 && manager->UseAction(type, id);
        }
        catch
        {
            return false;
        }
    }

    private bool TryUseGeneralAction(uint id)
    {
        try
        {
            var manager = ActionManager.Instance();
            if (manager == null || manager->GetActionStatus(ActionType.GeneralAction, id) != 0)
                return false;
            if (commandManager is null || dataManager is null)
                return TryUse(ActionType.GeneralAction, id);
            var name = dataManager.GetExcelSheet<GeneralAction>().GetRowOrDefault(id)?.Name.ToString();
            return !string.IsNullOrWhiteSpace(name) &&
                   commandManager.ProcessCommand($"/generalaction \"{EscapeArgument(name)}\"");
        }
        catch
        {
            return false;
        }
    }

    private static string EscapeArgument(string value) => value.Replace("\"", string.Empty, StringComparison.Ordinal);
}
