using Franthropy.Dalamud.Characters;

namespace Franthropy.Dalamud.Equipment;

public enum EquipmentUseStatus
{
    Obsolete,
    FutureUse,
    BaselineNotBetter,
    NoObtainedEligibleJob,
    LikelyCosmetic,
    SpecialPurpose,
    EvaluationFailure,
}

public enum EquipmentComparisonBasis
{
    SavedGearset,
    SynthesizedOwnedLoadout,
}

public sealed record EquipmentJobComparison(
    CharacterJobSnapshot Job,
    EquipmentUseStatus Status,
    EquipmentItemDefinition? Baseline,
    IReadOnlyList<GearsetSnapshot> ContributingGearsets,
    string? Diagnostic = null,
    EquipmentWitnessRequirement? WitnessRequirement = null,
    EquipmentComparisonBasis Basis = EquipmentComparisonBasis.SynthesizedOwnedLoadout,
    IReadOnlyList<string>? RejectedGearsets = null);

public sealed record EquipmentUseAnalysis(
    EquipmentUseStatus Status,
    IReadOnlyList<EquipmentJobComparison> Comparisons,
    string? FailureCode = null,
    string? Diagnostic = null)
{
    public bool IsStrictlyObsolete => Status == EquipmentUseStatus.Obsolete;
    public bool IsEvaluationFailure => Status == EquipmentUseStatus.EvaluationFailure;
}

public sealed class EquipmentUseAnalyzer
{
    public EquipmentUseAnalysis AnalyzeNqDefinitionPreview(
        EquipmentItemDefinition candidate,
        IReadOnlyList<CharacterJobSnapshot> jobs,
        IReadOnlyList<GearsetSnapshot> gearsets,
        IReadOnlyDictionary<uint, EquipmentItemDefinition> definitions)
        => AnalyzeCore(null, candidate, jobs, gearsets, [], definitions);

    public EquipmentUseAnalysis Analyze(
        EquipmentInstanceSnapshot candidateInstance,
        EquipmentItemDefinition candidate,
        IReadOnlyList<CharacterJobSnapshot> jobs,
        IReadOnlyList<GearsetSnapshot> gearsets,
        IReadOnlyList<EquipmentInstanceSnapshot> ownedInstances,
        IReadOnlyDictionary<uint, EquipmentItemDefinition> definitions)
        => AnalyzeCore(candidateInstance, candidate, jobs, gearsets, ownedInstances, definitions);

