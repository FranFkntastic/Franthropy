using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Franthropy.Dalamud.Travel;

public enum AetheryteTravelSubmissionState
{
    Submitted,
    Busy,
    Unavailable,
    Rejected,
    InvalidRequest,
}

public sealed record AetheryteTravelSubmissionResult(
    AetheryteTravelSubmissionState State,
    string Code,
    string Message)
{
    public bool Submitted => State == AetheryteTravelSubmissionState.Submitted;
    public bool Retryable => State == AetheryteTravelSubmissionState.Busy;
}

/// <summary>
/// Product-neutral structured transport for Lifestream aetheryte travel.
/// Consumers own route selection and completion detection.
/// </summary>
public sealed class DalamudLifestreamAetheryteTravel
{
    public const string InternalName = "Lifestream";
    public const string IsBusyChannel = "Lifestream.IsBusy";
    public const string TeleportChannel = "Lifestream.Teleport";

    private readonly Func<bool> isAvailable;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<uint, byte, bool> teleport;

    public DalamudLifestreamAetheryteTravel(IDalamudPluginInterface pluginInterface)
        : this(
            () => pluginInterface.InstalledPlugins.Any(plugin =>
                plugin.IsLoaded &&
                string.Equals(plugin.InternalName, InternalName, StringComparison.OrdinalIgnoreCase)),
            pluginInterface.GetIpcSubscriber<bool>(IsBusyChannel),
            pluginInterface.GetIpcSubscriber<uint, byte, bool>(TeleportChannel))
    {
    }

    internal DalamudLifestreamAetheryteTravel(
        Func<bool> isAvailable,
        ICallGateSubscriber<bool> isBusy,
        ICallGateSubscriber<uint, byte, bool> teleport)
    {
        this.isAvailable = isAvailable;
        this.isBusy = isBusy;
        this.teleport = teleport;
    }

    public AetheryteTravelSubmissionResult TrySubmit(uint aetheryteId)
    {
        if (aetheryteId == 0)
            return Result(AetheryteTravelSubmissionState.InvalidRequest, "InvalidAetheryte", "A non-zero aetheryte id is required.");
        if (!isAvailable())
            return Result(AetheryteTravelSubmissionState.Unavailable, "LifestreamUnavailable", "Lifestream is not loaded.");

        try
        {
            if (isBusy.InvokeFunc())
                return Result(AetheryteTravelSubmissionState.Busy, "LifestreamBusy", "Lifestream is already handling travel.");

            return teleport.InvokeFunc(aetheryteId, 0)
                ? Result(AetheryteTravelSubmissionState.Submitted, "Submitted", "Lifestream accepted aetheryte travel.")
                : Result(AetheryteTravelSubmissionState.Rejected, "TravelRejected", "Lifestream rejected aetheryte travel from the current game state.");
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
