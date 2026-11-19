using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Automation.Trades;

namespace Franthropy.Dalamud.Tests.Automation.Trades;

public unsafe class TradeOfferedInventoryTests
{
    [Fact]
    public void ReadsSourceIdentityButOfferQuantityWithoutAnyNativeItemGetters()
    {
        // Null vtables deliberately prohibit the old symbolic native getters.
        var slots = stackalloc InventoryItem[6];
        new Span<InventoryItem>(slots, 6).Clear();
        slots[0].IsSymbolic = true;
        slots[0].LinkedInventoryType = (ushort)InventoryType.Inventory1;
        slots[0].LinkedItemSlot = 0;
        slots[0].Quantity = 3;
        slots[5].Quantity = 1_000_000;
        var source = new InventoryItem { ItemId = 42, Quantity = 999, Flags = InventoryItem.ItemFlags.HighQuality };
        var inventory = new InventoryContainer { Items = &source, Size = 1, IsLoaded = true };
        var address = (nint)(&inventory);
        var container = new InventoryContainer { Items = slots, Size = 6, IsLoaded = true };

        var offer = DalamudTradeIo.ReadOfferedInventory(&container, 123, true, type =>
        {
            Assert.Equal(InventoryType.Inventory1, type);
            return address;
        });

        Assert.NotNull(offer);
        Assert.Equal(123u, offer.PartnerEntityId);
        Assert.True(offer.SyncPending);
        Assert.Equal(1_000_000, offer.Gil);
        Assert.Equal(new TradeOfferedItem(42, true, 3, (uint)InventoryType.Inventory1, 0), Assert.Single(offer.Items));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unloaded")]
    [InlineData("storage")]
    [InlineData("slot")]
    [InlineData("symbolic")]
    [InlineData("empty")]
    [InlineData("resized")]
    [InlineData("container")]
    public void InvalidSourceIsUnavailableWithoutFollowingFurtherLinks(string failure)
    {
        var slots = stackalloc InventoryItem[6];
        new Span<InventoryItem>(slots, 6).Clear();
        slots[0].IsSymbolic = true;
        slots[0].LinkedInventoryType = (ushort)(failure == "container" ? InventoryType.Currency : InventoryType.Inventory1);
        slots[0].LinkedItemSlot = failure == "slot" ? (ushort)1 : (ushort)0;
        slots[0].Quantity = 3;
        var source = new InventoryItem
        {
            ItemId = failure == "empty" ? 0u : 42u,
            Quantity = failure == "resized" ? 2 : 999,
            IsSymbolic = failure == "symbolic",
        };
        var inventory = new InventoryContainer
        {
            Items = failure == "storage" ? null : &source, Size = 1, IsLoaded = failure != "unloaded",
        };
        var address = failure == "missing" ? 0 : (nint)(&inventory);
        var container = new InventoryContainer { Items = slots, Size = 6, IsLoaded = true };
        var calls = 0;
        Assert.Null(DalamudTradeIo.ReadOfferedInventory(&container, 123, false, _ => { calls++; return address; }));
        Assert.InRange(calls, 0, 1);
    }

}
