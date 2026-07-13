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
public enum EquipmentWearerKind { Unknown, Cosmetic, Combat, Tank, StrengthCombat, StrengthMelee, Striking, Maiming, DexterityDps, PhysicalRanged, Scouting, Casting, Healing, Crafter, Gatherer }
public enum EquipmentRarity { Unknown, Normal, Uncommon, Rare, Relic }
public enum ExpertDeliveryEligibility { Unknown, Ineligible, Eligible }

public sealed record EquipmentStatValue(uint BaseParamId, EquipmentStatSemantic Semantic, int Value, bool IsSpecial, string? SourceName = null);

public sealed record EquipmentStatProfile(
    IReadOnlyList<EquipmentStatValue> Parameters,
    int PhysicalDamage,
    int MagicalDamage,
    int PhysicalDefense,
    int MagicalDefense,
    bool IsComplete,
    int BlockStrength = 0,
    int BlockRate = 0,
    int DelayMilliseconds = 0)
{
    public bool HasFunctionalStats => Parameters.Any(value => value.Value > 0) ||
        PhysicalDamage > 0 || MagicalDamage > 0 || PhysicalDefense > 0 || MagicalDefense > 0 ||
        BlockStrength > 0 || BlockRate > 0;
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

public sealed class EquipmentInstanceFingerprintComparer : IEqualityComparer<EquipmentInstanceFingerprint>
{
    public static EquipmentInstanceFingerprintComparer Instance { get; } = new();

    public bool Equals(EquipmentInstanceFingerprint? left, EquipmentInstanceFingerprint? right) =>
        ReferenceEquals(left, right) ||
        left is not null && right is not null &&
        left.Character == right.Character &&
        left.Container == right.Container &&
        left.SlotIndex == right.SlotIndex &&
        left.ItemId == right.ItemId &&
        left.IsHighQuality == right.IsHighQuality &&
        left.Quantity == right.Quantity &&
        left.Condition == right.Condition &&
        left.Spiritbond == right.Spiritbond &&
        left.CrafterContentId == right.CrafterContentId &&
        left.MateriaIds.SequenceEqual(right.MateriaIds) &&
        left.GlamourId == right.GlamourId &&
        left.Stains.SequenceEqual(right.Stains);

    public int GetHashCode(EquipmentInstanceFingerprint value)
    {
        var hash = new HashCode();
        hash.Add(value.Character);
        hash.Add(value.Container, StringComparer.Ordinal);
        hash.Add(value.SlotIndex);
        hash.Add(value.ItemId);
        hash.Add(value.IsHighQuality);
        hash.Add(value.Quantity);
        hash.Add(value.Condition);
        hash.Add(value.Spiritbond);
        hash.Add(value.CrafterContentId);
        foreach (var materiaId in value.MateriaIds) hash.Add(materiaId);
        hash.Add(value.GlamourId);
        foreach (var stain in value.Stains) hash.Add(stain);
        return hash.ToHashCode();
    }
}

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
    bool FitsRightRing = true,
    bool IsAllClasses = false,
    uint ClassJobCategoryId = 0,
    string? ClassJobCategoryName = null,
    uint ItemUiCategoryId = 0,
    string? ItemUiCategoryName = null,
    uint ItemSearchCategoryId = 0,
    string? ItemSearchCategoryName = null,
    bool IsSpecialPurpose = false,
    uint ItemSpecialBonusId = 0,
    int ItemSpecialBonusParam = 0,
    uint ItemActionId = 0,
    uint EquipRestrictionId = 0,
    uint GrandCompanyId = 0,
    uint RequiredPvpRank = 0,
    uint ClassJobUseId = 0)
{
    public bool HasUnmodeledEquipRestriction =>
        EquipRestrictionId > 1 || GrandCompanyId != 0 || RequiredPvpRank != 0;
}

