using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Diagnostics;

namespace Franthropy.Dalamud.Automation.Retainers;

internal static class RetainerNativeCommandCapability
{
    private const string Signature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 48 8B 5C 24 ?? 41 8B F0";

    public static nint Resolve(ISigScanner scanner)
    {
        NativeCapabilityGuard.RequireEmbeddedFieldLayout(Marshal.SizeOf<AgentRetainer>(),
            checked((int)Marshal.OffsetOf<AgentRetainer>(nameof(AgentRetainer.InventoryContextEvent))),
            Marshal.SizeOf<AgentInventoryContext.InventoryContextEvent>(), "Retainer inventory context");
        NativeCapabilityGuard.RequireAddress(InventoryManager.Addresses.GetInventoryContainer.Value, "Inventory containers");
        NativeCapabilityGuard.RequireAddress(AgentModule.Addresses.GetAgentByInternalId.Value, "Retainer agent lookup");
        NativeCapabilityGuard.RequireAddress(AtkUnitBase.Addresses.FireCallback.Value, "Retainer quantity callbacks");
        return NativeCapabilityGuard.ResolveUnique(scanner, Signature, "Retainer inventory command");
    }

    public static unsafe nint Context(AgentInterface* agent) => (nint)(&((AgentRetainer*)agent)->InventoryContextEvent);
}
