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
        Assert.Equal(EquipmentUseStatus.MissingBaseline, result.Status);
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
        Assert.Equal(EquipmentUseStatus.UnknownJobUnlockState, result.Status);
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

    private static CharacterJobSnapshot Job(uint id, uint level, bool? unlocked, uint? parentId = null) =>
        new(id, $"J{id}", $"Job {id}", level, unlocked, parentId, "Tank");

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
            IsExplicitlyProtectedFamily: false);

    private static IReadOnlyDictionary<uint, EquipmentItemDefinition> Definitions(params EquipmentItemDefinition[] values) =>
        values.ToDictionary(value => value.ItemId);

    private static EquipmentInstanceSnapshot Instance(uint itemId, string container, int slot) =>
        new(
            new EquipmentInstanceFingerprint(Scope, container, slot, itemId, false, 1, 30000, 0, null, [], null, []),
            DateTimeOffset.UtcNow,
            false);
}
