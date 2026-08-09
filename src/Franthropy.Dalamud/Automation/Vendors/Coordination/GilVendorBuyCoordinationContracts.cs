using Franthropy.Dalamud.Automation.Vendors;

namespace Franthropy.Dalamud.Automation.Vendors.Coordination;

public interface IGilVendorBuyRunStore
{
    GilVendorBuyRunSnapshot? LoadCurrent();
    void Save(GilVendorBuyRunSnapshot snapshot);
}

public interface IGilVendorBuyRuntime
{
    GilVendorInventorySnapshot CaptureInventory(IReadOnlyCollection<uint> itemIds);
    bool HasCapacity(IReadOnlyDictionary<uint, int> quantities, out string message);
    GilVendorReachResult AdvanceToOpenShop(GilVendorOffer offer);
    void ResetVendorApproach();
    GilVendorShopReadResult ReadShopRows();
    bool TrySubmitPurchase(GilVendorShopRow row, uint quantity, out string error);
    bool TryConfirmPurchasePrompt();
    int ResolveMaximumBatch(uint itemId);
    void CloseShop();
    void BeginAutomation();
    void EndAutomation();
}
