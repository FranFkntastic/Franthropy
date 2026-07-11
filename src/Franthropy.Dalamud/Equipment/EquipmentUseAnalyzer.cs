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
    string? Diagnostic = null);

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
    {
        if (candidate.StatProfile is not { IsComplete: true } candidateStats)
        {
            var unknownIds = candidate.StatProfile?.Parameters
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

        var comparisons = obtainedFamilies.Select(family => Compare(candidate, family, gearsets, definitions)).ToArray();
        var failure = comparisons.FirstOrDefault(value => value.Status == EquipmentUseStatus.EvaluationFailure);
        if (failure is not null)
            return Failure("JobComparisonFailed", failure.Diagnostic ?? $"Unable to compare {failure.Job.Abbreviation}.", comparisons);
        var overall = comparisons.All(comparison => comparison.Status == EquipmentUseStatus.Obsolete)
            ? EquipmentUseStatus.Obsolete
            : comparisons.First(comparison => comparison.Status != EquipmentUseStatus.Obsolete).Status;
        return new(overall, comparisons);
    }

    private static EquipmentJobComparison Compare(
        EquipmentItemDefinition candidate,
        IReadOnlyList<CharacterJobSnapshot> family,
        IReadOnlyList<GearsetSnapshot> gearsets,
        IReadOnlyDictionary<uint, EquipmentItemDefinition> definitions)
    {
        var job = Representative(family);
        if (job.Level < candidate.EquipLevel)
            return new(job, EquipmentUseStatus.FutureUse, null, []);
        var relevant = RelevantStats(job);
        if (relevant.Count == 0)
            return new(job, EquipmentUseStatus.EvaluationFailure, null, [], $"No supported stat profile exists for {job.Abbreviation}.");

        var familyIds = family.Select(value => value.ClassJobId).ToHashSet();
        var contributing = gearsets.Where(set => set.IsValid && familyIds.Contains(set.ClassJobId))
            .Where(set => set.Items.Any(item => item.Slot == candidate.Slot)).ToArray();
        var baselines = contributing.SelectMany(set => set.Items.Where(item => item.Slot == candidate.Slot))
            .Select(item => definitions.TryGetValue(item.ItemId, out var value) ? value : null)
            .Where(value => value is not null).Cast<EquipmentItemDefinition>()
            .Where(value => value.Slot == candidate.Slot && value.EligibleClassJobIds.Overlaps(familyIds)).ToArray();
        if (baselines.Length == 0)
            return new(job, EquipmentUseStatus.EvaluationFailure, null, contributing, $"No trusted {candidate.Slot} baseline was found for {job.Abbreviation}.");
        if (baselines.Any(value => value.StatProfile is not { IsComplete: true }))
            return new(job, EquipmentUseStatus.EvaluationFailure, null, contributing, $"A {job.Abbreviation} baseline has an incomplete stat profile.");

        var best = baselines.OrderByDescending(value => value.ItemLevel).First();
        var dominates = Dominates(best.StatProfile!, candidate.StatProfile!, relevant);
        return new(job, dominates ? EquipmentUseStatus.Obsolete : EquipmentUseStatus.BaselineNotBetter, best, contributing,
            dominates ? null : $"{best.Name} does not dominate {candidate.Name} on all stats relevant to {job.Abbreviation}.");
    }

    private static bool Dominates(EquipmentStatProfile baseline, EquipmentStatProfile candidate, IReadOnlySet<EquipmentStatSemantic> relevant)
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

    private static IReadOnlySet<EquipmentStatSemantic> RelevantStats(CharacterJobSnapshot job)
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
