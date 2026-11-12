using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Automation.Inventory;

namespace Franthropy.Dalamud.Automation.Retainers;

public static class DalamudRetainerInventory
{
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
