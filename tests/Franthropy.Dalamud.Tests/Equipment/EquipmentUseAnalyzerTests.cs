using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;

namespace Franthropy.Dalamud.Tests.Equipment;

public sealed class EquipmentUseAnalyzerTests
{
    private static readonly CharacterScope Scope = new(1, "Tester", 21);
    private readonly EquipmentUseAnalyzer analyzer = new();

    [Fact]
    public void RingRequiresTwoJointlyFeasibleDominatingInstances()
    {
        var candidate = Definition(100, 20, 20, 1) with { Slot = EquipmentSlot.Ring };
        var upgrade = Definition(200, 30, 30, 1) with { Slot = EquipmentSlot.Ring, IsUnique = true };
        var candidateInstance = Instance(100, "Inventory1", 1);
        var first = Instance(200, "Inventory1", 2);
        var second = Instance(200, "Inventory1", 3);

        var result = analyzer.Analyze(candidateInstance, candidate, [Job(1, 50, true)], [],
            [candidateInstance, first, second], Definitions(candidate, upgrade));

        Assert.Equal(EquipmentUseStatus.BaselineNotBetter, result.Status);
    }

    private static CharacterJobSnapshot Job(uint id, uint level, bool? unlocked, uint? parentId = null) =>
        new(id, $"J{id}", $"Job {id}", level, unlocked, parentId, "Tank", EquipmentStatSemantic.Strength, EquipmentDiscipline.Combat);

    private static EquipmentItemDefinition Definition(
        uint id,
        uint equipLevel,
        uint itemLevel,
        params uint[] jobs) =>
        new(
            id,
            $"Item {id}",
            equipLevel,
            itemLevel,
            EquipmentSlot.Body,
            jobs.ToHashSet(),
            Rarity: 1,
            IsEquipment: true,
            IsSoulCrystal: false,
            IsDesynthesizable: true,
            IsVendorSellable: true,
            VendorSellPrice: 1,
            IsDiscardable: true,
            IsArmoireEligible: false,
            IsRecoverable: true,
            IsExplicitlyProtectedFamily: false,
            StatProfile: new EquipmentStatProfile(
                [new(1, EquipmentStatSemantic.Strength, checked((int)itemLevel), false)],
                checked((int)itemLevel), 0, checked((int)itemLevel), checked((int)itemLevel), true),
            NormalizedRarity: EquipmentRarity.Normal);

    private static IReadOnlyDictionary<uint, EquipmentItemDefinition> Definitions(params EquipmentItemDefinition[] values) =>
        values.ToDictionary(value => value.ItemId);

    private static EquipmentInstanceSnapshot Instance(uint itemId, string container, int slot, bool highQuality = false) =>
        new(
            new EquipmentInstanceFingerprint(Scope, container, slot, itemId, highQuality, 1, 30000, 0, null, [], null, []),
            DateTimeOffset.UtcNow,
            false);
}
