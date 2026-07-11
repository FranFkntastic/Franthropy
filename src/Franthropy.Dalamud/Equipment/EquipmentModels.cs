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
    bool IsExplicitlyProtectedFamily);

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

