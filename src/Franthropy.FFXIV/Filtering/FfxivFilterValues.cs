namespace Franthropy.FFXIV.Filtering;

public readonly record struct FfxivItemKey(uint RowId);
public readonly record struct FfxivJobKey(uint RowId);
public readonly record struct FfxivUiCategoryKey(uint RowId);
public readonly record struct FfxivCharacterKey(ulong ContentId);
public readonly record struct FfxivRetainerKey(ulong RetainerId);
public readonly record struct FfxivWorldKey(uint RowId);
public readonly record struct FfxivDataCenterKey(uint RowId);

public enum FfxivItemQuality { NQ, HQ }

public enum FfxivEquipmentSlot
{
    MainHand, OffHand, Head, Body, Hands, Legs, Feet, Ears, Neck, Wrists, Ring, SoulCrystal,
}

public enum FfxivItemRarity { Common, Uncommon, Rare, Relic, Event }

public enum FfxivStorageLocation
{
    Inventory, Armoury, Equipped, Retainer, Saddlebag, GlamourDresser, Armoire,
}

public enum FfxivOfferSource { Vendor, Market, Exchange }

public enum FfxivAcquisitionSource
{
    Vendor, Market, Craft, Gather, Retainer, Quest, Duty, Drop, Exchange,
}

public enum FfxivRegion { NorthAmerica, Europe, Japan, Oceania, China, Korea }
