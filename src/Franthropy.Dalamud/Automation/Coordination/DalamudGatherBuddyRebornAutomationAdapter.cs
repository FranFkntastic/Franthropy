using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Franthropy.Dalamud.Automation.Coordination;

public sealed class DalamudGatherBuddyRebornAutomationAdapter : IInterruptibleAutomationAdapter
{
    private const string IsEnabledChannel = "GatherBuddyReborn.IsAutoGatherEnabled";
    private const string SetEnabledChannel = "GatherBuddyReborn.SetAutoGatherEnabled";
    private static readonly string[] InternalNames = ["GatherBuddyReborn", "GatherBuddy"];

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    public DalamudGatherBuddyRebornAutomationAdapter(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    public string DisplayName => "GatherBuddy Reborn";

    public bool IsAvailable => pluginInterface.InstalledPlugins.Any(plugin =>
        plugin.IsLoaded && InternalNames.Contains(plugin.InternalName, StringComparer.OrdinalIgnoreCase));

    public bool TryGetRunning(out bool isRunning, out string? error)
    {
        try
        {
            isRunning = pluginInterface.GetIpcSubscriber<bool>(IsEnabledChannel).InvokeFunc();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Franthropy] GatherBuddy Reborn running-state IPC failed.");
            isRunning = false;
            error = "GatherBuddy Reborn is loaded, but its running state could not be read.";
            return false;
        }
    }

    public bool TryInterrupt(out string? error)
    {
        try
        {
            pluginInterface.GetIpcSubscriber<bool, object>(SetEnabledChannel).InvokeAction(false);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Franthropy] GatherBuddy Reborn interruption IPC failed.");
            error = "GatherBuddy Reborn is running, but it did not accept the interruption request.";
            return false;
        }
    }
}
