namespace Franthropy.Dalamud.Automation.Inventory;

public sealed record ExpertDeliveryListEntry(uint ItemId, uint Seals, int Index);

public static class ExpertDeliverySelection
{
    public static DalamudUiTransactionResult ValidateSubmittedRow(
        uint itemId,
        int rowIndex,
        IReadOnlyList<ExpertDeliveryListEntry> entries)
    {
        var row = entries.FirstOrDefault(entry => entry.Index == rowIndex);
        return row?.ItemId == itemId
            ? DalamudUiTransactionResult.Completed("ExpertDeliveryRowStillBound", "The submitted row still identifies the approved item.")
            : DalamudUiTransactionResult.Fail("ExpertDeliveryRowChanged", "The submitted Expert Delivery row no longer identifies the approved item.");
    }

    public static DalamudUiTransactionResult SelectExactItem(
        uint itemId,
        IReadOnlyList<ExpertDeliveryListEntry> entries,
        out ExpertDeliveryListEntry? selected)
    {
        selected = null;
        var matches = entries.Where(entry => entry.ItemId == itemId).ToArray();
        if (matches.Length == 0)
            return DalamudUiTransactionResult.Fail("ExpertDeliveryItemUnavailable", "The approved item is not offered by the visible Expert Delivery list.");
        if (matches.Length > 1)
            return DalamudUiTransactionResult.Fail("ExpertDeliveryItemAmbiguous", "The visible Expert Delivery list contains multiple rows for the approved item ID.");
        selected = matches[0];
        return DalamudUiTransactionResult.Completed("ExpertDeliveryItemResolved", "Resolved one unambiguous Expert Delivery row.");
    }
}
