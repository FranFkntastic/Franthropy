using Franthropy.Filtering.Semantics;

namespace Franthropy.FFXIV.Filtering;

public sealed class FfxivFilterCatalog
{
    public const string CurrentVersion = "1.1";

    private FfxivFilterCatalog(IFfxivFilterResolvers resolvers)
    {
        ArgumentNullException.ThrowIfNull(resolvers);
        ItemName = FilterFields.Named("item.name", resolvers.Items, "item", "Item", "Localized FFXIV item identity.", ["item", "name"], matchUsesFuzzyResolution: true);
        ItemLevel = FilterFields.Integer("item.itemLevel", "Item level", "FFXIV item level used for gear progression.", ["ilvl"], minimum: 0);
        EquipLevel = FilterFields.Integer("item.equipLevel", "Equip level", "Character level required to equip the item.", ["level", "lvl"], minimum: 0);
        ItemJobs = FilterFields.Set("item.job", resolvers.Jobs, "job", "Eligible jobs", "Jobs or classes eligible to use the item.", ["job", "class"]);
        ItemSlots = FilterFields.Set("item.slot", EnumResolver<FfxivEquipmentSlot>(), "equipment slot", "Equipment slots", "Semantic equipment slots occupied by the item.", ["slot"]);
        ItemRarity = FilterFields.Enumeration<FfxivItemRarity>("item.rarity", "Rarity", "Normalized FFXIV item rarity.", ["rarity"]);
        ItemUiCategory = FilterFields.Named("item.uiCategory", resolvers.UiCategories, "item category", "UI category", "User-facing FFXIV item UI category.", ["category"]);
        ItemUnique = FilterFields.Boolean("item.unique", "Unique", "Whether the FFXIV item definition is unique.", ["unique"]);
        ItemTradable = FilterFields.Boolean("item.tradable", "Tradable", "Whether the item definition permits trade or market listing.", ["tradable"]);
        ItemDesynthesizable = FilterFields.Boolean("item.desynthesizable", "Desynthesizable", "Whether the item definition permits desynthesis.", ["desynth"]);
        InstanceQuality = FilterFields.Enumeration<FfxivItemQuality>("instance.quality", "Quality", "Observed NQ or HQ item quality.", ["quality"]);
        InstanceQuantity = FilterFields.Integer("instance.quantity", "Stack quantity", "Quantity in one observed physical stack.", minimum: 0);
        InstanceLocation = FilterFields.Enumeration<FfxivStorageLocation>("instance.location", "Location", "Semantic storage location of an observed item instance.", ["location"]);
        InstanceEquipped = FilterFields.Boolean("instance.equipped", "Equipped", "Whether the observed item instance is currently equipped.");
        InstanceCondition = FilterFields.Decimal("instance.condition", "Condition", "Observed item condition as a percentage from 0 through 100.", ["condition"], 0, 100);
        InstanceSpiritbond = FilterFields.Decimal("instance.spiritbond", "Spiritbond", "Observed spiritbond as a percentage from 0 through 100.", ["spiritbond"], 0, 100);
        OwnershipOwned = FilterFields.Boolean("ownership.owned", "Owned", "Whether at least one instance exists in a complete active ownership scope.", ["owned"]);
        OwnershipQuantity = FilterFields.Integer("ownership.quantity", "Owned quantity", "Total quantity across the active ownership scope.", minimum: 0);
        OwnershipCharacters = FilterFields.Set("ownership.character", resolvers.Characters, "character", "Characters", "Characters contributing ownership evidence.", ["character"]);
        OwnershipRetainers = FilterFields.Set("ownership.retainer", resolvers.Retainers, "retainer", "Retainers", "Retainers contributing ownership evidence.", ["retainer"]);
        OfferSource = FilterFields.Enumeration<FfxivOfferSource>("offer.source", "Offer source", "Source of one represented actionable purchase offer.");
        OfferPrice = FilterFields.Decimal("offer.price", "Unit price", "Unit purchase price in gil.", ["price"], minimum: 0);
        OfferTotalPrice = FilterFields.Decimal("offer.totalPrice", "Total price", "Total cost of the represented offer quantity in gil.", ["totalPrice"], minimum: 0);
        OfferQuantity = FilterFields.Integer("offer.quantity", "Offer quantity", "Quantity available in one represented purchase offer.", minimum: 0);
        OfferWorld = FilterFields.Named("offer.world", resolvers.Worlds, "world", "World", "FFXIV world containing the purchase offer.", ["world"]);
        OfferDataCenter = FilterFields.Named("offer.dataCenter", resolvers.DataCenters, "data center", "Data center", "FFXIV data center containing the purchase offer.", ["dataCenter"]);
        OfferRegion = FilterFields.Enumeration<FfxivRegion>("offer.region", "Region", "FFXIV region containing the purchase offer.", ["region"], RegionAliases);
        OfferAge = FilterFields.Duration("offer.age", "Evidence age", "Elapsed time since price and availability were observed.", ["age"]);
        AcquisitionSources = FilterFields.Set("acquisition.source", EnumResolver<FfxivAcquisitionSource>(), "acquisition source", "Acquisition sources", "Known ways the item can be obtained.");
        Fields =
        [
            ItemName, ItemLevel, EquipLevel, ItemJobs, ItemSlots, ItemRarity, ItemUiCategory, ItemUnique, ItemTradable,
            ItemDesynthesizable, InstanceQuality, InstanceQuantity, InstanceLocation, InstanceEquipped, InstanceCondition,
            InstanceSpiritbond, OwnershipOwned, OwnershipQuantity, OwnershipCharacters, OwnershipRetainers, OfferSource,
            OfferPrice, OfferTotalPrice, OfferQuantity, OfferWorld, OfferDataCenter, OfferRegion, OfferAge, AcquisitionSources,
        ];
        Catalog = new FilterCatalog(Fields, CurrentVersion,
        [
            new("is", "equipped", InstanceEquipped.Key, "true", "Item is currently equipped."),
            new("is", "hq", InstanceQuality.Key, nameof(FfxivItemQuality.HQ), "Item is high quality."),
            new("is", "nq", InstanceQuality.Key, nameof(FfxivItemQuality.NQ), "Item is normal quality."),
        ]);
    }

