using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Franthropy.Dalamud.Automation.Ui;

/// <summary>
/// Requests the game's own item-detail tooltip for one rendered node through
/// AtkTooltipManager.ShowTooltip, the same native entry point the game's own hover
/// handling uses once a hovered node has been resolved. No cursor movement, window
/// focus, input injection, or event synthesis is involved, and the call carries no
/// item data: the game resolves the equipped item itself. Display is never proof of
/// content — callers must accept success only from the rendered ItemDetail addon,
/// never from this call returning.
/// </summary>
public static unsafe class RenderedItemDetailTooltipRequest
{
    /// <summary>
    /// Asks the game to render its ItemDetail tooltip for the item equipped in
    /// <paramref name="equippedContainerIndex"/> (the EquippedItems container order,
    /// including the legacy belt index), attached to <paramref name="targetNode"/>.
    /// Returns false when the stage or node is unavailable; true only means the
    /// request was dispatched to the game's tooltip manager.
    /// </summary>
    public static bool TryShowEquippedItemTooltip(ushort parentAddonId, AtkResNode* targetNode, short equippedContainerIndex) =>
        TryShowInventoryItemTooltip(parentAddonId, targetNode, InventoryType.EquippedItems, equippedContainerIndex);

    /// <summary>
    /// Asks the game to render its ItemDetail tooltip for the item in
    /// <paramref name="inventoryType"/> slot <paramref name="slotIndex"/>, attached to
    /// <paramref name="targetNode"/>. Returns false when the stage or node is unavailable
    /// or the arguments are out of range; true only means the request was dispatched
    /// to the game's tooltip manager.
    /// </summary>
    public static bool TryShowInventoryItemTooltip(ushort parentAddonId, AtkResNode* targetNode, InventoryType inventoryType, short slotIndex) =>
        TryShowInventoryItemTooltip(parentAddonId, targetNode, (uint)inventoryType, slotIndex);

    /// <summary>
    /// Asks the game to render its ItemDetail tooltip for the item in the container
    /// identified by the raw agent type/id value <paramref name="typeOrId"/> at slot
    /// <paramref name="slotIndex"/>. The agent's container numbering does not always match
    /// the InventoryType enum; callers pick the correct value for their container.
    /// </summary>
    public static bool TryShowInventoryItemTooltip(ushort parentAddonId, AtkResNode* targetNode, uint typeOrId, short slotIndex)
    {
        if (targetNode == null || slotIndex < 0)
            return false;
        var stage = AtkStage.Instance();
        if (stage == null)
            return false;

        var args = stackalloc AtkTooltipManager.AtkTooltipArgs[1];
        args->Ctor();
        args->ItemArgs.InventoryType = (InventoryType)typeOrId;
        args->ItemArgs.Flag1 = 0;
        args->ItemArgs.BuyQuantity = -1;
        args->ItemArgs.Slot = slotIndex;
        args->ItemArgs.Kind = DetailKind.InventoryItem;
        stage->TooltipManager.ShowTooltip(AtkTooltipType.Item, parentAddonId, targetNode, args);
        return true;
    }

    /// <summary>Dismisses the tooltip owned by the given parent addon, if one is showing.</summary>
    public static void HideTooltip(ushort parentAddonId)
    {
        var stage = AtkStage.Instance();
        if (stage == null)
            return;
        stage->TooltipManager.HideTooltip(parentAddonId);
    }
}
