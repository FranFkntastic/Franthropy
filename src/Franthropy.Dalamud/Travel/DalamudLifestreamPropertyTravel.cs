using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Franthropy.Dalamud.Travel;

public enum PrivateEstateTravelState
{
    Submitted,
    Busy,
    Unavailable,
    NoPrivateEstate,
    Rejected,
}

public sealed record PrivateEstateTravelResult(
    PrivateEstateTravelState State,
    string Code,
    string Message)
{
    public bool Submitted => State == PrivateEstateTravelState.Submitted;
    public bool Retryable => State == PrivateEstateTravelState.Busy;
}

/// <summary>
/// Product-neutral transport for Lifestream's private-estate shortcut.
/// Consumers own routing policy, completion detection, and follow-up interaction.
/// </summary>
public sealed class DalamudLifestreamPropertyTravel
{
    public const string IsBusyChannel = "Lifestream.IsBusy";
    public const string HasPrivateHouseChannel = "Lifestream.HasPrivateHouse";
    public const string TeleportToHomeChannel = "Lifestream.TeleportToHome";

    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<bool?> hasPrivateHouse;
    private readonly ICallGateSubscriber<bool> teleportToHome;
    private readonly Func<string?> playerBlocker;

    public DalamudLifestreamPropertyTravel(IDalamudPluginInterface pluginInterface)
        : this(
            pluginInterface.GetIpcSubscriber<bool>(IsBusyChannel),
            pluginInterface.GetIpcSubscriber<bool?>(HasPrivateHouseChannel),
            pluginInterface.GetIpcSubscriber<bool>(TeleportToHomeChannel),
            DalamudPlayerActionReadiness.GetBlocker)
    {
    }

    internal DalamudLifestreamPropertyTravel(
        ICallGateSubscriber<bool> isBusy,
        ICallGateSubscriber<bool?> hasPrivateHouse,
        ICallGateSubscriber<bool> teleportToHome,
        Func<string?>? playerBlocker = null)
    {
        this.isBusy = isBusy;
        this.hasPrivateHouse = hasPrivateHouse;
        this.teleportToHome = teleportToHome;
        this.playerBlocker = playerBlocker ?? (() => null);
    }

    public PrivateEstateTravelResult TrySubmit()
    {
        try
        {
            if (isBusy.InvokeFunc())
                return Result(PrivateEstateTravelState.Busy, "LifestreamBusy", "Lifestream is already handling travel.");

            var availability = hasPrivateHouse.InvokeFunc();
            // Unknown availability means Lifestream's housing data has not loaded yet; check again.
            if (availability is null)
                return Result(PrivateEstateTravelState.Busy, "PrivateEstateUnknown", "Lifestream has not determined private-estate availability yet.");
            if (!availability.Value)
                return Result(PrivateEstateTravelState.NoPrivateEstate, "NoPrivateEstate", "This character does not have a private estate.");
            if (playerBlocker() is { } blocker)
                return Result(PrivateEstateTravelState.Busy, "PlayerNotReady", blocker);

            return teleportToHome.InvokeFunc()
                ? Result(PrivateEstateTravelState.Submitted, "Submitted", "Lifestream accepted private-estate travel.")
                : Result(PrivateEstateTravelState.Rejected, "TravelRejected", "Lifestream did not accept private-estate travel.");
        }
        catch (Exception ex)
        {
            return Result(PrivateEstateTravelState.Unavailable, "IpcFailure", ex.Message);
        }
    }

    private static PrivateEstateTravelResult Result(
        PrivateEstateTravelState state,
        string code,
        string message) =>
        new(state, code, message);
}