    private EquipmentUseAnalysis AnalyzeCore(
        EquipmentInstanceSnapshot? candidateInstance,
        EquipmentItemDefinition candidate,
        IReadOnlyList<CharacterJobSnapshot> jobs,
        IReadOnlyList<GearsetSnapshot> gearsets,
        IReadOnlyList<EquipmentInstanceSnapshot> ownedInstances,
        IReadOnlyDictionary<uint, EquipmentItemDefinition> definitions)
    {
        if (candidate.IsSpecialPurpose)
            return new(EquipmentUseStatus.SpecialPurpose, [], Diagnostic: $"{candidate.Name} carries a special equipment bonus or action and is protected independently of stat dominance.");
        if (candidate.EligibleClassJobIds.Count == 0)
            return Failure("EligibleJobMaskUnavailable", $"{candidate.Name} is equipment, but no class/job eligibility mask was captured.");
        if (candidate.HasUnmodeledEquipRestriction)
            return Failure("EquipRestrictionUnmodeled", $"{candidate.Name} has a race, sex, company, or PvP-rank restriction that is not yet proven for this character.");
        var candidateStats = candidateInstance is null ? candidate.StatProfile : EquipmentInstanceStats.Resolve(candidateInstance, candidate);
        if (candidateStats is not { IsComplete: true })
        {
            var unknownIds = candidateStats?.Parameters
                .Where(value => value.Semantic == EquipmentStatSemantic.Unknown)
                .Select(value => $"{value.BaseParamId} ({value.SourceName ?? "unnamed"})").Distinct().Order().ToArray() ?? [];
            return Failure("IncompleteStatProfile", $"{candidate.Name} has no complete functional stat profile. Unmapped BaseParams: {(unknownIds.Length == 0 ? "none recorded" : string.Join(", ", unknownIds))}.");
        }
        if (candidate.IsAllClasses && EquipmentWearerInference.Infer(candidate).Kind == EquipmentWearerKind.Cosmetic)
            return new(EquipmentUseStatus.LikelyCosmetic, [], Diagnostic: "All Classes equipment has no wearer-defining stats.");

        var eligibleFamilies = jobs
            .Where(job => candidate.EligibleClassJobIds.Contains(job.ClassJobId))
            .GroupBy(job => job.ParentClassJobId ?? job.ClassJobId)
            .Select(group => group.ToArray())
            .ToArray();
        if (eligibleFamilies.Any(family => !family.Any(job => job.IsUnlocked == true) && family.Any(job => job.IsUnlocked is null)))
            return Failure("JobUnlockStateUnavailable", "An eligible job family has no conclusive obtained-state observation.");

        var obtainedFamilies = eligibleFamilies.Where(family => family.Any(job => job.IsUnlocked == true)).ToArray();
        if (EquipmentWearerInference.RequiresIntentRefinement(candidate, jobs))
        {
            var inference = EquipmentWearerInference.Infer(candidate);
            if (inference.Kind == EquipmentWearerKind.Unknown)
                return Failure("WearerInferenceUnavailable", $"{candidate.Name} has a broad equip mask, but its intended wearer cannot be inferred safely ({inference.Label}).");
            obtainedFamilies = obtainedFamilies
                .Where(family => EquipmentWearerInference.MatchesIntendedWearer(candidate, Representative(family), jobs))
                .ToArray();
        }
        if (obtainedFamilies.Length == 0)
        {
            return new(EquipmentUseStatus.NoObtainedEligibleJob, []);
        }

        var comparisons = obtainedFamilies.Select(family => Compare(candidateInstance, candidate, candidateStats, family, jobs, gearsets, ownedInstances, definitions)).ToArray();
        var failure = comparisons.FirstOrDefault(value => value.Status == EquipmentUseStatus.EvaluationFailure);
        if (failure is not null)
            return Failure("JobComparisonFailed", failure.Diagnostic ?? $"Unable to compare {failure.Job.Abbreviation}.", comparisons);
        var overall = comparisons.All(comparison => comparison.Status == EquipmentUseStatus.Obsolete)
            ? EquipmentUseStatus.Obsolete
            : comparisons.First(comparison => comparison.Status != EquipmentUseStatus.Obsolete).Status;
        return new(overall, comparisons);
    }

