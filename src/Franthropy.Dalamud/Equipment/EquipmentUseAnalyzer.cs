using Franthropy.Dalamud.Characters;

namespace Franthropy.Dalamud.Equipment;

public enum EquipmentUseStatus
{
    Obsolete,
    FutureUse,
    MissingBaseline,
    BaselineNotBetter,
    NoUnlockedEligibleJob,
    UnknownJobUnlockState,
}

public sealed record EquipmentJobComparison(
    CharacterJobSnapshot Job,
    EquipmentUseStatus Status,
    EquipmentItemDefinition? Baseline,
    IReadOnlyList<GearsetSnapshot> ContributingGearsets);

public sealed record EquipmentUseAnalysis(
    EquipmentUseStatus Status,
    IReadOnlyList<EquipmentJobComparison> Comparisons)
{
    public bool IsStrictlyObsolete => Status == EquipmentUseStatus.Obsolete;
}

public sealed class EquipmentUseAnalyzer
{
    public EquipmentUseAnalysis Analyze(
        EquipmentItemDefinition candidate,
        IReadOnlyList<CharacterJobSnapshot> jobs,
        IReadOnlyList<GearsetSnapshot> gearsets,
        IReadOnlyDictionary<uint, EquipmentItemDefinition> definitions)
    {
        var eligibleJobs = jobs.Where(job => candidate.EligibleClassJobIds.Contains(job.ClassJobId)).ToArray();
        if (eligibleJobs.Any(job => job.IsUnlocked is null))
            return new EquipmentUseAnalysis(EquipmentUseStatus.UnknownJobUnlockState, []);

        var unlockedJobs = eligibleJobs.Where(job => job.IsUnlocked == true).ToArray();
        if (unlockedJobs.Length == 0)
            return new EquipmentUseAnalysis(EquipmentUseStatus.NoUnlockedEligibleJob, []);

        var comparisons = unlockedJobs.Select(job => Compare(candidate, job, gearsets, definitions)).ToArray();
        var overall = comparisons.All(comparison => comparison.Status == EquipmentUseStatus.Obsolete)
            ? EquipmentUseStatus.Obsolete
            : comparisons.First(comparison => comparison.Status != EquipmentUseStatus.Obsolete).Status;
        return new EquipmentUseAnalysis(overall, comparisons);
    }

    private static EquipmentJobComparison Compare(
        EquipmentItemDefinition candidate,
        CharacterJobSnapshot job,
        IReadOnlyList<GearsetSnapshot> gearsets,
        IReadOnlyDictionary<uint, EquipmentItemDefinition> definitions)
    {
        if (job.Level < candidate.EquipLevel)
            return new EquipmentJobComparison(job, EquipmentUseStatus.FutureUse, null, []);

        var contributing = gearsets
            .Where(gearset => gearset.IsValid && gearset.ClassJobId == job.ClassJobId)
            .Where(gearset => gearset.Items.Any(item => item.Slot == candidate.Slot))
            .ToArray();

        var baselines = contributing
            .SelectMany(gearset => gearset.Items.Where(item => item.Slot == candidate.Slot))
            .Select(item => definitions.TryGetValue(item.ItemId, out var definition) ? definition : null)
            .Where(definition => definition is not null)
            .Cast<EquipmentItemDefinition>()
            .Where(definition => definition.Slot == candidate.Slot)
            .Where(definition => definition.EligibleClassJobIds.Contains(job.ClassJobId))
            .OrderByDescending(definition => definition.ItemLevel)
            .ToArray();

        if (baselines.Length == 0)
            return new EquipmentJobComparison(job, EquipmentUseStatus.MissingBaseline, null, contributing);

        var best = baselines[0];
        var status = best.ItemLevel > candidate.ItemLevel
            ? EquipmentUseStatus.Obsolete
            : EquipmentUseStatus.BaselineNotBetter;
        return new EquipmentJobComparison(job, status, best, contributing);
    }
}