public sealed record EquipmentWearerInference(EquipmentWearerKind Kind, string Label, string Source)
{
    public bool Matches(CharacterJobSnapshot job, EquipmentSlot slot) => Kind switch
    {
        EquipmentWearerKind.Crafter => job.Discipline == EquipmentDiscipline.Crafter,
        EquipmentWearerKind.Gatherer => job.Discipline == EquipmentDiscipline.Gatherer,
        EquipmentWearerKind.Tank => string.Equals(job.Role, "Tank", StringComparison.OrdinalIgnoreCase),
        EquipmentWearerKind.StrengthCombat => job.Discipline == EquipmentDiscipline.Combat && job.PrimaryStat == EquipmentStatSemantic.Strength,
        EquipmentWearerKind.StrengthMelee => IsMelee(job) && job.PrimaryStat == EquipmentStatSemantic.Strength,
        EquipmentWearerKind.Striking => job.Abbreviation is "PGL" or "ROG" or "MNK" or "NIN" or "SAM" or "VPR",
        EquipmentWearerKind.Maiming => job.Abbreviation is "LNC" or "DRG" or "RPR",
        EquipmentWearerKind.DexterityDps => job.Discipline == EquipmentDiscipline.Combat && job.PrimaryStat == EquipmentStatSemantic.Dexterity,
        EquipmentWearerKind.PhysicalRanged => string.Equals(job.Role, "Physical Ranged DPS", StringComparison.OrdinalIgnoreCase) && job.PrimaryStat == EquipmentStatSemantic.Dexterity,
        EquipmentWearerKind.Scouting => IsMelee(job) && job.PrimaryStat == EquipmentStatSemantic.Dexterity,
        EquipmentWearerKind.Casting => job.Discipline == EquipmentDiscipline.Combat && job.PrimaryStat == EquipmentStatSemantic.Intelligence,
        EquipmentWearerKind.Healing => job.Discipline == EquipmentDiscipline.Combat && job.PrimaryStat == EquipmentStatSemantic.Mind,
        EquipmentWearerKind.Combat => job.Discipline == EquipmentDiscipline.Combat,
        _ => false,
    };

    public static EquipmentWearerInference Infer(EquipmentItemDefinition definition)
    {
        var named = InferFromCanonicalSuffix(definition);
        if (named is not null)
            return named with { Source = IsBroadClassJobCategory(definition) ? "inferred from canonical family within broad game category" : $"game category: {definition.ClassJobCategoryName ?? definition.ClassJobCategoryId.ToString()}" };
        var supplied = definition.StatProfile?.Parameters
            .Where(value => value.Value > 0)
            .Select(value => value.Semantic)
            .ToHashSet() ?? [];
        var defining = new HashSet<EquipmentStatSemantic>
        {
            EquipmentStatSemantic.Strength, EquipmentStatSemantic.Dexterity, EquipmentStatSemantic.Intelligence, EquipmentStatSemantic.Mind,
            EquipmentStatSemantic.Craftsmanship, EquipmentStatSemantic.Control, EquipmentStatSemantic.CraftingPoints,
            EquipmentStatSemantic.Gathering, EquipmentStatSemantic.Perception, EquipmentStatSemantic.GatheringPoints,
        };
        if (definition.IsAllClasses && !supplied.Overlaps(defining))
            return new(EquipmentWearerKind.Cosmetic, "Cosmetic / non-role equipment", "inferred from absence of wearer-defining stats");
        if (supplied.Overlaps([EquipmentStatSemantic.Craftsmanship, EquipmentStatSemantic.Control, EquipmentStatSemantic.CraftingPoints]))
            return new(EquipmentWearerKind.Crafter, "Crafters", "inferred from stats");
        if (supplied.Overlaps([EquipmentStatSemantic.Gathering, EquipmentStatSemantic.Perception, EquipmentStatSemantic.GatheringPoints]))
            return new(EquipmentWearerKind.Gatherer, "Gatherers", "inferred from stats");
        var primary = new[] { EquipmentStatSemantic.Strength, EquipmentStatSemantic.Dexterity, EquipmentStatSemantic.Intelligence, EquipmentStatSemantic.Mind }
            .Where(supplied.Contains).ToArray();
        if (primary.Length > 1) return new(EquipmentWearerKind.Combat, "Combat jobs", "inferred from stats");
        if (primary.Length == 1)
            return primary[0] switch
            {
                EquipmentStatSemantic.Strength when supplied.Contains(EquipmentStatSemantic.Tenacity) => new(EquipmentWearerKind.Tank, "Tanks", "inferred from stats"),
                EquipmentStatSemantic.Strength => new(EquipmentWearerKind.StrengthCombat, "Strength combat jobs", "inferred from stats"),
                EquipmentStatSemantic.Dexterity => new(EquipmentWearerKind.DexterityDps, "Dexterity DPS", "inferred from stats"),
                EquipmentStatSemantic.Intelligence => new(EquipmentWearerKind.Casting, "Magical DPS", "inferred from stats"),
                EquipmentStatSemantic.Mind => new(EquipmentWearerKind.Healing, "Healers", "inferred from stats"),
                _ => new(EquipmentWearerKind.Combat, "Combat jobs", "inferred from stats"),
            };
        if (definition.StatProfile is { HasFunctionalStats: false })
            return new(EquipmentWearerKind.Cosmetic, "Cosmetic / statless", "inferred from stats");
        if (!definition.IsAllClasses && !string.IsNullOrWhiteSpace(definition.ClassJobCategoryName))
            return new(EquipmentWearerKind.Unknown, definition.ClassJobCategoryName, "game category");
        return new(EquipmentWearerKind.Unknown, definition.IsAllClasses ? "All Classes (unresolved)" : "Equip-mask restricted", "unresolved");
    }

