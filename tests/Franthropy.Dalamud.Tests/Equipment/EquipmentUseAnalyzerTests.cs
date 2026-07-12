using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;

namespace Franthropy.Dalamud.Tests.Equipment;

public sealed class EquipmentUseAnalyzerTests
{
    private static readonly CharacterScope Scope = new(1, "Tester", 21);
    private readonly EquipmentUseAnalyzer analyzer = new();

    [Fact]
    public void SharedGear_IsObsoleteOnlyWhenEveryUnlockedEligibleJobHasBetterBaseline()
    {
        var candidate = Definition(100, 20, 30, 1, 2);
        var warriorBaseline = Definition(200, 30, 40, 1);
        var paladinBaseline = Definition(300, 30, 29, 2);
        var result = analyzer.Analyze(
            candidate,
            [Job(1, 50, true), Job(2, 50, true)],
            [Gearset(1, 1, 200), Gearset(2, 2, 300)],
            Definitions(candidate, warriorBaseline, paladinBaseline));

        Assert.Equal(EquipmentUseStatus.BaselineNotBetter, result.Status);
        Assert.False(result.IsStrictlyObsolete);
    }

    [Fact]
    public void LowerLevelUnlockedJob_ProtectsFutureGear()
    {
        var candidate = Definition(100, 40, 40, 1);
        var result = analyzer.Analyze(candidate, [Job(1, 30, true)], [], Definitions(candidate));
        Assert.Equal(EquipmentUseStatus.FutureUse, result.Status);
    }

    [Fact]
    public void LockedJob_DoesNotProtectGear()
    {
        var candidate = Definition(100, 40, 40, 1, 2);
        var baseline = Definition(200, 50, 50, 1);
        var result = analyzer.Analyze(
            candidate,
            [Job(1, 60, true), Job(2, 1, false)],
            [Gearset(1, 1, 200)],
            Definitions(candidate, baseline));
        Assert.True(result.IsStrictlyObsolete);
    }

    [Fact]
    public void MissingGearset_PreventsObsoleteResult()
    {
        var candidate = Definition(100, 20, 20, 1);
        var result = analyzer.Analyze(candidate, [Job(1, 50, true)], [], Definitions(candidate));
        Assert.Equal(EquipmentUseStatus.EvaluationFailure, result.Status);
        Assert.Equal("JobComparisonFailed", result.FailureCode);
    }

    [Theory]
    [InlineData(3, 21)] // MRD -> WAR
    [InlineData(29, 30)] // ROG -> NIN
    public void UpgradedJobGearset_SatisfiesItsBaseClassFamily(uint classId, uint jobId)
    {
        var candidate = Definition(100, 20, 20, classId, jobId);
        var baseline = Definition(200, 30, 30, classId, jobId);
        var result = analyzer.Analyze(
            candidate,
            [Job(classId, 50, true, classId), Job(jobId, 50, true, classId)],
            [Gearset(1, jobId, 200)],
            Definitions(candidate, baseline));

        Assert.True(result.IsStrictlyObsolete);
        var comparison = Assert.Single(result.Comparisons);
        Assert.Equal(jobId, comparison.Job.ClassJobId);
    }

    [Fact]
    public void UnknownUnlockState_PreventsObsoleteResult()
    {
        var candidate = Definition(100, 20, 20, 1);
        var result = analyzer.Analyze(candidate, [Job(1, 50, null)], [], Definitions(candidate));
        Assert.Equal(EquipmentUseStatus.EvaluationFailure, result.Status);
        Assert.Equal("JobUnlockStateUnavailable", result.FailureCode);
    }

    [Fact]
    public void UnobtainedEligibleFamily_IsNotAConsumer()
    {
        var candidate = Definition(100, 20, 20, 1);
        var result = analyzer.Analyze(candidate, [Job(1, 50, false)], [], Definitions(candidate));
        Assert.Equal(EquipmentUseStatus.NoObtainedEligibleJob, result.Status);
    }

