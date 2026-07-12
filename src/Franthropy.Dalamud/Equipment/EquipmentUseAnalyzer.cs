using Franthropy.Dalamud.Characters;

namespace Franthropy.Dalamud.Equipment;

public enum EquipmentUseStatus
{
    Obsolete,
    FutureUse,
    BaselineNotBetter,
    NoObtainedEligibleJob,
    LikelyCosmetic,
    EvaluationFailure,
}

public sealed record EquipmentJobComparison(
    CharacterJobSnapshot Job,
    EquipmentUseStatus Status,
    EquipmentItemDefinition? Baseline,
    IReadOnlyList<GearsetSnapshot> ContributingGearsets,
    string? Diagnostic = null,
    EquipmentWitnessRequirement? WitnessRequirement = null);

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
    public EquipmentUseAnalysis Analyze(
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
        var candidateStats = candidateInstance is null ? candidate.StatProfile : EquipmentInstanceStats.Resolve(candidateInstance, candidate);
        if (candidateStats is not { IsComplete: true })
        {
            var unknownIds = candidateStats?.Parameters
                .Where(value => value.Semantic == EquipmentStatSemantic.Unknown)
                .Select(value => $"{value.BaseParamId} ({value.SourceName ?? "unnamed"})").Distinct().Order().ToArray() ?? [];
            return Failure("IncompleteStatProfile", $"{candidate.Name} has no complete functional stat profile. Unmapped BaseParams: {(unknownIds.Length == 0 ? "none recorded" : string.Join(", ", unknownIds))}.");
        }
        if (IsAllClasses(candidate, jobs) && !candidateStats.HasFunctionalStats)
            return new(EquipmentUseStatus.LikelyCosmetic, [], Diagnostic: "All Classes equipment has no functional stats.");

        var eligibleFamilies = jobs
            .Where(job => candidate.EligibleClassJobIds.Contains(job.ClassJobId))
            .GroupBy(job => job.ParentClassJobId ?? job.ClassJobId)
            .Select(group => group.ToArray())
            .ToArray();
        if (eligibleFamilies.Any(family => !family.Any(job => job.IsUnlocked == true) && family.Any(job => job.IsUnlocked is null)))
            return Failure("JobUnlockStateUnavailable", "An eligible job family has no conclusive obtained-state observation.");

        var obtainedFamilies = eligibleFamilies.Where(family => family.Any(job => job.IsUnlocked == true)).ToArray();
        if (IsAllClasses(candidate, jobs))
        {
            var supplied = Values(candidateStats).Where(pair => pair.Value > 0).Select(pair => pair.Key).ToHashSet();
            obtainedFamilies = obtainedFamilies.Where(family =>
            {
                var representative = Representative(family);
                return RelevantStats(representative).Overlaps(supplied);
            }).ToArray();
        }
        if (obtainedFamilies.Length == 0)
        {
            return new(EquipmentUseStatus.NoObtainedEligibleJob, []);
        }

        var comparisons = obtainedFamilies.Select(family => Compare(candidateInstance, candidate, candidateStats, family, gearsets, ownedInstances, definitions)).ToArray();
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
        IReadOnlyList<GearsetSnapshot> gearsets,
        IReadOnlyList<EquipmentInstanceSnapshot> ownedInstances,
        IReadOnlyDictionary<uint, EquipmentItemDefinition> definitions)
    {
        var job = Representative(family);
        if (job.Level < candidate.EquipLevel)
            return new(job, EquipmentUseStatus.FutureUse, null, []);
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
        var savedBaselines = contributing.SelectMany(set => set.Items.Where(item => item.Slot == candidate.Slot))
            .Select(item => definitions.TryGetValue(item.ItemId, out var value) ? value : null)
            .Where(value => value is not null).Cast<EquipmentItemDefinition>()
            .Where(value => value.Slot == candidate.Slot && value.EligibleClassJobIds.Overlaps(familyIds)).ToArray();
        if (candidateInstance is null)
        {
            if (savedBaselines.Length == 0)
                return new(job, EquipmentUseStatus.EvaluationFailure, null, contributing, $"No trusted {candidate.Slot} baseline was found for {job.Abbreviation}.");
            if (savedBaselines.Any(value => value.StatProfile is not { IsComplete: true }))
                return new(job, EquipmentUseStatus.EvaluationFailure, null, contributing, $"A {job.Abbreviation} baseline has an incomplete stat profile.");
            var legacyBest = savedBaselines.OrderByDescending(value => value.ItemLevel).First();
            var legacyDominates = Dominates(legacyBest.StatProfile!, candidateStats, relevant);
            return new(job, legacyDominates ? EquipmentUseStatus.Obsolete : EquipmentUseStatus.BaselineNotBetter, legacyBest, contributing,
                legacyDominates ? null : $"{legacyBest.Name} does not dominate {candidate.Name} on all stats relevant to {job.Abbreviation}.");
        }

        var gearsetItemIds = contributing.SelectMany(set => set.Items.Where(item => item.Slot == candidate.Slot)).Select(item => item.ItemId).ToHashSet();
        var usable = new List<(EquipmentInstanceSnapshot Instance, EquipmentItemDefinition Definition, EquipmentStatProfile Stats, bool Gearset)>();
        var incompleteWitnessNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instance in ownedInstances)
        {
            if (instance.Fingerprint == candidateInstance.Fingerprint || instance.Fingerprint.MateriaIds.Count > 0)
                continue;
            if (!definitions.TryGetValue(instance.Fingerprint.ItemId, out var definition) || definition.Slot != candidate.Slot ||
                definition.EquipLevel > job.Level || !definition.EligibleClassJobIds.Overlaps(familyIds))
                continue;
            if (candidate.Slot == EquipmentSlot.MainHand &&
                (definition.MainHandOccupancy != candidate.MainHandOccupancy || definition.OffHandOccupancy != candidate.OffHandOccupancy))
                continue;
            if (candidate.Slot == EquipmentSlot.OffHand && definition.OffHandOccupancy != 1)
                continue;
            if (candidate.Slot == EquipmentSlot.Ring && (!definition.FitsLeftRing || !definition.FitsRightRing))
                continue;
            var stats = EquipmentInstanceStats.Resolve(instance, definition);
            if (stats is not { IsComplete: true })
            {
                incompleteWitnessNames.Add(definition.Name);
                continue;
            }
            usable.Add((instance, definition, stats, gearsetItemIds.Contains(definition.ItemId)));
        }
        if (usable.Count == 0)
        {
            var excluded = incompleteWitnessNames.Count == 0
                ? string.Empty
                : $" Incomplete prospective witnesses excluded: {string.Join(", ", incompleteWitnessNames.Order())}.";
            return new(job, EquipmentUseStatus.EvaluationFailure, null, contributing,
                $"No saved or owned usable {candidate.Slot} witness was found for {job.Abbreviation}.{excluded}");
        }

        var dominating = usable.Where(value => Dominates(value.Stats, candidateStats, relevant))
            .Select(value => new EquipmentDominanceWitness(value.Instance.Fingerprint, value.Definition.ItemId, value.Definition.Name, value.Stats, value.Gearset))
            .OrderByDescending(value => value.IsGearsetReferenced)
            .ThenByDescending(value => definitions[value.ItemId].ItemLevel)
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
            var bestAvailable = usable.OrderByDescending(value => value.Gearset).ThenByDescending(value => value.Definition.ItemLevel).First();
            return new(job, EquipmentUseStatus.BaselineNotBetter, bestAvailable.Definition, contributing,
                $"Owned equipment does not provide {requiredCount} retained feasible witness{(requiredCount == 1 ? "" : "es")} that dominate {candidate.Name} for {job.Abbreviation}.", requirement);
        }

        var best = definitions[dominating[0].ItemId];
        return new(job, EquipmentUseStatus.Obsolete, best, contributing, null, requirement);
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

    public static bool Dominates(EquipmentStatProfile baseline, EquipmentStatProfile candidate, IReadOnlySet<EquipmentStatSemantic> relevant)
    {
        var left = Values(baseline);
        var right = Values(candidate);
        var strictlyBetter = false;
        foreach (var stat in relevant)
        {
            var baselineValue = left.GetValueOrDefault(stat);
            var candidateValue = right.GetValueOrDefault(stat);
            if (baselineValue < candidateValue) return false;
            strictlyBetter |= baselineValue > candidateValue;
        }
        return strictlyBetter;
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

    private static bool IsAllClasses(EquipmentItemDefinition candidate, IReadOnlyList<CharacterJobSnapshot> jobs) =>
        jobs.Any(job => !string.Equals(job.Abbreviation, "ADV", StringComparison.OrdinalIgnoreCase)) &&
        jobs.Where(job => !string.Equals(job.Abbreviation, "ADV", StringComparison.OrdinalIgnoreCase))
            .All(job => candidate.EligibleClassJobIds.Contains(job.ClassJobId));

    private static EquipmentUseAnalysis Failure(string code, string diagnostic, IReadOnlyList<EquipmentJobComparison>? comparisons = null) =>
        new(EquipmentUseStatus.EvaluationFailure, comparisons ?? [], code, diagnostic);
}
