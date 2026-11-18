namespace Franthropy.Dalamud.Automation.Retainers;

/// <summary>
/// Commands 0/1 move the whole stack immediately, except for multi-unit crystals,
/// which prompt for a quantity. Commands 3/4 prompt for multi-unit stacks;
/// singletons never prompt. Other native verbs are not used for transfers.
/// </summary>
internal enum RetainerInventoryCommand : long
{
    RetrieveFromRetainer = 0,
    EntrustToRetainer = 1,
    RetrieveQuantity = 3,
    EntrustQuantity = 4,
}

internal enum RetainerTransferDirection { Retrieve, Entrust }

internal static class RetainerInventoryCommandPolicy
{
    public static (RetainerInventoryCommand Command, bool NeedsQuantityInput) Select(
        RetainerTransferDirection direction, bool isCrystalContainer, int sourceQuantity, int requestedQuantity)
    {
        var quantityCommand = !isCrystalContainer && sourceQuantity > 1 && requestedQuantity < sourceQuantity;
        var command = direction switch
        {
            RetainerTransferDirection.Retrieve => quantityCommand
                ? RetainerInventoryCommand.RetrieveQuantity : RetainerInventoryCommand.RetrieveFromRetainer,
            RetainerTransferDirection.Entrust => quantityCommand
                ? RetainerInventoryCommand.EntrustQuantity : RetainerInventoryCommand.EntrustToRetainer,
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };
        return (command, sourceQuantity > 1 && (isCrystalContainer || quantityCommand));
    }
}
