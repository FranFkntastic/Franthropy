using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;

namespace Franthropy.Dalamud.Tests.Equipment;

public sealed class EquipmentUseAnalyzerTests
{
    [Fact]
    public void Analyze_IgnoresIncompleteProspectiveWitnessWhenCompleteWitnessExists()
    {
        var candidate = Definition(100, 10, 5, 1) with
        {
            Slot = EquipmentSlot.Ring,
            FitsLeftRing = true,
            FitsRightRing = true,
        };
        var incomplete = Definition(101, 10, 10, 1) with
        {
            Slot = EquipmentSlot.Ring,
            StatProfile = Definition(101, 10, 10, 1).StatProfile! with { IsComplete = false },
            FitsLeftRing = true,
            FitsRightRing = true,
        };
        var first = Definition(102, 10, 10, 1) with
        {
            Slot = EquipmentSlot.Ring,
            FitsLeftRing = true,
            FitsRightRing = true,
        };
        var second = Definition(103, 10, 11, 1) with
        {
            Slot = EquipmentSlot.Ring,
            FitsLeftRing = true,
            FitsRightRing = true,
        };
        var candidateInstance = Instance(100, "ArmoryRing", 0);
        var result = new EquipmentUseAnalyzer().Analyze(candidateInstance, candidate, [Job(1, 50, true)], [],
            [candidateInstance, Instance(101, "ArmoryRing", 1), Instance(102, "ArmoryRing", 2), Instance(103, "ArmoryRing", 3)],
            new Dictionary<uint, EquipmentItemDefinition> { [100] = candidate, [101] = incomplete, [102] = first, [103] = second });

        Assert.Equal(EquipmentUseStatus.Obsolete, result.Status);
        Assert.DoesNotContain(result.Comparisons[0].WitnessRequirement!.ViableWitnesses, witness => witness.ItemId == 101);
    }

    private static readonly CharacterScope Scope = new(1, "Tester", 21);
    private readonly EquipmentUseAnalyzer analyzer = new();

