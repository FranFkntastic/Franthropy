using FFXIVClientStructs.FFXIV.Client.Game;

namespace Franthropy.Dalamud.Automation.Inventory;

public sealed record DalamudInventoryStack(
    InventoryType Container,
    int SlotIndex,
    uint ItemId,
    int Quantity,
    bool IsHighQuality = false,
    bool IsSymbolic = false,
    InventoryItem.ItemFlags Flags = InventoryItem.ItemFlags.None,
    string Metadata = "")
{
    public bool IsOrdinary => ItemId != 0 && Quantity > 0 && !IsSymbolic &&
        Flags is InventoryItem.ItemFlags.None or InventoryItem.ItemFlags.HighQuality;
    public bool CompatibleWith(DalamudInventoryStack other) => IsOrdinary && other.IsOrdinary &&
        ItemId == other.ItemId && Flags == other.Flags && CompatibilityMetadata == other.CompatibilityMetadata;
    // The researched ordinary merge compares flag-adjusted ID/HQ, not signature.
    // Ignore ONLY crafter identity; retain every other captured metadata byte.
    public string CompatibilityMetadata => Metadata.Length == 0x60 ? Metadata.Remove(16, 16) : Metadata;
}

public sealed record DalamudInventoryRead(
    bool IsComplete,
    string Message,
    IReadOnlyList<DalamudInventoryStack> Stacks);

/// <summary>
/// Reads exact live inventory stacks without applying product-specific grouping or policy.
/// </summary>
public static class DalamudInventoryStackScanner
{
    /// <summary>Reads every requested container or returns unknown, never a partial inventory presented as complete.</summary>
    public static unsafe DalamudInventoryRead ReadComplete(IReadOnlyList<InventoryType> inventoryTypes)
    {
        ArgumentNullException.ThrowIfNull(inventoryTypes);
        if (inventoryTypes.Count == 0)
            return new(false, "No inventory containers were requested.", []);
        var manager = InventoryManager.Instance();
        if (manager == null)
            return new(false, "The native inventory manager is unavailable.", []);
        var stacks = new List<DalamudInventoryStack>();
        foreach (var type in inventoryTypes.Distinct())
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded || container->Size <= 0)
                return new(false, $"Inventory container {type} is not loaded.", []);
            for (var index = 0; index < container->Size; index++)
            {
                var slot = container->GetInventorySlot(index);
                if (slot == null)
                    return new(false, $"Inventory slot {type}/{index} is unavailable.", []);
                stacks.Add(new(type, index, slot->ItemId, checked((int)slot->Quantity),
                    slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality), slot->IsSymbolic, slot->Flags,
                    Convert.ToHexString(new ReadOnlySpan<byte>((byte*)slot + 0x18, 0x30))));
            }
        }
        return new(true, "All requested inventory containers were read.", stacks);
    }

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
                    checked((int)slot->Quantity),
                    slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)));
            }
        }

        return stacks;
    }

    public static unsafe int CountLoadedItem(
        InventoryType inventoryType,
        uint itemId,
        bool? isHighQuality = null)
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
            if (slot != null &&
                slot->ItemId == itemId &&
                (isHighQuality is null ||
                 slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) == isHighQuality))
            {
                quantity += checked((int)slot->Quantity);
            }
        }

        return quantity;
    }
}
