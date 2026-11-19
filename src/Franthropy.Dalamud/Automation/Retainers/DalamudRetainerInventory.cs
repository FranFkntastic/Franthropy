using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Inventory;

namespace Franthropy.Dalamud.Automation.Retainers;

public static class DalamudRetainerInventory
{
    internal const string InventoryLarge = "InventoryRetainerLarge";
    internal const string InventorySmall = "InventoryRetainer";
    internal static readonly IReadOnlyList<InventoryType> PlayerOrdinaryItemContainers =
        [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    public static readonly IReadOnlyList<InventoryType> OrdinaryItemContainers =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    public static readonly IReadOnlyList<InventoryType> ItemContainers =
    [
        .. OrdinaryItemContainers,
        InventoryType.RetainerCrystals,
    ];

    public static IReadOnlyList<DalamudInventoryStack> ScanLoadedStacks(IReadOnlySet<uint> itemIds) =>
        DalamudInventoryStackScanner.ScanLoadedStacks(ItemContainers, itemIds);

    // Framework-thread observation only. A hidden but still allocated SelectString is harmless.
    internal static unsafe (string Code, string Message)? CommandBlocker(IGameGui gui, bool crystals, out AgentInterface* agent)
    {
        agent = null;
        bool Ready(string name)
        {
            var addon = gui.GetAddonByName<AtkUnitBase>(name, 1);
            return addon != null && addon->IsReady && addon->IsVisible;
        }

        if (!Ready(InventoryLarge) && !Ready(InventorySmall))
            return ("RetainerInventoryUnavailable", "Retainer inventory window is not ready.");
        var menu = gui.GetAddonByName<AtkUnitBase>("SelectString", 1);
        if (menu != null && menu->IsVisible)
            return ("RetainerInventoryUnavailable", "Retainer command menu is still visible.");
        if (RetainerDepositVerifier.QuantityPopupVisible(gui))
            return ("QuantityPopupAlreadyOpen", "An unowned quantity popup exists before dispatch.");
        var module = AgentModule.Instance();
        agent = module == null ? null : module->GetAgentByInternalId(AgentId.Retainer);
        if (agent == null || !agent->IsAgentActive())
            return ("RetainerAgentUnavailable", "Retainer agent is not active.");

        var manager = InventoryManager.Instance();
        if (manager == null)
            return ("InventoryUnavailable", "Inventory manager is unavailable.");
        bool Loaded(InventoryType type)
        {
            var container = manager->GetInventoryContainer(type);
            return container != null && container->IsLoaded;
        }

        if (crystals)
            return Loaded(InventoryType.Crystals) && Loaded(InventoryType.RetainerCrystals)
                ? null : ("InventoryUnavailable", "Crystal containers are not loaded.");
        foreach (var type in PlayerOrdinaryItemContainers)
            if (!Loaded(type))
                return ("InventoryUnavailable", $"Player bag {type} is not loaded.");
        foreach (var type in OrdinaryItemContainers)
            if (!Loaded(type))
                return ("RetainerInventoryUnavailable", $"Retainer page {type} is not loaded.");
        return null;
    }

    internal static unsafe string? OpenBlocker(IGameGui gui) =>
        CommandBlocker(gui, crystals: false, out _)?.Message;

    /// <summary>Complete ordinary-page coverage is required before retained values authorize stock use.</summary>
    public static bool HasOrdinaryCoverage(IEnumerable<string> observedSources) => OrdinaryItemContainers
        .All(type => observedSources.Contains(type.ToString(), StringComparer.Ordinal));

    public static bool HasCrystalCoverage(IEnumerable<string> observedSources) =>
        observedSources.Contains(nameof(InventoryType.RetainerCrystals), StringComparer.Ordinal);

    public static bool HasCoverageForItem(IEnumerable<string> observedSources, uint itemId) =>
        ElementalCurrencyCatalog.IsElementalCurrency(itemId)
            ? HasCrystalCoverage(observedSources) : HasOrdinaryCoverage(observedSources);

    public static unsafe int CountAvailableRetainers()
    {
        var manager = RetainerManager.Instance();
        if (manager is null)
            return 0;

        var count = 0;
        for (var index = 0; index < manager->GetRetainerCount(); index++)
            if (manager->Retainers[index].Available)
                count++;

        return count;
    }
}