    private static EquipmentJobComparison Compare(
        EquipmentInstanceSnapshot? candidateInstance,
        EquipmentItemDefinition candidate,
        EquipmentStatProfile candidateStats,
        IReadOnlyList<CharacterJobSnapshot> family,
        IReadOnlyList<CharacterJobSnapshot> allJobs,
        IReadOnlyList<GearsetSnapshot> gearsets,
        IReadOnlyList<EquipmentInstanceSnapshot> ownedInstances,
        IReadOnlyDictionary<uint, EquipmentItemDefinition> definitions)
    {
        var job = Representative(family);
        var futureUse = job.Level < candidate.EquipLevel;
        if (futureUse && candidateInstance is null)
            return new(job, EquipmentUseStatus.FutureUse, null, []);
        var comparisonLevel = WitnessComparisonLevel(candidate, job);
        var relevant = RelevantStats(job);
        if (relevant.Count == 0)
            return new(job, EquipmentUseStatus.EvaluationFailure, null, [], $"No supported stat profile exists for {job.Abbreviation}.");

        var familyIds = family.Select(value => value.ClassJobId).ToHashSet();
        if (candidate.Slot == EquipmentSlot.MainHand && !IsSupportedMainHand(candidate))
            return new(job, EquipmentUseStatus.EvaluationFailure, null, [], $"Unsupported main/off-hand occupancy metadata {candidate.MainHandOccupancy}/{candidate.OffHandOccupancy} for {candidate.Name}.");
        if (candidate.Slot == EquipmentSlot.OffHand && candidate.OffHandOccupancy != 1)
            return new(job, EquipmentUseStatus.EvaluationFailure, null, [], $"Unsupported off-hand occupancy metadata for {candidate.Name}.");
        if (candidate.Slot == EquipmentSlot.Ring && (!candidate.FitsLeftRing || !candidate.FitsRightRing))
            return new(job, EquipmentUseStatus.EvaluationFailure, null, [], $"Incomplete ring-slot compatibility metadata for {candidate.Name}.");
        var contributing = gearsets.Where(set => set.IsValid && familyIds.Contains(set.ClassJobId))
            .Where(set => set.Items.Any(item => item.Slot == candidate.Slot)).ToArray();
        if (candidateInstance is null)
        {
            var savedWitnesses = contributing
                .SelectMany(set => set.Items.Where(item => item.Slot == candidate.Slot)
                    .Select(reference => (Set: set, Reference: reference)))
                .Where(value => definitions.ContainsKey(value.Reference.ItemId))
                .Select(value => (value.Set, value.Reference, Definition: definitions[value.Reference.ItemId]))
                .Where(value => IsEligibleWitness(candidate, value.Definition, job, allJobs))
                .Where(value => candidate.Slot != EquipmentSlot.MainHand ||
                                (value.Definition.MainHandOccupancy == candidate.MainHandOccupancy &&
                                 value.Definition.OffHandOccupancy == candidate.OffHandOccupancy))
                .Where(value => candidate.Slot != EquipmentSlot.OffHand || value.Definition.OffHandOccupancy == 1)
                .Where(value => candidate.Slot != EquipmentSlot.Ring ||
                                (value.Definition.FitsLeftRing && value.Definition.FitsRightRing))
                .Select(value => (value.Set, value.Reference, value.Definition, Stats: value.Reference.IsHighQuality == true
                    ? value.Definition.HighQualityStatProfile
                    : value.Definition.StatProfile))
                .ToArray();
            if (savedWitnesses.Length == 0)
                return new(job, EquipmentUseStatus.EvaluationFailure, null, contributing, $"No saved {candidate.Slot} witness was found for {job.Abbreviation}.", Basis: EquipmentComparisonBasis.SavedGearset);
            // A saved gearset proves that an item was intentionally assigned to this job and slot;
            // it does not prove best-owned status. Any saved witness that independently passes the
            // full no-loss comparison is sufficient, regardless of item level or gearset ordering.
            var coveringWitnesses = savedWitnesses
                .Where(value => value.Stats is { IsComplete: true } &&
                                EvaluateCoverage(value.Definition, value.Stats, candidate, candidateStats, job) != EquipmentCoverageKind.None)
                .ToArray();
            var hasCoverage = candidate.Slot != EquipmentSlot.Ring
                ? coveringWitnesses.Length > 0
                : coveringWitnesses.GroupBy(value => value.Set.GearsetId).Any(group =>
                {
                    var values = group.ToArray();
                    for (var left = 0; left < values.Length; left++)
                        for (var right = left + 1; right < values.Length; right++)
                            if (values[left].Definition.ItemId != values[right].Definition.ItemId || !values[left].Definition.IsUnique)
                                return true;
                    return false;
                });
            if (!hasCoverage && savedWitnesses.Any(value => value.Stats is not { IsComplete: true }))
                return new(job, EquipmentUseStatus.EvaluationFailure, null, contributing,
                    $"A potentially relevant saved {job.Abbreviation} witness has an incomplete exact-quality stat profile.", Basis: EquipmentComparisonBasis.SavedGearset);
            var displayedWitness = coveringWitnesses.OrderByDescending(value => value.Definition.ItemLevel).Select(value => value.Definition).FirstOrDefault();
            return new(job, hasCoverage ? EquipmentUseStatus.Obsolete : EquipmentUseStatus.BaselineNotBetter, hasCoverage ? displayedWitness : null, contributing,
                hasCoverage ? null : $"No feasible saved {job.Abbreviation} witness set covers {candidate.Name} without losing a relevant intrinsic stat.",
                Basis: EquipmentComparisonBasis.SavedGearset);
        }

        var gearsetReferences = contributing.SelectMany(set => set.Items.Where(item => item.Slot == candidate.Slot)).ToArray();
        var basis = gearsetReferences.Length > 0
            ? EquipmentComparisonBasis.SavedGearset
            : EquipmentComparisonBasis.SynthesizedOwnedLoadout;
        var gearsetItemIds = gearsetReferences.Select(item => item.ItemId).ToHashSet();
        var usable = new List<(EquipmentInstanceSnapshot Instance, EquipmentItemDefinition Definition, EquipmentStatProfile Stats, bool Gearset)>();
        var incompleteWitnessNames = new HashSet<string>(StringComparer.Ordinal);
        var rejectedGearsets = new List<string>();
        IReadOnlyList<EquipmentInstanceSnapshot> sourceInstances = ownedInstances;
        if (basis == EquipmentComparisonBasis.SavedGearset)
        {
            var feasibleReferences = new List<GearsetItemReference>();
            foreach (var gearset in contributing)
            {
                var references = gearset.Items.Where(item => item.Slot == candidate.Slot).ToArray();
                string? rejection = null;
                foreach (var referenceGroup in references.GroupBy(value => new { value.ItemId, value.IsHighQuality }))
                {
                    if (!definitions.TryGetValue(referenceGroup.Key.ItemId, out var referenceDefinition))
                    {
                        rejection = $"item {referenceGroup.Key.ItemId} has no definition";
                        break;
                    }
                    if (!IsEligibleWitness(candidate, referenceDefinition, job, allJobs))
                    {
                        rejection = $"item {referenceGroup.Key.ItemId} is not a semantically compatible {candidate.Slot} anchor";
                        break;
                    }
                    if (candidate.Slot == EquipmentSlot.MainHand &&
                        (referenceDefinition.MainHandOccupancy != candidate.MainHandOccupancy ||
                         referenceDefinition.OffHandOccupancy != candidate.OffHandOccupancy))
                    {
                        rejection = $"item {referenceGroup.Key.ItemId} has incompatible hand occupancy";
                        break;
                    }
                    if (candidate.Slot == EquipmentSlot.OffHand && referenceDefinition.OffHandOccupancy != 1)
                    {
                        rejection = $"item {referenceGroup.Key.ItemId} is not a valid off-hand configuration";
                        break;
                    }
                    if (candidate.Slot == EquipmentSlot.Ring && (!referenceDefinition.FitsLeftRing || !referenceDefinition.FitsRightRing))
                    {
                        rejection = $"item {referenceGroup.Key.ItemId} cannot occupy both ring slots";
                        break;
                    }
                    var referenceProfile = referenceGroup.Key.IsHighQuality == true
                        ? referenceDefinition.HighQualityStatProfile
                        : referenceDefinition.StatProfile;
                    if (referenceProfile is not { IsComplete: true })
                    {
                        rejection = $"item {referenceGroup.Key.ItemId} has an incomplete exact-quality stat profile";
                        break;
                    }
                    var availableCount = ownedInstances.Count(instance =>
                        !EquipmentInstanceFingerprintComparer.Instance.Equals(instance.Fingerprint, candidateInstance.Fingerprint) &&
                        instance.Fingerprint.ItemId == referenceGroup.Key.ItemId &&
                        (referenceGroup.Key.IsHighQuality is null || instance.Fingerprint.IsHighQuality == referenceGroup.Key.IsHighQuality));
                    if (availableCount < referenceGroup.Count())
                    {
                        rejection = $"requires {referenceGroup.Count()} exact-quality instance(s) of item {referenceGroup.Key.ItemId}, but {availableCount} were observed";
                        break;
                    }
                }
                if (rejection is null)
                    feasibleReferences.AddRange(references);
                else
                    rejectedGearsets.Add($"{gearset.Name}: {rejection}");
            }
            if (feasibleReferences.Count == 0)
            {
                basis = EquipmentComparisonBasis.SynthesizedOwnedLoadout;
                gearsetReferences = [];
                gearsetItemIds = [];
                sourceInstances = ownedInstances;
            }
            else
            {
                gearsetReferences = feasibleReferences.Distinct().ToArray();
                gearsetItemIds = gearsetReferences.Select(item => item.ItemId).ToHashSet();
                // A gearset proves intentional job/slot assignment, not best-owned status. Keep every
                // semantically compatible owned item in the witness pool and merely tag exact anchors.
                sourceInstances = ownedInstances;
            }
        }
        foreach (var instance in sourceInstances)
        {
            if (EquipmentInstanceFingerprintComparer.Instance.Equals(instance.Fingerprint, candidateInstance.Fingerprint))
                continue;
            var isGearsetReference = gearsetReferences.Any(reference =>
                reference.ItemId == instance.Fingerprint.ItemId &&
                (reference.IsHighQuality is null || reference.IsHighQuality == instance.Fingerprint.IsHighQuality));
            if (!definitions.TryGetValue(instance.Fingerprint.ItemId, out var definition) ||
                !IsEligibleWitness(candidate, definition, job, allJobs))
            {
                if (isGearsetReference)
                    return new(job, EquipmentUseStatus.EvaluationFailure, null, contributing,
                            $"A saved {job.Abbreviation} gearset anchor is not a semantically valid {candidate.Slot} item for that job.", Basis: basis, RejectedGearsets: rejectedGearsets);
                continue;
            }
            if (candidate.Slot == EquipmentSlot.MainHand &&
                (definition.MainHandOccupancy != candidate.MainHandOccupancy || definition.OffHandOccupancy != candidate.OffHandOccupancy))
            {
                if (isGearsetReference)
                    return new(job, EquipmentUseStatus.EvaluationFailure, null, contributing,
                        $"The saved {job.Abbreviation} main-hand anchor has incompatible hand occupancy.", Basis: basis);
                continue;
            }
            if (candidate.Slot == EquipmentSlot.OffHand && definition.OffHandOccupancy != 1)
            {
                if (isGearsetReference)
                    return new(job, EquipmentUseStatus.EvaluationFailure, null, contributing,
                        $"The saved {job.Abbreviation} off-hand anchor has incompatible occupancy.", Basis: basis);
                continue;
            }
            if (candidate.Slot == EquipmentSlot.Ring && (!definition.FitsLeftRing || !definition.FitsRightRing))
            {
                if (isGearsetReference)
                    return new(job, EquipmentUseStatus.EvaluationFailure, null, contributing,
                        $"A saved {job.Abbreviation} ring anchor is not compatible with both ring slots.", Basis: basis);
                continue;
            }
            var stats = EquipmentInstanceStats.Resolve(instance, definition);
            if (stats is not { IsComplete: true })
            {
                if (isGearsetReference)
                    return new(job, EquipmentUseStatus.EvaluationFailure, null, contributing,
                        $"The saved {job.Abbreviation} {candidate.Slot} anchor {definition.Name} has an incomplete intrinsic stat profile.", Basis: basis);
                incompleteWitnessNames.Add(definition.Name);
                continue;
            }
            usable.Add((instance, definition, stats, isGearsetReference));
        }
        if (usable.Count == 0)
        {
            if (incompleteWitnessNames.Count > 0)
                return new(job, EquipmentUseStatus.EvaluationFailure, null, contributing,
                    $"No complete {candidate.Slot} witness was available for {job.Abbreviation}. Incomplete prospective witnesses: {string.Join(", ", incompleteWitnessNames.Order())}.", Basis: basis, RejectedGearsets: rejectedGearsets);
            return new(job, futureUse ? EquipmentUseStatus.FutureUse : EquipmentUseStatus.BaselineNotBetter, null, contributing,
                $"No retained owned {candidate.Slot} item is both usable by {job.Abbreviation} by level {comparisonLevel} and covers {candidate.Name} without relevant-stat loss.",
                new EquipmentWitnessRequirement(job, candidate.Slot, candidate.Slot == EquipmentSlot.Ring ? 2 : 1, []), basis, rejectedGearsets);
        }

        var dominating = usable.Select(value => (Value: value, Coverage: EvaluateCoverage(value.Definition, value.Stats, candidate, candidateStats, job)))
            .Where(value => value.Coverage != EquipmentCoverageKind.None)
            .Select(value => new EquipmentDominanceWitness(value.Value.Instance.Fingerprint, value.Value.Definition.ItemId, value.Value.Definition.Name, value.Value.Stats, value.Value.Gearset, value.Coverage))
            .OrderByDescending(value => definitions[value.ItemId].ItemLevel)
            .ThenByDescending(value => value.IsGearsetReferenced)
            .ThenBy(value => value.Fingerprint.Container, StringComparer.Ordinal)
            .ThenBy(value => value.Fingerprint.SlotIndex)
            .ToArray();
        var requiredCount = candidate.Slot == EquipmentSlot.Ring ? 2 : 1;
        var feasibleCount = candidate.Slot == EquipmentSlot.Ring
            ? MaximumFeasibleRingCount(dominating, definitions)
            : dominating.Length;
        var requirement = new EquipmentWitnessRequirement(job, candidate.Slot, requiredCount, dominating);
        if (feasibleCount < requiredCount)
        {
            return new(job, futureUse ? EquipmentUseStatus.FutureUse : EquipmentUseStatus.BaselineNotBetter, null, contributing,
                $"{usable.Count} semantically compatible retained {candidate.Slot} item(s) were evaluated for {job.Abbreviation}, but none provides {requiredCount} feasible retained witness{(requiredCount == 1 ? "" : "es")} that covers {candidate.Name} without relevant-stat loss.", requirement, basis, rejectedGearsets);
        }

        var best = definitions[dominating[0].ItemId];
        var witnessBasis = dominating[0].IsGearsetReferenced
            ? EquipmentComparisonBasis.SavedGearset
            : EquipmentComparisonBasis.SynthesizedOwnedLoadout;
        return new(job, EquipmentUseStatus.Obsolete, best, contributing, null, requirement, witnessBasis, rejectedGearsets);
    }