    [Fact]
    public void SharedGear_IsObsoleteOnlyWhenEveryUnlockedEligibleJobHasBetterBaseline()
    {
        var candidate = Definition(100, 20, 30, 1, 2);
        var warriorBaseline = Definition(200, 30, 40, 1);
        var paladinBaseline = Definition(300, 30, 29, 2);
        var result = analyzer.AnalyzeNqDefinitionPreview(
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
        var result = analyzer.AnalyzeNqDefinitionPreview(candidate, [Job(1, 30, true)], [], Definitions(candidate));
        Assert.Equal(EquipmentUseStatus.FutureUse, result.Status);
    }

    [Fact]
    public void LockedJob_DoesNotProtectGear()
    {
        var candidate = Definition(100, 40, 40, 1, 2);
        var baseline = Definition(200, 50, 50, 1);
        var result = analyzer.AnalyzeNqDefinitionPreview(
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
        var result = analyzer.AnalyzeNqDefinitionPreview(candidate, [Job(1, 50, true)], [], Definitions(candidate));
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
        var result = analyzer.AnalyzeNqDefinitionPreview(
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
        var result = analyzer.AnalyzeNqDefinitionPreview(candidate, [Job(1, 50, null)], [], Definitions(candidate));
        Assert.Equal(EquipmentUseStatus.EvaluationFailure, result.Status);
        Assert.Equal("JobUnlockStateUnavailable", result.FailureCode);
    }

    [Fact]
    public void UnobtainedEligibleFamily_IsNotAConsumer()
    {
        var candidate = Definition(100, 20, 20, 1);
        var result = analyzer.AnalyzeNqDefinitionPreview(candidate, [Job(1, 50, false)], [], Definitions(candidate));
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
        var result = analyzer.AnalyzeNqDefinitionPreview(candidate, [Job(1, 50, true)], [Gearset(1, 1, 200)], Definitions(candidate, baseline));
        Assert.Equal(EquipmentUseStatus.BaselineNotBetter, result.Status);
    }

    [Fact]
    public void StatlessAllClassesEquipment_IsLikelyCosmetic()
    {
        var candidate = Definition(100, 1, 1, 1, 2) with
        {
            StatProfile = new EquipmentStatProfile([], 0, 0, 0, 0, true),
            IsAllClasses = true,
        };
        var result = analyzer.AnalyzeNqDefinitionPreview(candidate, [Job(1, 50, true), Job(2, 50, true)], [], Definitions(candidate));
        Assert.Equal(EquipmentUseStatus.LikelyCosmetic, result.Status);
    }

    [Fact]
    public void AllClassesCastingItem_IsComparedOnlyToIntelligenceJobs()
    {
        var candidate = Definition(100, 20, 20, 1, 2) with
        {
            IsAllClasses = true,
            StatProfile = new EquipmentStatProfile([new(4, EquipmentStatSemantic.Intelligence, 20, false), new(3, EquipmentStatSemantic.Vitality, 20, false)], 0, 0, 0, 0, true),
        };
        var casterUpgrade = Definition(200, 30, 30, 2) with
        {
            StatProfile = new EquipmentStatProfile([new(4, EquipmentStatSemantic.Intelligence, 30, false), new(3, EquipmentStatSemantic.Vitality, 30, false)], 0, 0, 0, 0, true),
        };
        var strength = Job(1, 50, true) with { PrimaryStat = EquipmentStatSemantic.Strength };
        var caster = Job(2, 50, true) with { PrimaryStat = EquipmentStatSemantic.Intelligence };
        var result = analyzer.AnalyzeNqDefinitionPreview(candidate, [strength, caster], [Gearset(1, 2, 200)], Definitions(candidate, casterUpgrade));

        Assert.True(result.IsStrictlyObsolete);
        Assert.Equal(2u, Assert.Single(result.Comparisons).Job.ClassJobId);
        Assert.Equal("Magical DPS", EquipmentWearerInference.Infer(candidate).Label);
    }

    [Fact]
    public void BroadCombatCategoryCastingItem_IsComparedOnlyToIntelligenceJobs()
    {
        var candidate = Definition(100, 20, 20, 1, 2) with
        {
            Name = "Test Bracelet of Casting",
            ClassJobCategoryId = 34,
            ClassJobCategoryName = "Disciples of War or Magic",
            StatProfile = new EquipmentStatProfile([new(4, EquipmentStatSemantic.Intelligence, 20, false)], 0, 0, 0, 0, true),
        };
        var casterUpgrade = Definition(200, 30, 30, 2) with
        {
            StatProfile = new EquipmentStatProfile([new(4, EquipmentStatSemantic.Intelligence, 30, false)], 0, 0, 0, 0, true),
        };
        var strength = Job(1, 50, true) with { PrimaryStat = EquipmentStatSemantic.Strength };
        var caster = Job(2, 50, true) with { PrimaryStat = EquipmentStatSemantic.Intelligence };
        var result = analyzer.AnalyzeNqDefinitionPreview(candidate, [strength, caster], [Gearset(1, 2, 200)], Definitions(candidate, casterUpgrade));

        Assert.True(result.IsStrictlyObsolete);
        Assert.Equal(2u, Assert.Single(result.Comparisons).Job.ClassJobId);
        Assert.Equal("inferred from canonical family within broad game category", EquipmentWearerInference.Infer(candidate).Source);
    }

    [Fact]
    public void AllClassesCasterAccessory_CannotServeAsCraftingBaseline()
    {
        var candidate = Definition(100, 81, 480, 8) with
        {
            Name = "Ametrine Ear Cuffs of Crafting",
            Slot = EquipmentSlot.Ears,
            ClassJobCategoryId = 33,
            ClassJobCategoryName = "Disciple of the Hand",
            StatProfile = new EquipmentStatProfile([
                new(70, EquipmentStatSemantic.Craftsmanship, 45, false),
                new(11, EquipmentStatSemantic.CraftingPoints, 65, false)], 0, 0, 0, 0, true),
        };
        var caster = Definition(200, 60, 270, 8) with
        {
            Name = "Augmented Shire Philosopher's Earring",
            Slot = EquipmentSlot.Ears,
            IsAllClasses = true,
            ClassJobCategoryId = 1,
            ClassJobCategoryName = "All Classes",
            StatProfile = new EquipmentStatProfile([new(4, EquipmentStatSemantic.Intelligence, 47, false)], 0, 0, 1, 1, true),
        };
        var target = Instance(100, "ArmoryEar", 1);
        var result = analyzer.Analyze(target, candidate,
            [ProfileJob(8, "CRP", "Role 0", EquipmentStatSemantic.Unknown, EquipmentDiscipline.Crafter)], [],
            [target, Instance(200, "ArmoryEar", 2)], Definitions(candidate, caster));

        Assert.Equal(EquipmentUseStatus.BaselineNotBetter, result.Status);
        var comparison = Assert.Single(result.Comparisons);
        Assert.Null(comparison.Baseline);
        Assert.Empty(comparison.WitnessRequirement!.ViableWitnesses);
    }

    [Fact]
    public void MeldedCraftingAccessory_CanServeAsConservativelyUndermodeledCraftingBaseline()
    {
        var candidate = Definition(100, 81, 480, 8) with
        {
            Name = "Ametrine Ear Cuffs of Crafting",
            Slot = EquipmentSlot.Ears,
            ClassJobCategoryId = 33,
            ClassJobCategoryName = "Disciple of the Hand",
            StatProfile = new EquipmentStatProfile([
                new(70, EquipmentStatSemantic.Craftsmanship, 45, false),
                new(11, EquipmentStatSemantic.CraftingPoints, 65, false)], 0, 0, 0, 0, true),
        };
        var upgrade = Definition(300, 90, 560, 8) with
        {
            Name = "Integral Earrings of Crafting",
            Slot = EquipmentSlot.Ears,
            ClassJobCategoryId = 33,
            ClassJobCategoryName = "Disciple of the Hand",
            StatProfile = new EquipmentStatProfile([
                new(70, EquipmentStatSemantic.Craftsmanship, 50, false),
                new(11, EquipmentStatSemantic.CraftingPoints, 70, false)], 0, 0, 0, 0, true),
        };
        var target = Instance(100, "ArmoryEar", 1);
        var melded = Instance(300, "ArmoryEar", 2) with
        {
            Fingerprint = Instance(300, "ArmoryEar", 2).Fingerprint with { MateriaIds = [23] },
        };
        var result = analyzer.Analyze(target, candidate,
            [ProfileJob(8, "CRP", "Role 0", EquipmentStatSemantic.Unknown, EquipmentDiscipline.Crafter)], [],
            [target, melded], Definitions(candidate, upgrade));

        Assert.Equal(EquipmentUseStatus.Obsolete, result.Status);
        Assert.Equal("Integral Earrings of Crafting", Assert.Single(result.Comparisons).Baseline!.Name);
        Assert.Equal(melded.Fingerprint, Assert.Single(result.Comparisons[0].WitnessRequirement!.ViableWitnesses).Fingerprint);
    }

    [Fact]
    public void SavedCrafterGearsetAnchor_IsAuthoritativeAndCrossDomainItemsNeverEnterProof()
    {
        var candidateNq = new EquipmentStatProfile([
            new(70, EquipmentStatSemantic.Craftsmanship, 39, false),
            new(11, EquipmentStatSemantic.CraftingPoints, 58, false)], 0, 0, 0, 0, true);
        var candidateHq = new EquipmentStatProfile([
            new(70, EquipmentStatSemantic.Craftsmanship, 45, true),
            new(11, EquipmentStatSemantic.CraftingPoints, 65, true)], 0, 0, 0, 0, true);
        var candidate = Definition(35444, 81, 480, 8) with
        {
            Name = "Ametrine Ear Cuffs of Crafting", Slot = EquipmentSlot.Ears,
            ClassJobCategoryId = 33, ClassJobCategoryName = "Disciple of the Hand",
            StatProfile = candidateNq, HighQualityStatProfile = candidateHq,
        };
        var crested = Definition(47194, 100, 750, 8) with
        {
            Name = "Crested Earrings of Crafting", Slot = EquipmentSlot.Ears,
            ClassJobCategoryId = 33, ClassJobCategoryName = "Disciple of the Hand",
            StatProfile = new EquipmentStatProfile([
                new(70, EquipmentStatSemantic.Craftsmanship, 87, false),
                new(11, EquipmentStatSemantic.CraftingPoints, 75, false)], 0, 0, 0, 0, true),
            HighQualityStatProfile = new EquipmentStatProfile([
                new(70, EquipmentStatSemantic.Craftsmanship, 98, true),
                new(11, EquipmentStatSemantic.CraftingPoints, 85, true)], 0, 0, 0, 0, true),
        };
        var shire = Definition(16387, 60, 270, 8) with
        {
            Name = "Augmented Shire Philosopher's Earring", Slot = EquipmentSlot.Ears,
            IsAllClasses = true, ClassJobCategoryId = 1, ClassJobCategoryName = "All Classes",
            StatProfile = new EquipmentStatProfile([new(4, EquipmentStatSemantic.Intelligence, 47, false)], 0, 0, 1, 1, true),
        };
        var target = Instance(35444, "ArmoryEar", 6, true);
        var anchor = Instance(47194, "EquippedItems", 8, true) with
        {
            Fingerprint = Instance(47194, "EquippedItems", 8, true).Fingerprint with { MateriaIds = [1, 2, 3, 4, 5] },
        };
        var result = analyzer.Analyze(target, candidate,
            [ProfileJob(8, "CRP", "Role 0", EquipmentStatSemantic.Unknown, EquipmentDiscipline.Crafter)],
            [new GearsetSnapshot(1, "Carpenter", 8, [new(EquipmentSlot.Ears, 47194, true)], true)],
            [target, anchor, Instance(16387, "ArmoryEar", 9)], Definitions(candidate, crested, shire));

        Assert.Equal(EquipmentUseStatus.Obsolete, result.Status);
        var comparison = Assert.Single(result.Comparisons);
        Assert.Equal(EquipmentComparisonBasis.SavedGearset, comparison.Basis);
        Assert.Equal("Crested Earrings of Crafting", comparison.Baseline!.Name);
        var witness = Assert.Single(comparison.WitnessRequirement!.ViableWitnesses);
        Assert.Equal(anchor.Fingerprint, witness.Fingerprint);
        Assert.DoesNotContain(comparison.WitnessRequirement.ViableWitnesses, value => value.ItemId == 16387);
    }

    [Fact]
    public void AlternativeGearsets_ReusingOneAnchor_DoNotRequireDuplicateOwnedInstances()
    {
        var candidate = Definition(100, 50, 100, 8) with
        {
            Slot = EquipmentSlot.Ears,
            StatProfile = new EquipmentStatProfile([new(70, EquipmentStatSemantic.Craftsmanship, 40, false)], 0, 0, 0, 0, true),
        };
        var anchorDefinition = Definition(200, 60, 200, 8) with
        {
            Slot = EquipmentSlot.Ears,
            StatProfile = new EquipmentStatProfile([new(70, EquipmentStatSemantic.Craftsmanship, 50, false)], 0, 0, 0, 0, true),
        };
        var target = Instance(100, "ArmoryEar", 1);
        var anchor = Instance(200, "ArmoryEar", 2);
        var gearsets = new[]
        {
            new GearsetSnapshot(1, "Carpenter A", 8, [new(EquipmentSlot.Ears, 200, false)], true),
            new GearsetSnapshot(2, "Carpenter B", 8, [new(EquipmentSlot.Ears, 200, false)], true),
        };

        var result = analyzer.Analyze(target, candidate,
            [ProfileJob(8, "CRP", "Role 0", EquipmentStatSemantic.Unknown, EquipmentDiscipline.Crafter)],
            gearsets, [target, anchor], Definitions(candidate, anchorDefinition));

        Assert.Equal(EquipmentUseStatus.Obsolete, result.Status);
        Assert.Single(Assert.Single(result.Comparisons).WitnessRequirement!.ViableWitnesses);
    }

    [Fact]
    public void StaleSavedAnchor_FallsBackToSynthesizedOwnedLoadout()
    {
        var candidate = Definition(100, 20, 20, 8) with
        {
            Slot = EquipmentSlot.Legs,
            StatProfile = new EquipmentStatProfile([new(70, EquipmentStatSemantic.Craftsmanship, 20, false)], 0, 0, 0, 0, true),
        };
        var ownedDefinition = Definition(200, 30, 30, 8) with
        {
            Slot = EquipmentSlot.Legs,
            StatProfile = new EquipmentStatProfile([new(70, EquipmentStatSemantic.Craftsmanship, 30, false)], 0, 0, 0, 0, true),
        };
        var target = Instance(100, "ArmoryLegs", 1);
        var owned = Instance(200, "ArmoryLegs", 2);
        var result = analyzer.Analyze(target, candidate,
            [ProfileJob(8, "CRP", "Role 0", EquipmentStatSemantic.Unknown, EquipmentDiscipline.Crafter)],
            [new GearsetSnapshot(1, "Stale Crafter", 8, [new(EquipmentSlot.Legs, 999, true)], true)],
            [target, owned], Definitions(candidate, ownedDefinition));

        Assert.Equal(EquipmentUseStatus.Obsolete, result.Status);
        Assert.Equal(EquipmentComparisonBasis.SynthesizedOwnedLoadout, Assert.Single(result.Comparisons).Basis);
        Assert.Equal(owned.Fingerprint, Assert.Single(result.Comparisons[0].WitnessRequirement!.ViableWitnesses).Fingerprint);
    }

    [Fact]
    public void UpgradedJob_DoesNotAcceptClassOnlyWitness()
    {
        var candidate = Definition(100, 20, 20, 3, 21);
        var classOnly = Definition(200, 30, 30, 3);
        var target = Instance(100, "ArmoryBody", 1);
        var witness = Instance(200, "ArmoryBody", 2);

        var result = analyzer.Analyze(target, candidate,
            [Job(3, 50, true, 3), Job(21, 50, true, 3)],
            [], [target, witness], Definitions(candidate, classOnly));

        Assert.Equal(EquipmentUseStatus.BaselineNotBetter, result.Status);
        Assert.Empty(Assert.Single(result.Comparisons).WitnessRequirement!.ViableWitnesses);
    }

    [Fact]
    public void EquivalentRetainedInstance_CoversRemovalWithoutStatLoss()
    {
        var definition = Definition(100, 50, 100, 8) with
        {
            Slot = EquipmentSlot.Ears,
            StatProfile = new EquipmentStatProfile([new(70, EquipmentStatSemantic.Craftsmanship, 40, false)], 0, 0, 0, 0, true),
        };
        var target = Instance(100, "ArmoryEar", 1);
        var retained = Instance(100, "ArmoryEar", 2);

        var result = analyzer.Analyze(target, definition,
            [ProfileJob(8, "CRP", "Role 0", EquipmentStatSemantic.Unknown, EquipmentDiscipline.Crafter)],
            [], [target, retained], Definitions(definition));

        Assert.Equal(EquipmentUseStatus.Obsolete, result.Status);
        Assert.Equal(retained.Fingerprint, Assert.Single(Assert.Single(result.Comparisons).WitnessRequirement!.ViableWitnesses).Fingerprint);
    }

    [Fact]
    public void CombatCoverage_AllowsEqualJobRelevantSecondaryBudgetWhenCoreStatsImprove()
    {
        var candidate = Definition(100, 60, 260, 1) with
        {
            Slot = EquipmentSlot.Body,
            StatProfile = new EquipmentStatProfile([
                new(1, EquipmentStatSemantic.Strength, 90, false),
                new(3, EquipmentStatSemantic.Vitality, 95, false),
                new(27, EquipmentStatSemantic.CriticalHit, 80, false),
                new(44, EquipmentStatSemantic.Determination, 20, false)], 0, 0, 200, 200, true),
        };
        var baseline = Definition(200, 62, 265, 1) with
        {
            Slot = EquipmentSlot.Body,
            StatProfile = new EquipmentStatProfile([
                new(1, EquipmentStatSemantic.Strength, 92, false),
                new(3, EquipmentStatSemantic.Vitality, 97, false),
                new(27, EquipmentStatSemantic.CriticalHit, 20, false),
                new(44, EquipmentStatSemantic.Determination, 80, false)], 0, 0, 205, 205, true),
        };
        var target = Instance(100, "ArmoryBody", 1);
        var retained = Instance(200, "ArmoryBody", 2);

        var result = analyzer.Analyze(target, candidate, [Job(1, 70, true)], [],
            [target, retained], Definitions(candidate, baseline));

        Assert.Equal(EquipmentUseStatus.Obsolete, result.Status);
        Assert.Equal(EquipmentCoverageKind.CombatCoreAndSecondaryBudget,
            Assert.Single(Assert.Single(result.Comparisons).WitnessRequirement!.ViableWitnesses).CoverageKind);
    }

    [Fact]
    public void CombatCoverage_RejectsHigherSecondaryBudgetWhenPrimaryStatRegresses()
    {
        var candidate = Definition(100, 60, 260, 1) with
        {
            Slot = EquipmentSlot.Body,
            StatProfile = new EquipmentStatProfile([
                new(1, EquipmentStatSemantic.Strength, 90, false),
                new(3, EquipmentStatSemantic.Vitality, 95, false),
                new(27, EquipmentStatSemantic.CriticalHit, 80, false)], 0, 0, 200, 200, true),
        };
        var baseline = Definition(200, 62, 265, 1) with
        {
            Slot = EquipmentSlot.Body,
            StatProfile = new EquipmentStatProfile([
                new(1, EquipmentStatSemantic.Strength, 89, false),
                new(3, EquipmentStatSemantic.Vitality, 97, false),
                new(44, EquipmentStatSemantic.Determination, 200, false)], 0, 0, 205, 205, true),
        };
        var target = Instance(100, "ArmoryBody", 1);
        var retained = Instance(200, "ArmoryBody", 2);

        var result = analyzer.Analyze(target, candidate, [Job(1, 70, true)], [],
            [target, retained], Definitions(candidate, baseline));

        Assert.Equal(EquipmentUseStatus.BaselineNotBetter, result.Status);
    }

    [Fact]
    public void SpecialPurposeEquipment_IsProtectedBeforeComparison()
    {
        var candidate = Definition(8568, 1, 1, 8) with
        {
            Name = "Ehcatl Wristgloves",
            IsSpecialPurpose = true,
            ItemSpecialBonusId = 1,
        };
        var result = analyzer.AnalyzeNqDefinitionPreview(candidate, [Job(8, 100, true)], [], Definitions(candidate));

        Assert.Equal(EquipmentUseStatus.SpecialPurpose, result.Status);
    }

    [Fact]
    public void EmptyEligibilityMask_IsEvaluationFailureRatherThanNoObtainedJob()
    {
        var candidate = Definition(100, 1, 1, 8) with { EligibleClassJobIds = new HashSet<uint>() };
        var result = analyzer.AnalyzeNqDefinitionPreview(candidate, [Job(8, 100, true)], [], Definitions(candidate));

        Assert.True(result.IsEvaluationFailure);
        Assert.Equal("EligibleJobMaskUnavailable", result.FailureCode);
    }

    [Fact]
    public void UnmodeledEquipRestriction_FailsClosed()
    {
        var candidate = Definition(100, 1, 1, 8) with { GrandCompanyId = 1 };
        var result = analyzer.AnalyzeNqDefinitionPreview(candidate, [Job(8, 100, true)], [], Definitions(candidate));

        Assert.True(result.IsEvaluationFailure);
        Assert.Equal("EquipRestrictionUnmodeled", result.FailureCode);
    }

    [Fact]
    public void SheetDefaultRestrictionAndClassJobUse_DoNotCreateFalseRestriction()
    {
        var definition = Definition(100, 1, 1, 8) with { EquipRestrictionId = 1, ClassJobUseId = 3 };

        Assert.False(definition.HasUnmodeledEquipRestriction);
    }

    [Fact]
    public void AllClassesCraftingItem_IsComparedOnlyToCrafters()
    {
        var candidate = Definition(100, 20, 20, 1, 2) with
        {
            IsAllClasses = true,
            StatProfile = new EquipmentStatProfile([new(70, EquipmentStatSemantic.Craftsmanship, 20, false)], 0, 0, 0, 0, true),
        };
        var upgrade = Definition(200, 30, 30, 2) with
        {
            StatProfile = new EquipmentStatProfile([new(70, EquipmentStatSemantic.Craftsmanship, 30, false)], 0, 0, 0, 0, true),
        };
        var combat = Job(1, 50, true);
        var crafter = Job(2, 50, true) with { Discipline = EquipmentDiscipline.Crafter, PrimaryStat = EquipmentStatSemantic.Unknown };
        var result = analyzer.AnalyzeNqDefinitionPreview(candidate, [combat, crafter], [Gearset(1, 2, 200)], Definitions(candidate, upgrade));

        Assert.True(result.IsStrictlyObsolete);
        Assert.Equal(2u, Assert.Single(result.Comparisons).Job.ClassJobId);
        Assert.Equal("Crafters", EquipmentWearerInference.Infer(candidate).Label);
    }

    [Fact]
    public void CanonicalEquipmentFamilies_NeverCompareAgainstUnrelatedJobs()
    {
        var jobs = new[]
        {
            ProfileJob(1, "PLD", "Tank", EquipmentStatSemantic.Strength, EquipmentDiscipline.Combat),
            ProfileJob(2, "MNK", "Melee DPS", EquipmentStatSemantic.Strength, EquipmentDiscipline.Combat),
            ProfileJob(3, "NIN", "Melee DPS", EquipmentStatSemantic.Dexterity, EquipmentDiscipline.Combat),
            ProfileJob(4, "BRD", "Physical Ranged DPS", EquipmentStatSemantic.Dexterity, EquipmentDiscipline.Combat),
            ProfileJob(5, "BLM", "Magical Ranged DPS", EquipmentStatSemantic.Intelligence, EquipmentDiscipline.Combat),
            ProfileJob(6, "WHM", "Healer", EquipmentStatSemantic.Mind, EquipmentDiscipline.Combat),
            ProfileJob(7, "CRP", "Role 0", EquipmentStatSemantic.Unknown, EquipmentDiscipline.Crafter),
            ProfileJob(8, "MIN", "Role 0", EquipmentStatSemantic.Unknown, EquipmentDiscipline.Gatherer),
        };
        var cases = new[]
        {
            ("Test Coat of Fending", EquipmentStatSemantic.Strength, 1u, "Tanks"),
            ("Test Coat of Slaying", EquipmentStatSemantic.Strength, 2u, "Strength melee DPS"),
            ("Test Coat of Scouting", EquipmentStatSemantic.Dexterity, 3u, "Dexterity melee DPS"),
            ("Test Coat of Aiming", EquipmentStatSemantic.Dexterity, 4u, "Physical ranged DPS"),
            ("Test Coat of Casting", EquipmentStatSemantic.Intelligence, 5u, "Magical DPS"),
            ("Test Coat of Healing", EquipmentStatSemantic.Mind, 6u, "Healers"),
            ("Test Coat of Crafting", EquipmentStatSemantic.Craftsmanship, 7u, "Crafters"),
            ("Test Coat of Gathering", EquipmentStatSemantic.Gathering, 8u, "Gatherers"),
        };

        foreach (var (name, stat, expectedJobId, expectedLabel) in cases)
        {
            var candidate = Definition(100, 20, 20, jobs.Select(job => job.ClassJobId).ToArray()) with
            {
                Name = name,
                IsAllClasses = true,
                StatProfile = Stat(stat, 20),
            };
            var upgrade = Definition(200, 30, 30, jobs.Select(job => job.ClassJobId).ToArray()) with
            {
                Name = name.Replace("Test", "Upgrade"),
                IsAllClasses = true,
                StatProfile = Stat(stat, 30),
            };
            var candidateInstance = Instance(100, "Inventory1", 1);
            var result = analyzer.Analyze(candidateInstance, candidate, jobs, [],
                [candidateInstance, Instance(200, "ArmoryBody", 2)], Definitions(candidate, upgrade));

            Assert.True(result.IsStrictlyObsolete, name);
            Assert.Equal(expectedJobId, Assert.Single(result.Comparisons).Job.ClassJobId);
            Assert.Equal(expectedLabel, EquipmentWearerInference.Infer(candidate).Label);
        }
    }

    [Fact]
    public void AimingAccessory_IncludesDexterityMeleeButExcludesStrengthAndCasters()
    {
        var definition = Definition(100, 20, 20, 1, 2, 3) with
        {
            Name = "Test Bracelet of Aiming",
            Slot = EquipmentSlot.Wrists,
            IsAllClasses = true,
            StatProfile = Stat(EquipmentStatSemantic.Dexterity, 20),
        };
        var inference = EquipmentWearerInference.Infer(definition);

        Assert.Equal("Dexterity DPS", inference.Label);
        Assert.True(inference.Matches(ProfileJob(1, "NIN", "Melee DPS", EquipmentStatSemantic.Dexterity, EquipmentDiscipline.Combat), definition.Slot));
        Assert.True(inference.Matches(ProfileJob(2, "BRD", "Physical Ranged DPS", EquipmentStatSemantic.Dexterity, EquipmentDiscipline.Combat), definition.Slot));
        Assert.False(inference.Matches(ProfileJob(3, "MNK", "Melee DPS", EquipmentStatSemantic.Strength, EquipmentDiscipline.Combat), definition.Slot));
        Assert.False(inference.Matches(ProfileJob(4, "BLM", "Magical Ranged DPS", EquipmentStatSemantic.Intelligence, EquipmentDiscipline.Combat), definition.Slot));
    }

    [Fact]
    public void MaimingAndHistoricalStrikingFamilies_UseTheirGameCategoryJobSets()
    {
        var maiming = EquipmentWearerInference.Infer(Definition(100, 20, 20, 1) with { Name = "Test Coat of Maiming" });
        Assert.True(maiming.Matches(ProfileJob(1, "DRG", "Melee DPS", EquipmentStatSemantic.Strength, EquipmentDiscipline.Combat), EquipmentSlot.Body));
        Assert.True(maiming.Matches(ProfileJob(2, "RPR", "Melee DPS", EquipmentStatSemantic.Strength, EquipmentDiscipline.Combat), EquipmentSlot.Body));
        Assert.False(maiming.Matches(ProfileJob(3, "MNK", "Melee DPS", EquipmentStatSemantic.Strength, EquipmentDiscipline.Combat), EquipmentSlot.Body));

        var striking = EquipmentWearerInference.Infer(Definition(100, 20, 20, 1) with { Name = "Test Coat of Striking" });
        Assert.True(striking.Matches(ProfileJob(1, "MNK", "Melee DPS", EquipmentStatSemantic.Strength, EquipmentDiscipline.Combat), EquipmentSlot.Body));
        Assert.True(striking.Matches(ProfileJob(2, "NIN", "Melee DPS", EquipmentStatSemantic.Dexterity, EquipmentDiscipline.Combat), EquipmentSlot.Body));
        Assert.False(striking.Matches(ProfileJob(3, "DRG", "Melee DPS", EquipmentStatSemantic.Strength, EquipmentDiscipline.Combat), EquipmentSlot.Body));
    }

    [Fact]
    public void AllClassesStrengthItem_NarrowsToStrengthCombatJobs()
    {
        var candidate = Definition(100, 20, 20, 1, 2) with { IsAllClasses = true };
        var upgrade = Definition(200, 30, 30, 1);
        var strength = Job(1, 50, true);
        var caster = Job(2, 50, true) with { Role = "Magical Ranged DPS", PrimaryStat = EquipmentStatSemantic.Intelligence };
        var result = analyzer.AnalyzeNqDefinitionPreview(candidate, [strength, caster], [Gearset(1, 1, 200)], Definitions(candidate, upgrade));

        Assert.True(result.IsStrictlyObsolete);
        Assert.Equal(1u, Assert.Single(result.Comparisons).Job.ClassJobId);
    }

    [Fact]
    public void GearsetProtection_TracksQualityAndRequiredMultiplicity()
    {
        var index = GearsetProtectionIndex.Create([new GearsetSnapshot(1, "Set", 1, [new(EquipmentSlot.Body, 100, false)], true)]);
        var first = Instance(100, "ArmoryBody", 1);
        var second = Instance(100, "Inventory1", 12);
        var highQuality = Instance(100, "Inventory1", 13, true);
        Assert.True(index.IsProtected(first.Fingerprint.ItemId, false, 1));
        Assert.False(index.IsProtected(first.Fingerprint.ItemId, false, 2));
        Assert.False(index.IsProtected(highQuality.Fingerprint.ItemId, true, 1));
        Assert.True(index.RetainsRequiredMultiplicity([first]));
        Assert.False(index.RetainsRequiredMultiplicity([highQuality]));
        Assert.True(index.DoesNotReduceRequiredMultiplicity([highQuality], [highQuality]));
        Assert.True(index.DoesNotReduceRequiredMultiplicity([first, second], [second]));
        Assert.False(index.DoesNotReduceRequiredMultiplicity([first, second], []));
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

    [Fact]
    public void OwnedCandidateWithNoUsableBetterWitness_IsAValidProtectionConclusion()
    {
        var candidate = Definition(100, 10, 10, 1);
        var instance = Instance(100, "ArmoryLegs", 1);

        var result = analyzer.Analyze(instance, candidate, [Job(1, 28, true)], [], [instance], Definitions(candidate));

        Assert.Equal(EquipmentUseStatus.BaselineNotBetter, result.Status);
        var comparison = Assert.Single(result.Comparisons);
        Assert.Null(comparison.Baseline);
        Assert.Empty(comparison.WitnessRequirement!.ViableWitnesses);
        Assert.Contains("No retained owned", comparison.Diagnostic);
    }

    [Fact]
    public void FutureUse_DuplicateNonRingProtectsOnlyOneRetainedCopy()
    {
        var candidate = Definition(100, 40, 40, 1);
        var first = Instance(100, "ArmoryEar", 1, highQuality: true);
        var second = Instance(100, "ArmoryEar", 2, highQuality: true);
        candidate = candidate with
        {
            Slot = EquipmentSlot.Ears,
            HighQualityStatProfile = candidate.StatProfile,
        };

        var withDuplicate = analyzer.Analyze(first, candidate, [Job(1, 30, true)], [], [first, second], Definitions(candidate));
        var onlyCopy = analyzer.Analyze(first, candidate, [Job(1, 30, true)], [], [first], Definitions(candidate));

        Assert.Equal(EquipmentUseStatus.Obsolete, withDuplicate.Status);
        Assert.Equal(second.Fingerprint, Assert.Single(withDuplicate.Comparisons[0].WitnessRequirement!.ViableWitnesses).Fingerprint);
        Assert.Equal(EquipmentUseStatus.FutureUse, onlyCopy.Status);
    }

    [Fact]
    public void SavedGearsetAnchor_DoesNotExcludeBetterOwnedWitness()
    {
        var candidate = Definition(100, 20, 25, 1);
        var staleGearsetItem = Definition(200, 20, 20, 1);
        var ownedUpgrade = Definition(300, 30, 40, 1);
        var candidateInstance = Instance(100, "ArmoryBody", 1);
        var staleInstance = Instance(200, "ArmoryBody", 2);
        var upgradeInstance = Instance(300, "ArmoryBody", 3);

        var result = analyzer.Analyze(
            candidateInstance,
            candidate,
            [Job(1, 50, true)],
            [Gearset(1, 1, 200)],
            [candidateInstance, staleInstance, upgradeInstance],
            Definitions(candidate, staleGearsetItem, ownedUpgrade));

        Assert.Equal(EquipmentUseStatus.Obsolete, result.Status);
        var comparison = Assert.Single(result.Comparisons);
        Assert.Equal(300u, comparison.Baseline!.ItemId);
        Assert.Equal(EquipmentComparisonBasis.SynthesizedOwnedLoadout, comparison.Basis);
        Assert.Contains(comparison.WitnessRequirement!.ViableWitnesses, witness => witness.ItemId == 300 && !witness.IsGearsetReferenced);
    }

    [Fact]
    public void DefinitionPreview_DoesNotTreatHighestItemLevelSavedItemAsBest()
    {
        var candidate = Definition(100, 20, 20, 1);
        var higherItemLevelButWrongStats = Definition(200, 40, 50, 1) with
        {
            StatProfile = new EquipmentStatProfile(
                [new(6, EquipmentStatSemantic.Piety, 50, false)],
                50, 0, 50, 50, true),
        };
        var lowerItemLevelCoveringWitness = Definition(300, 30, 30, 1);

        var result = analyzer.AnalyzeNqDefinitionPreview(
            candidate,
            [Job(1, 50, true)],
            [Gearset(1, 1, 200), Gearset(2, 1, 300)],
            Definitions(candidate, higherItemLevelButWrongStats, lowerItemLevelCoveringWitness));

        Assert.Equal(EquipmentUseStatus.Obsolete, result.Status);
        Assert.Equal(300u, Assert.Single(result.Comparisons).Baseline!.ItemId);
    }

    [Fact]
    public void DefinitionPreview_UsesSavedWitnessExactQuality()
    {
        var candidate = Definition(100, 20, 25, 1);
        var witness = Definition(200, 20, 20, 1) with
        {
            HighQualityStatProfile = new EquipmentStatProfile(
                [new(1, EquipmentStatSemantic.Strength, 30, false)],
                30, 0, 30, 30, true),
        };
        var gearset = new GearsetSnapshot(1, "HQ set", 1, [new(EquipmentSlot.Body, 200, true)], true);

        var result = analyzer.AnalyzeNqDefinitionPreview(candidate, [Job(1, 50, true)], [gearset], Definitions(candidate, witness));

        Assert.Equal(EquipmentUseStatus.Obsolete, result.Status);
    }

    [Fact]
    public void DefinitionPreview_RingRequiresTwoSavedWitnessesInOneGearset()
    {
        var candidate = Definition(100, 20, 20, 1) with
        {
            Slot = EquipmentSlot.Ring,
            FitsLeftRing = true,
            FitsRightRing = true,
        };
        var witness = Definition(200, 30, 30, 1) with
        {
            Slot = EquipmentSlot.Ring,
            FitsLeftRing = true,
            FitsRightRing = true,
        };
        var gearset = new GearsetSnapshot(1, "One-ring set", 1, [new(EquipmentSlot.Ring, 200)], true);

        var result = analyzer.AnalyzeNqDefinitionPreview(candidate, [Job(1, 50, true)], [gearset], Definitions(candidate, witness));

        Assert.Equal(EquipmentUseStatus.BaselineNotBetter, result.Status);
    }

    [Fact]
    public void ShieldWithLowerBlockRate_DoesNotCoverCandidateDespiteHigherDefense()
    {
        var candidate = Definition(100, 20, 20, 1) with { Slot = EquipmentSlot.OffHand };
        var baseline = Definition(200, 30, 30, 1) with { Slot = EquipmentSlot.OffHand };
        var candidateStats = candidate.StatProfile! with { BlockStrength = 100, BlockRate = 100 };
        var baselineStats = baseline.StatProfile! with { BlockStrength = 120, BlockRate = 90 };

        var coverage = EquipmentUseAnalyzer.EvaluateCoverage(baseline, baselineStats, candidate, candidateStats, Job(1, 50, true));

        Assert.Equal(EquipmentCoverageKind.None, coverage);
    }

    [Fact]
    public void ShieldWithLowerDefense_DoesNotCoverCandidateDespiteHigherBlock()
    {
        var candidate = Definition(100, 20, 20, 1) with { Slot = EquipmentSlot.OffHand };
        var baseline = Definition(200, 30, 30, 1) with { Slot = EquipmentSlot.OffHand };
        var candidateStats = candidate.StatProfile! with { PhysicalDefense = 30, MagicalDefense = 30, BlockStrength = 100, BlockRate = 100 };
        var baselineStats = baseline.StatProfile! with { PhysicalDefense = 29, MagicalDefense = 40, BlockStrength = 120, BlockRate = 120 };

        var coverage = EquipmentUseAnalyzer.EvaluateCoverage(baseline, baselineStats, candidate, candidateStats, Job(1, 50, true));

        Assert.Equal(EquipmentCoverageKind.None, coverage);
    }

    [Fact]
    public void MainHandWithDifferentDelay_UsesDamageAndStatsRatherThanExactDelayEquality()
    {
        var candidate = Definition(100, 20, 20, 1) with { Slot = EquipmentSlot.MainHand };
        var baseline = Definition(200, 30, 30, 1) with { Slot = EquipmentSlot.MainHand };
        var candidateStats = candidate.StatProfile! with { DelayMilliseconds = 2400 };
        var baselineStats = baseline.StatProfile! with { DelayMilliseconds = 2800 };

        var coverage = EquipmentUseAnalyzer.EvaluateCoverage(baseline, baselineStats, candidate, candidateStats, Job(1, 50, true));

        Assert.Equal(EquipmentCoverageKind.ComponentwiseNoLoss, coverage);
    }

    [Theory]
    [InlineData(EquipmentStatSemantic.Piety, EquipmentStatSemantic.CriticalHit, "Healer", EquipmentStatSemantic.Mind)]
    [InlineData(EquipmentStatSemantic.Tenacity, EquipmentStatSemantic.DirectHit, "Tank", EquipmentStatSemantic.Strength)]
    [InlineData(EquipmentStatSemantic.SkillSpeed, EquipmentStatSemantic.Determination, "Melee DPS", EquipmentStatSemantic.Strength)]
    public void CombatUtilitySecondary_CannotBeReplacedByOffensiveBudget(
        EquipmentStatSemantic protectedStat,
        EquipmentStatSemantic replacementStat,
        string role,
        EquipmentStatSemantic primaryStat)
    {
        var candidate = Definition(100, 20, 20, 1);
        var baseline = Definition(200, 30, 30, 1);
        var candidateStats = candidate.StatProfile! with
        {
            Parameters = [new(1, protectedStat, 10, false)],
        };
        var baselineStats = baseline.StatProfile! with
        {
            Parameters = [new(2, replacementStat, 20, false)],
        };
        var job = Job(1, 50, true) with { Role = role, PrimaryStat = primaryStat };

        var coverage = EquipmentUseAnalyzer.EvaluateCoverage(baseline, baselineStats, candidate, candidateStats, job);

        Assert.Equal(EquipmentCoverageKind.None, coverage);
    }

    [Fact]
    public void SpecialPurposeEquipment_IsProtectedBeforeOrdinaryStatEvaluation()
    {
        var candidate = Definition(100, 1, 1, 1) with
        {
            IsAllClasses = true,
            IsSpecialPurpose = true,
            StatProfile = new EquipmentStatProfile([new(69, EquipmentStatSemantic.Unknown, 1, false, "Utility")], 0, 0, 0, 0, false),
        };

        var result = analyzer.AnalyzeNqDefinitionPreview(candidate, [Job(1, 100, true)], [], Definitions(candidate));

        Assert.Equal(EquipmentUseStatus.SpecialPurpose, result.Status);
        Assert.False(result.IsEvaluationFailure);
    }

    [Fact]
    public void AllClassesDefenseOnlyEquipment_IsCosmeticRatherThanAnEvaluationFailure()
    {
        var candidate = Definition(100, 1, 1, 1) with
        {
            IsAllClasses = true,
            StatProfile = new EquipmentStatProfile([], 0, 0, 7, 13, true),
        };

        var result = analyzer.AnalyzeNqDefinitionPreview(candidate, [Job(1, 100, true)], [], Definitions(candidate));

        Assert.Equal(EquipmentUseStatus.LikelyCosmetic, result.Status);
    }

    private static CharacterJobSnapshot Job(uint id, uint level, bool? unlocked, uint? parentId = null) =>
        new(id, $"J{id}", $"Job {id}", level, unlocked, parentId, "Tank", EquipmentStatSemantic.Strength, EquipmentDiscipline.Combat);

    private static CharacterJobSnapshot ProfileJob(uint id, string abbreviation, string role, EquipmentStatSemantic primary, EquipmentDiscipline discipline) =>
        new(id, abbreviation, abbreviation, 100, true, id, role, primary, discipline);

    private static EquipmentStatProfile Stat(EquipmentStatSemantic semantic, int value) =>
        new([new(1, semantic, value, false)], 0, 0, 0, 0, true);

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
