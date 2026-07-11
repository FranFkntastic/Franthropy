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
        var eligibleFamilies = jobs
            .Where(job => candidate.EligibleClassJobIds.Contains(job.ClassJobId))
            .GroupBy(job => job.ParentClassJobId ?? job.ClassJobId)
            .Select(group => group.ToArray())
            .ToArray();
        if (eligibleFamilies.Any(family => !family.Any(job => job.IsUnlocked == true) && family.Any(job => job.IsUnlocked is null)))
            return new EquipmentUseAnalysis(EquipmentUseStatus.UnknownJobUnlockState, []);

        var unlockedFamilies = eligibleFamilies
            .Where(family => family.Any(job => job.IsUnlocked == true))
            .ToArray();
        if (unlockedFamilies.Length == 0)
            return new EquipmentUseAnalysis(EquipmentUseStatus.NoUnlockedEligibleJob, []);

        var comparisons = unlockedFamilies.Select(family => Compare(candidate, family, gearsets, definitions)).ToArray();
        var overall = comparisons.All(comparison => comparison.Status == EquipmentUseStatus.Obsolete)
            ? EquipmentUseStatus.Obsolete
            : comparisons.First(comparison => comparison.Status != EquipmentUseStatus.Obsolete).Status;
        return new EquipmentUseAnalysis(overall, comparisons);
    }

    private static EquipmentJobComparison Compare(
        EquipmentItemDefinition candidate,
        IReadOnlyList<CharacterJobSnapshot> family,
        IReadOnlyList<GearsetSnapshot> gearsets,
        IReadOnlyDictionary<uint, EquipmentItemDefinition> definitions)
    {
        var unlocked = family.Where(job => job.IsUnlocked == true).ToArray();
        var job = unlocked
            .OrderByDescending(value => value.ParentClassJobId is not null && value.ParentClassJobId != value.ClassJobId)
            .ThenByDescending(value => value.Level)
            .First();
        if (job.Level < candidate.EquipLevel)
            return new EquipmentJobComparison(job, EquipmentUseStatus.FutureUse, null, []);

        var familyIds = family.Select(value => value.ClassJobId).ToHashSet();
        var contributing = gearsets
            .Where(gearset => gearset.IsValid && familyIds.Contains(gearset.ClassJobId))
            .Where(gearset => gearset.Items.Any(item => item.Slot == candidate.Slot))
            .ToArray();

        var baselines = contributing
            .SelectMany(gearset => gearset.Items.Where(item => item.Slot == candidate.Slot))
            .Select(item => definitions.TryGetValue(item.ItemId, out var definition) ? definition : null)
            .Where(definition => definition is not null)
            .Cast<EquipmentItemDefinition>()
            .Where(definition => definition.Slot == candidate.Slot)
            .Where(definition => definition.EligibleClassJobIds.Overlaps(familyIds))
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