    private static int MaximumFeasibleRingCount(
        IReadOnlyList<EquipmentDominanceWitness> witnesses,
        IReadOnlyDictionary<uint, EquipmentItemDefinition> definitions)
    {
        if (witnesses.Count == 0) return 0;
        if (witnesses.Count == 1) return 1;
        for (var left = 0; left < witnesses.Count; left++)
            for (var right = left + 1; right < witnesses.Count; right++)
                if (witnesses[left].ItemId != witnesses[right].ItemId || !definitions[witnesses[left].ItemId].IsUnique)
                    return 2;
        return 1;
    }

    private static bool IsSupportedMainHand(EquipmentItemDefinition definition) =>
        definition.MainHandOccupancy == 1 && definition.OffHandOccupancy is 0 or -1;

    // Future-use cleanup compares retained gear at the candidate's equip level; until then,
    // neither the candidate nor its witness is usable. Current gear may use the job's level.
    public static uint WitnessComparisonLevel(EquipmentItemDefinition candidate, CharacterJobSnapshot job) =>
        job.Level < candidate.EquipLevel ? candidate.EquipLevel : job.Level;

    public static bool IsEligibleWitness(
        EquipmentItemDefinition candidate,
        EquipmentItemDefinition witness,
        CharacterJobSnapshot job,
        IReadOnlyList<CharacterJobSnapshot> knownJobs)
    {
        return witness.Slot == candidate.Slot &&
               witness.EquipLevel <= WitnessComparisonLevel(candidate, job) &&
               witness.EligibleClassJobIds.Contains(job.ClassJobId) &&
               !witness.HasUnmodeledEquipRestriction &&
               EquipmentWearerInference.MatchesIntendedWearer(witness, job, knownJobs);
    }

