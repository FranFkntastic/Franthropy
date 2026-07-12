using Franthropy.Dalamud.Characters;

namespace Franthropy.Dalamud.Equipment;

public enum EquipmentSlot
{
    Unknown,
    MainHand,
    OffHand,
    Head,
    Body,
    Hands,
    Legs,
    Feet,
    Ears,
    Neck,
    Wrists,
    Ring,
    SoulCrystal,
}

public enum EquipmentStatSemantic
{
    Unknown,
    Strength, Dexterity, Vitality, Intelligence, Mind,
    CriticalHit, Determination, DirectHit, SkillSpeed, SpellSpeed, Tenacity, Piety,
    Craftsmanship, Control, CraftingPoints, Gathering, Perception, GatheringPoints,
    PhysicalDamage, MagicalDamage, PhysicalDefense, MagicalDefense,
    PiercingResistance,
}

public enum EquipmentDiscipline { Unknown, Combat, Crafter, Gatherer }
public enum EquipmentRarity { Unknown, Normal, Uncommon, Rare, Relic }
public enum ExpertDeliveryEligibility { Unknown, Ineligible, Eligible }

public sealed record EquipmentStatValue(uint BaseParamId, EquipmentStatSemantic Semantic, int Value, bool IsSpecial, string? SourceName = null);

public sealed record EquipmentStatProfile(
    IReadOnlyList<EquipmentStatValue> Parameters,
    int PhysicalDamage,
    int MagicalDamage,
    int PhysicalDefense,
    int MagicalDefense,
    bool IsComplete)
{
    public bool HasFunctionalStats => Parameters.Any(value => value.Value > 0) ||
        PhysicalDamage > 0 || MagicalDamage > 0 || PhysicalDefense > 0 || MagicalDefense > 0;
}

public sealed record EquipmentInstanceFingerprint(
    CharacterScope Character,
    string Container,
    int SlotIndex,
    uint ItemId,
    bool IsHighQuality,
    uint Quantity,
    ushort Condition,
    ushort Spiritbond,
    ulong? CrafterContentId,
    IReadOnlyList<uint> MateriaIds,
    uint? GlamourId,
    IReadOnlyList<byte> Stains);

public sealed record EquipmentInstanceSnapshot(
    EquipmentInstanceFingerprint Fingerprint,
    DateTimeOffset CapturedAt,
    bool IsEquipped);

public sealed record EquipmentItemDefinition(
    uint ItemId,
    string Name,
    uint EquipLevel,
    uint ItemLevel,
    EquipmentSlot Slot,
    IReadOnlySet<uint> EligibleClassJobIds,
    byte Rarity,
    bool IsEquipment,
    bool IsSoulCrystal,
    bool? IsDesynthesizable,
    bool? IsVendorSellable,
    uint? VendorSellPrice,
    bool? IsDiscardable,
    bool? IsArmoireEligible,
    bool? IsRecoverable,
    bool IsExplicitlyProtectedFamily,
    EquipmentStatProfile? StatProfile = null,
    EquipmentRarity NormalizedRarity = EquipmentRarity.Unknown,
    ExpertDeliveryEligibility ExpertDeliveryEligibility = ExpertDeliveryEligibility.Unknown,
    string? ExpertDeliveryProvenance = null,
    EquipmentStatProfile? HighQualityStatProfile = null,
    bool IsUnique = false,
    uint EquipSlotCategoryId = 0,
    sbyte MainHandOccupancy = 0,
    sbyte OffHandOccupancy = 0,
    bool FitsLeftRing = true,
    bool FitsRightRing = true);

public sealed record EquipmentDominanceWitness(
    EquipmentInstanceFingerprint Fingerprint,
    uint ItemId,
    string ItemName,
    EquipmentStatProfile EffectiveStatProfile,
    bool IsGearsetReferenced);

public sealed record EquipmentWitnessRequirement(
    CharacterJobSnapshot Job,
    EquipmentSlot Slot,
    int RequiredCount,
    IReadOnlyList<EquipmentDominanceWitness> ViableWitnesses,
    string? Diagnostic = null);

public static class EquipmentInstanceStats
{
    public static EquipmentStatProfile? Resolve(
        EquipmentInstanceSnapshot instance,
        EquipmentItemDefinition definition) =>
        instance.Fingerprint.IsHighQuality
            ? definition.HighQualityStatProfile
            : definition.StatProfile;
}

public sealed record GearsetItemReference(EquipmentSlot Slot, uint ItemId);

public sealed record GearsetSnapshot(
    int GearsetId,
    string Name,
    uint ClassJobId,
    IReadOnlyList<GearsetItemReference> Items,
    bool IsValid,
    string? Diagnostic = null);

public sealed record SnapshotComponentDiagnostic(
    string Component,
    SnapshotComponentStatus Status,
    string? Message = null);

public sealed record CharacterEquipmentSnapshotDiagnostics(
    IReadOnlyList<SnapshotComponentDiagnostic> Components)
{
    public bool IsComplete =>
        Components.Count > 0 && Components.All(component => component.Status == SnapshotComponentStatus.Complete);

    public IReadOnlyList<SnapshotComponentDiagnostic> Blocking =>
        Components.Where(component => component.Status != SnapshotComponentStatus.Complete).ToArray();
}

public sealed record CharacterEquipmentSnapshot(
    Guid GenerationId,
    CharacterIdentitySnapshot Identity,
    IReadOnlyList<CharacterJobSnapshot> Jobs,
    IReadOnlyList<GearsetSnapshot> Gearsets,
    IReadOnlyList<EquipmentInstanceSnapshot> Instances,
    IReadOnlyDictionary<uint, EquipmentItemDefinition> Definitions,
    CharacterEquipmentSnapshotDiagnostics Diagnostics);

