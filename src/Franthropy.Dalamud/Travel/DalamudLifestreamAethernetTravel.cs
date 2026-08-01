using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Franthropy.Dalamud.Travel;

/// <summary>
/// Product-neutral structured transport for Lifestream aethernet travel.
/// Callers must first place the character within range of the destination network.
/// </summary>
public sealed class DalamudLifestreamAethernetTravel
{
    public const string InternalName = "Lifestream";
    public const string IsBusyChannel = "Lifestream.IsBusy";
    public const string TeleportChannel = "Lifestream.AethernetTeleportById";

    private readonly Func<bool> isAvailable;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<uint, bool> teleport;

    public DalamudLifestreamAethernetTravel(IDalamudPluginInterface pluginInterface)
        : this(
            () => pluginInterface.InstalledPlugins.Any(plugin =>
                plugin.IsLoaded &&
                string.Equals(plugin.InternalName, InternalName, StringComparison.OrdinalIgnoreCase)),
            pluginInterface.GetIpcSubscriber<bool>(IsBusyChannel),
            pluginInterface.GetIpcSubscriber<uint, bool>(TeleportChannel))
    {
    }

    internal DalamudLifestreamAethernetTravel(
        Func<bool> isAvailable,
        ICallGateSubscriber<bool> isBusy,
        ICallGateSubscriber<uint, bool> teleport)
    {
        this.isAvailable = isAvailable;
        this.isBusy = isBusy;
        this.teleport = teleport;
    }

    public AetheryteTravelSubmissionResult TrySubmit(uint aethernetId)
    {
        if (aethernetId == 0)
            return Result(AetheryteTravelSubmissionState.InvalidRequest, "InvalidAethernet", "A non-zero aethernet id is required.");
        if (!isAvailable())
            return Result(AetheryteTravelSubmissionState.Unavailable, "LifestreamUnavailable", "Lifestream is not loaded.");

        try
        {
            if (isBusy.InvokeFunc())
                return Result(AetheryteTravelSubmissionState.Busy, "LifestreamBusy", "Lifestream is already handling travel.");

            return teleport.InvokeFunc(aethernetId)
                ? Result(AetheryteTravelSubmissionState.Submitted, "Submitted", "Lifestream accepted aethernet travel.")
                : Result(AetheryteTravelSubmissionState.Rejected, "TravelRejected", "Lifestream rejected aethernet travel from the current game state.");
        }
        catch (Exception ex)
        {
            return Result(AetheryteTravelSubmissionState.Unavailable, "IpcFailure", ex.Message);
        }
    }

    private static AetheryteTravelSubmissionResult Result(
        AetheryteTravelSubmissionState state,
        string code,
        string message) =>
        new(state, code, message);
}