    public static bool CoversWithoutLoss(EquipmentStatProfile baseline, EquipmentStatProfile candidate, IReadOnlySet<EquipmentStatSemantic> relevant)
    {
        var left = Values(baseline);
        var right = Values(candidate);
        foreach (var stat in relevant)
        {
            var baselineValue = left.GetValueOrDefault(stat);
            var candidateValue = right.GetValueOrDefault(stat);
            if (baselineValue < candidateValue) return false;
        }
        return true;
    }

    public static EquipmentCoverageKind EvaluateCoverage(
        EquipmentItemDefinition baselineDefinition,
        EquipmentStatProfile baseline,
        EquipmentItemDefinition candidateDefinition,
        EquipmentStatProfile candidate,
        CharacterJobSnapshot job)
    {
        var relevant = RelevantStats(job);
        if (job.Discipline != EquipmentDiscipline.Combat)
            return CoversWithoutLoss(baseline, candidate, relevant)
                ? EquipmentCoverageKind.ComponentwiseNoLoss
                : EquipmentCoverageKind.None;
        if (candidateDefinition.Slot == EquipmentSlot.OffHand &&
            (baseline.BlockStrength < candidate.BlockStrength || baseline.BlockRate < candidate.BlockRate))
            return EquipmentCoverageKind.None;
        if (CoversWithoutLoss(baseline, candidate, relevant))
            return EquipmentCoverageKind.ComponentwiseNoLoss;

        var baselineValues = Values(baseline);
        var candidateValues = Values(candidate);
        var core = CombatCoreStats(job);
        if (core.Any(stat => baselineValues.GetValueOrDefault(stat) < candidateValues.GetValueOrDefault(stat)))
            return EquipmentCoverageKind.None;
        var secondaries = CombatSecondaryStats(job);
        var baselineBudget = secondaries.Sum(stat => baselineValues.GetValueOrDefault(stat));
        var candidateBudget = secondaries.Sum(stat => candidateValues.GetValueOrDefault(stat));
        if (baselineBudget < candidateBudget)
            return EquipmentCoverageKind.None;

        return EquipmentCoverageKind.CombatCoreAndSecondaryBudget;
    }

