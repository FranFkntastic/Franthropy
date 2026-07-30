using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Franthropy.Dalamud.Travel;

public enum VNavmeshLifecycleState
{
    Unavailable,
    Loading,
    Ready,
    Running,
    IpcFailure,
}

public sealed record VNavmeshLifecycleObservation(
    VNavmeshLifecycleState State,
    string Code,
    string Message)
{
    public bool CanSubmitPath => State == VNavmeshLifecycleState.Ready;
    public bool IsRunning => State == VNavmeshLifecycleState.Running;
    public bool IsTransient => State == VNavmeshLifecycleState.Loading;
}

public enum VNavmeshPathSubmissionState
{
    Submitted,
    Loading,
    Unavailable,
    Rejected,
    IpcFailure,
}

public sealed record VNavmeshPathSubmissionResult(
    VNavmeshPathSubmissionState State,
    string Code,
    string Message)
{
    public bool Submitted => State == VNavmeshPathSubmissionState.Submitted;
    public bool Retryable => State == VNavmeshPathSubmissionState.Loading;
}

/// <summary>
/// Product-neutral vnavmesh lifecycle and path-submission boundary. Installation, transient
/// navmesh loading, an active path, and IPC failure remain distinct states.
/// </summary>
public sealed class DalamudVNavmeshTravel
{
    public const string InternalName = "vnavmesh";
    public const string NavIsReadyChannel = "vnavmesh.Nav.IsReady";
    public const string PathIsRunningChannel = "vnavmesh.Path.IsRunning";
    public const string PathStopChannel = "vnavmesh.Path.Stop";
    public const string PathSetMovementAllowedChannel = "vnavmesh.Path.SetMovementAllowed";
    public const string MoveCloseToChannel = "vnavmesh.SimpleMove.PathfindAndMoveCloseTo";

    private readonly Func<bool> isAvailable;
    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<bool> isRunning;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<object> stop;
    private readonly ICallGateSubscriber<bool, object> setMovementAllowed;

    public DalamudVNavmeshTravel(IDalamudPluginInterface pluginInterface)
        : this(
            () => pluginInterface.InstalledPlugins.Any(plugin =>
                plugin.IsLoaded &&
                string.Equals(plugin.InternalName, InternalName, StringComparison.OrdinalIgnoreCase)),
            pluginInterface.GetIpcSubscriber<bool>(NavIsReadyChannel),
            pluginInterface.GetIpcSubscriber<bool>(PathIsRunningChannel),
            pluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>(MoveCloseToChannel),
            pluginInterface.GetIpcSubscriber<object>(PathStopChannel),
            pluginInterface.GetIpcSubscriber<bool, object>(PathSetMovementAllowedChannel))
    {
    }

    internal DalamudVNavmeshTravel(
        Func<bool> isAvailable,
        ICallGateSubscriber<bool> isReady,
        ICallGateSubscriber<bool> isRunning,
        ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo,
        ICallGateSubscriber<object> stop,
        ICallGateSubscriber<bool, object> setMovementAllowed)
    {
        this.isAvailable = isAvailable;
        this.isReady = isReady;
        this.isRunning = isRunning;
        this.moveCloseTo = moveCloseTo;
        this.stop = stop;
        this.setMovementAllowed = setMovementAllowed;
    }

    public VNavmeshLifecycleObservation Observe()
    {
        if (!isAvailable())
            return Observe(VNavmeshLifecycleState.Unavailable, "VNavmeshUnavailable", "vnavmesh is not loaded.");

        try
        {
            if (isRunning.InvokeFunc())
                return Observe(VNavmeshLifecycleState.Running, "PathRunning", "vnavmesh is following a path.");
            if (isReady.InvokeFunc())
                return Observe(VNavmeshLifecycleState.Ready, "Ready", "vnavmesh is ready.");
            return Observe(VNavmeshLifecycleState.Loading, "NavmeshLoading", "vnavmesh is loaded and waiting for the current territory navmesh.");
        }
        catch (Exception ex)
        {
            return Observe(VNavmeshLifecycleState.IpcFailure, "IpcFailure", ex.Message);
        }
    }

    public VNavmeshPathSubmissionResult TryMoveCloseTo(Vector3 destination, float range)
    {
        var lifecycle = Observe();
        if (lifecycle.State == VNavmeshLifecycleState.Loading)
            return Submit(VNavmeshPathSubmissionState.Loading, lifecycle.Code, lifecycle.Message);
        if (lifecycle.State == VNavmeshLifecycleState.Unavailable)
            return Submit(VNavmeshPathSubmissionState.Unavailable, lifecycle.Code, lifecycle.Message);
        if (lifecycle.State == VNavmeshLifecycleState.IpcFailure)
            return Submit(VNavmeshPathSubmissionState.IpcFailure, lifecycle.Code, lifecycle.Message);
        if (lifecycle.State == VNavmeshLifecycleState.Running)
            return Submit(VNavmeshPathSubmissionState.Rejected, "PathAlreadyRunning", "vnavmesh is already following a path.");

        try
        {
            return moveCloseTo.InvokeFunc(destination, false, range)
                ? Submit(VNavmeshPathSubmissionState.Submitted, "Submitted", "vnavmesh accepted the path.")
                : Submit(VNavmeshPathSubmissionState.Rejected, "PathRejected", "vnavmesh rejected the path.");
        }
        catch (Exception ex)
        {
            return Submit(VNavmeshPathSubmissionState.IpcFailure, "IpcFailure", ex.Message);
        }
    }

    public bool TryStop()
    {
        if (!isAvailable())
            return false;
        try
        {
            stop.InvokeAction();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TrySetMovementAllowed(bool allowed)
    {
        if (!isAvailable())
            return false;
        try
        {
            setMovementAllowed.InvokeAction(allowed);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static VNavmeshLifecycleObservation Observe(
        VNavmeshLifecycleState state,
        string code,
        string message) =>
        new(state, code, message);

    private static VNavmeshPathSubmissionResult Submit(
        VNavmeshPathSubmissionState state,
        string code,
        string message) =>
        new(state, code, message);
}
