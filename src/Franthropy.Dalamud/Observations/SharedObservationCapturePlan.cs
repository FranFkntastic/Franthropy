using Franthropy.Observations.V1;

namespace Franthropy.Dalamud.Observations;

internal sealed record SharedObservationCapturePlan(
    bool PlayerInventory,
    bool Saddlebag,
    bool RetainerInventory,
    bool RetainerListings)
{
    public static SharedObservationCapturePlan Create(
        bool hasPlayerInventoryChanges,
        bool hasSaddlebagChanges,
        bool hasRetainerInventoryChanges,
        bool hasRetainerListingChanges,
        ObservationOwner? owner,
        ulong? retainerId)
    {
        var hasOwner = owner is not null;
        var hasRetainer = hasOwner && retainerId is > 0;
        return new SharedObservationCapturePlan(
            hasOwner && hasPlayerInventoryChanges,
            hasOwner && hasSaddlebagChanges,
            hasRetainer && hasRetainerInventoryChanges,
            hasRetainer && hasRetainerListingChanges);
    }

    public void Execute(
        Action capturePlayerInventory,
        Action captureSaddlebag,
        Action captureRetainerInventory,
        Action captureRetainerListings,
        Action<Exception> reportFailure)
    {
        try
        {
            if (PlayerInventory)
                capturePlayerInventory();
            if (Saddlebag)
                captureSaddlebag();
            if (RetainerInventory)
                captureRetainerInventory();
            if (RetainerListings)
                captureRetainerListings();
        }
        catch (Exception ex)
        {
            reportFailure(ex);
        }
    }
}