    private static readonly IReadOnlyDictionary<string, FfxivRegion> RegionAliases =
        new Dictionary<string, FfxivRegion>
        {
            ["North America"] = FfxivRegion.NorthAmerica,
            ["NA"] = FfxivRegion.NorthAmerica,
            ["EU"] = FfxivRegion.Europe,
            ["JP"] = FfxivRegion.Japan,
            ["OC"] = FfxivRegion.Oceania,
        };

    public FilterCatalog Catalog { get; }
    public IReadOnlyList<FilterField> Fields { get; }
    public FilterField<FfxivItemKey> ItemName { get; }
    public FilterField<long> ItemLevel { get; }
    public FilterField<long> EquipLevel { get; }
    public FilterSetField<FfxivJobKey> ItemJobs { get; }
    public FilterSetField<FfxivEquipmentSlot> ItemSlots { get; }
    public FilterField<FfxivItemRarity> ItemRarity { get; }
    public FilterField<FfxivUiCategoryKey> ItemUiCategory { get; }
    public FilterField<bool> ItemUnique { get; }
    public FilterField<bool> ItemTradable { get; }
    public FilterField<bool> ItemDesynthesizable { get; }
    public FilterField<FfxivItemQuality> InstanceQuality { get; }
    public FilterField<long> InstanceQuantity { get; }
    public FilterField<FfxivStorageLocation> InstanceLocation { get; }
    public FilterField<bool> InstanceEquipped { get; }
    public FilterField<decimal> InstanceCondition { get; }
    public FilterField<decimal> InstanceSpiritbond { get; }
    public FilterField<bool> OwnershipOwned { get; }
    public FilterField<long> OwnershipQuantity { get; }
    public FilterSetField<FfxivCharacterKey> OwnershipCharacters { get; }
    public FilterSetField<FfxivRetainerKey> OwnershipRetainers { get; }
    public FilterField<FfxivOfferSource> OfferSource { get; }
    public FilterField<decimal> OfferPrice { get; }
    public FilterField<decimal> OfferTotalPrice { get; }
    public FilterField<long> OfferQuantity { get; }
    public FilterField<FfxivWorldKey> OfferWorld { get; }
    public FilterField<FfxivDataCenterKey> OfferDataCenter { get; }
    public FilterField<FfxivRegion> OfferRegion { get; }
    public FilterField<TimeSpan> OfferAge { get; }
    public FilterSetField<FfxivAcquisitionSource> AcquisitionSources { get; }

    public static FfxivFilterCatalog Create(IFfxivFilterResolvers resolvers) => new(resolvers);

    private static FilterNamedValueCatalog<TEnum> EnumResolver<TEnum>() where TEnum : struct, Enum =>
        new(Enum.GetValues<TEnum>().Select(value => new FilterLiteralCandidate<TEnum>(value, value.ToString())));
}