    [Fact]
    public void StatRegression_PreventsItemLevelFromProvingObsolescence()
    {
        var candidate = Definition(100, 20, 20, 1) with
        {
            StatProfile = new EquipmentStatProfile([new(1, EquipmentStatSemantic.Strength, 25, false)], 0, 0, 20, 20, true),
        };
        var baseline = Definition(200, 30, 30, 1) with
        {
            StatProfile = new EquipmentStatProfile([new(1, EquipmentStatSemantic.Strength, 24, false)], 0, 0, 30, 30, true),
        };
        var result = analyzer.Analyze(candidate, [Job(1, 50, true)], [Gearset(1, 1, 200)], Definitions(candidate, baseline));
        Assert.Equal(EquipmentUseStatus.BaselineNotBetter, result.Status);
    }

    [Fact]
    public void StatlessAllClassesEquipment_IsLikelyCosmetic()
    {
        var candidate = Definition(100, 1, 1, 1, 2) with
        {
            StatProfile = new EquipmentStatProfile([], 0, 0, 0, 0, true),
        };
        var result = analyzer.Analyze(candidate, [Job(1, 50, true), Job(2, 50, true)], [], Definitions(candidate));
        Assert.Equal(EquipmentUseStatus.LikelyCosmetic, result.Status);
    }

    [Fact]
    public void GearsetProtection_IsConservativeAcrossDuplicateInstances()
    {
        var index = GearsetProtectionIndex.Create([Gearset(1, 1, 100)]);
        var first = Instance(100, "ArmoryBody", 1);
        var second = Instance(100, "Inventory1", 12);
        Assert.True(index.IsProtected(first.Fingerprint.ItemId));
        Assert.True(index.IsProtected(second.Fingerprint.ItemId));
        Assert.Single(index.GetReferences(100));
    }

    [Fact]
    public void PartialDiagnostics_AreBlocking()
    {
        var diagnostics = new CharacterEquipmentSnapshotDiagnostics(
        [
            new("jobs", SnapshotComponentStatus.Complete),
            new("gearsets", SnapshotComponentStatus.Partial, "Unreadable gearset"),
        ]);
        Assert.False(diagnostics.IsComplete);
        Assert.Single(diagnostics.Blocking);
    }

    [Fact]
    public void LooseOwnedItem_CanProveCandidateObsolete()
    {
        var candidate = Definition(100, 20, 20, 1);
        var upgrade = Definition(200, 30, 30, 1);
        var candidateInstance = Instance(100, "Inventory1", 1);
        var upgradeInstance = Instance(200, "ArmoryBody", 2);

        var result = analyzer.Analyze(candidateInstance, candidate, [Job(1, 50, true)], [],
            [candidateInstance, upgradeInstance], Definitions(candidate, upgrade));

        Assert.True(result.IsStrictlyObsolete);
        var witness = Assert.Single(Assert.Single(result.Comparisons).WitnessRequirement!.ViableWitnesses);
        Assert.Equal(upgradeInstance.Fingerprint, witness.Fingerprint);
        Assert.False(witness.IsGearsetReferenced);
    }

    [Fact]
    public void HighQualityWitness_UsesExactSpecialProfile()
    {
        var candidate = Definition(100, 20, 25, 1);
        var hqUpgrade = Definition(200, 20, 20, 1) with
        {
            HighQualityStatProfile = new EquipmentStatProfile(
                [new(1, EquipmentStatSemantic.Strength, 30, true)], 30, 0, 30, 30, true),
        };
        var candidateInstance = Instance(100, "Inventory1", 1);
        var hqInstance = Instance(200, "Inventory1", 2, highQuality: true);

        var result = analyzer.Analyze(candidateInstance, candidate, [Job(1, 50, true)], [],
            [candidateInstance, hqInstance], Definitions(candidate, hqUpgrade));

        Assert.True(result.IsStrictlyObsolete);
        Assert.Equal(30, Assert.Single(result.Comparisons).WitnessRequirement!.ViableWitnesses[0]
            .EffectiveStatProfile.Parameters.Single().Value);
    }

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

    private static GearsetSnapshot Gearset(int id, uint jobId, uint itemId) =>
        new(id, $"Set {id}", jobId, [new(EquipmentSlot.Body, itemId)], true);

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