    private static IReadOnlySet<EquipmentStatSemantic> CombatCoreStats(CharacterJobSnapshot job)
    {
        var result = new HashSet<EquipmentStatSemantic>
        {
            job.PrimaryStat!.Value,
            EquipmentStatSemantic.Vitality,
            EquipmentStatSemantic.PhysicalDefense,
            EquipmentStatSemantic.MagicalDefense,
            EquipmentStatSemantic.PiercingResistance,
        };
        result.Add(job.PrimaryStat is EquipmentStatSemantic.Intelligence or EquipmentStatSemantic.Mind
            ? EquipmentStatSemantic.MagicalDamage
            : EquipmentStatSemantic.PhysicalDamage);
        result.Add(job.PrimaryStat is EquipmentStatSemantic.Intelligence or EquipmentStatSemantic.Mind
            ? EquipmentStatSemantic.SpellSpeed
            : EquipmentStatSemantic.SkillSpeed);
        if (string.Equals(job.Role, "Tank", StringComparison.OrdinalIgnoreCase) || job.Role == "1")
            result.Add(EquipmentStatSemantic.Tenacity);
        if (job.PrimaryStat == EquipmentStatSemantic.Mind)
            result.Add(EquipmentStatSemantic.Piety);
        return result;
    }

    private static IReadOnlySet<EquipmentStatSemantic> CombatSecondaryStats(CharacterJobSnapshot job)
    {
        var result = new HashSet<EquipmentStatSemantic>
        {
            EquipmentStatSemantic.CriticalHit,
            EquipmentStatSemantic.Determination,
            EquipmentStatSemantic.DirectHit,
        };
        return result;
    }

