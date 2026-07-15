using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Automation.Inventory;

namespace Franthropy.Dalamud.Automation.Retainers;

public static class DalamudRetainerInventory
{
    public static readonly IReadOnlyList<InventoryType> ItemContainers =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
        InventoryType.RetainerCrystals,
    ];

    public static IReadOnlyList<DalamudInventoryStack> ScanLoadedStacks(IReadOnlySet<uint> itemIds) =>
        DalamudInventoryStackScanner.ScanLoadedStacks(ItemContainers, itemIds);
}
