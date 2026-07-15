using FFXIVClientStructs.FFXIV.Client.Game;

namespace Franthropy.Dalamud.Automation.Inventory;

public sealed record DalamudInventoryStack(
    InventoryType Container,
    int SlotIndex,
    uint ItemId,
    int Quantity);

/// <summary>
/// Reads exact live inventory stacks without applying product-specific grouping or policy.
/// </summary>
public static class DalamudInventoryStackScanner
{
    public static unsafe IReadOnlyList<DalamudInventoryStack> ScanLoadedStacks(
        IReadOnlyList<InventoryType> inventoryTypes,
        IReadOnlySet<uint>? itemIds = null)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return [];

        var stacks = new List<DalamudInventoryStack>();
        foreach (var inventoryType in inventoryTypes)
        {
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null || slot->ItemId == 0 || slot->Quantity == 0)
                    continue;
                if (itemIds != null && !itemIds.Contains(slot->ItemId))
                    continue;

                stacks.Add(new DalamudInventoryStack(
                    inventoryType,
                    slotIndex,
                    slot->ItemId,
                    checked((int)slot->Quantity)));
            }
        }

        return stacks;
    }

    public static unsafe int CountLoadedItem(InventoryType inventoryType, uint itemId)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return 0;

        var container = inventoryManager->GetInventoryContainer(inventoryType);
        if (container == null || !container->IsLoaded)
            return 0;

        var quantity = 0;
        for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
        {
            var slot = container->GetInventorySlot(slotIndex);
            if (slot != null && slot->ItemId == itemId)
                quantity += checked((int)slot->Quantity);
        }

        return quantity;
    }
}