    private static Dictionary<EquipmentStatSemantic, int> Values(EquipmentStatProfile profile)
    {
        var values = profile.Parameters.GroupBy(value => value.Semantic).ToDictionary(group => group.Key, group => group.Max(value => value.Value));
        values[EquipmentStatSemantic.PhysicalDamage] = profile.PhysicalDamage;
        values[EquipmentStatSemantic.MagicalDamage] = profile.MagicalDamage;
        values[EquipmentStatSemantic.PhysicalDefense] = profile.PhysicalDefense;
        values[EquipmentStatSemantic.MagicalDefense] = profile.MagicalDefense;
        return values;
    }

    public static IReadOnlySet<EquipmentStatSemantic> RelevantStats(CharacterJobSnapshot job)
    {
        var shared = new HashSet<EquipmentStatSemantic>();
        if (job.Discipline == EquipmentDiscipline.Crafter)
            return new HashSet<EquipmentStatSemantic> { EquipmentStatSemantic.Craftsmanship, EquipmentStatSemantic.Control, EquipmentStatSemantic.CraftingPoints };
        if (job.Discipline == EquipmentDiscipline.Gatherer)
            return new HashSet<EquipmentStatSemantic> { EquipmentStatSemantic.Gathering, EquipmentStatSemantic.Perception, EquipmentStatSemantic.GatheringPoints };
        if (job.Discipline != EquipmentDiscipline.Combat || job.PrimaryStat is null or EquipmentStatSemantic.Unknown)
            return shared;
        shared.UnionWith([job.PrimaryStat.Value, EquipmentStatSemantic.Vitality, EquipmentStatSemantic.CriticalHit,
            EquipmentStatSemantic.Determination, EquipmentStatSemantic.DirectHit, EquipmentStatSemantic.PhysicalDefense,
            EquipmentStatSemantic.MagicalDefense]);
        shared.Add(EquipmentStatSemantic.PiercingResistance);
        var magical = job.PrimaryStat is EquipmentStatSemantic.Intelligence or EquipmentStatSemantic.Mind;
        shared.Add(magical ? EquipmentStatSemantic.SpellSpeed : EquipmentStatSemantic.SkillSpeed);
        shared.Add(magical ? EquipmentStatSemantic.MagicalDamage : EquipmentStatSemantic.PhysicalDamage);
        if (string.Equals(job.Role, "Tank", StringComparison.OrdinalIgnoreCase) || job.Role == "1") shared.Add(EquipmentStatSemantic.Tenacity);
        if (job.PrimaryStat == EquipmentStatSemantic.Mind) shared.Add(EquipmentStatSemantic.Piety);
        return shared;
    }

    private static CharacterJobSnapshot Representative(IReadOnlyList<CharacterJobSnapshot> family) =>
        family.Where(value => value.IsUnlocked == true)
            .OrderByDescending(value => value.ParentClassJobId is not null && value.ParentClassJobId != value.ClassJobId)
            .ThenByDescending(value => value.Level).First();

    private static EquipmentUseAnalysis Failure(string code, string diagnostic, IReadOnlyList<EquipmentJobComparison>? comparisons = null) =>
        new(EquipmentUseStatus.EvaluationFailure, comparisons ?? [], code, diagnostic);
}