    public static bool MatchesIntendedWearer(
        EquipmentItemDefinition definition,
        CharacterJobSnapshot job,
        IReadOnlyList<CharacterJobSnapshot> knownJobs)
    {
        if (definition.IsSpecialPurpose)
            return false;
        if (!RequiresIntentRefinement(definition, knownJobs))
            return true;
        var inference = Infer(definition);
        return inference.Kind switch
        {
            EquipmentWearerKind.Cosmetic => false,
            EquipmentWearerKind.Unknown => false,
            _ => inference.Matches(job, definition.Slot),
        };
    }

    public static bool RequiresIntentRefinement(
        EquipmentItemDefinition definition,
        IReadOnlyList<CharacterJobSnapshot> knownJobs)
    {
        var eligible = knownJobs
            .Where(value => definition.EligibleClassJobIds.Contains(value.ClassJobId))
            .Where(value => !string.Equals(value.Abbreviation, "ADV", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (eligible.Length == 0)
            return true;
        var domains = eligible.Select(value => value.Discipline switch
        {
            EquipmentDiscipline.Crafter => "crafter",
            EquipmentDiscipline.Gatherer => "gatherer",
            EquipmentDiscipline.Combat => $"combat:{value.Role}:{value.PrimaryStat}",
            _ => "unknown",
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return domains.Length > 1;
    }

    private static EquipmentWearerInference? InferFromCanonicalSuffix(EquipmentItemDefinition definition)
    {
        var name = definition.Name;
        if (name.EndsWith(" of Crafting", StringComparison.OrdinalIgnoreCase)) return new(EquipmentWearerKind.Crafter, "Crafters", string.Empty);
        if (name.EndsWith(" of Gathering", StringComparison.OrdinalIgnoreCase)) return new(EquipmentWearerKind.Gatherer, "Gatherers", string.Empty);
        if (name.EndsWith(" of Fending", StringComparison.OrdinalIgnoreCase)) return new(EquipmentWearerKind.Tank, "Tanks", string.Empty);
        if (name.EndsWith(" of Slaying", StringComparison.OrdinalIgnoreCase)) return new(EquipmentWearerKind.StrengthMelee, "Strength melee DPS", string.Empty);
        if (name.EndsWith(" of Maiming", StringComparison.OrdinalIgnoreCase)) return new(EquipmentWearerKind.Maiming, "Maiming jobs", string.Empty);
        if (name.EndsWith(" of Striking", StringComparison.OrdinalIgnoreCase)) return new(EquipmentWearerKind.Striking, "Melee DPS (Striking)", string.Empty);
        if (name.EndsWith(" of Scouting", StringComparison.OrdinalIgnoreCase)) return new(EquipmentWearerKind.Scouting, "Dexterity melee DPS", string.Empty);
        if (name.EndsWith(" of Aiming", StringComparison.OrdinalIgnoreCase))
            return IsAccessory(definition.Slot) ? new(EquipmentWearerKind.DexterityDps, "Dexterity DPS", string.Empty) : new(EquipmentWearerKind.PhysicalRanged, "Physical ranged DPS", string.Empty);
        if (name.EndsWith(" of Casting", StringComparison.OrdinalIgnoreCase)) return new(EquipmentWearerKind.Casting, "Magical DPS", string.Empty);
        if (name.EndsWith(" of Healing", StringComparison.OrdinalIgnoreCase)) return new(EquipmentWearerKind.Healing, "Healers", string.Empty);
        return null;
    }

    private static bool IsMelee(CharacterJobSnapshot job) => string.Equals(job.Role, "Melee DPS", StringComparison.OrdinalIgnoreCase);

    private static bool IsAccessory(EquipmentSlot? definitionSlot) => definitionSlot is EquipmentSlot.Ears or EquipmentSlot.Neck or EquipmentSlot.Wrists or EquipmentSlot.Ring;

    public static bool IsBroadClassJobCategory(EquipmentItemDefinition definition) =>
        definition.IsAllClasses || definition.ClassJobCategoryId is >= 30 and <= 34;
}

public sealed record EquipmentDominanceWitness(
    EquipmentInstanceFingerprint Fingerprint,
    uint ItemId,
    string ItemName,
    EquipmentStatProfile EffectiveStatProfile,
    bool IsGearsetReferenced,
    EquipmentCoverageKind CoverageKind = EquipmentCoverageKind.ComponentwiseNoLoss);

public enum EquipmentCoverageKind
{
    None,
    ComponentwiseNoLoss,
    CombatCoreAndSecondaryBudget,
}

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

public sealed record GearsetItemReference(EquipmentSlot Slot, uint ItemId, bool? IsHighQuality = null);

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

