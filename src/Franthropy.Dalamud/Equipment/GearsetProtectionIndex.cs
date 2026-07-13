namespace Franthropy.Dalamud.Equipment;

public sealed class GearsetProtectionIndex
{
    private readonly IReadOnlyDictionary<(uint ItemId, bool? IsHighQuality), IReadOnlyList<GearsetSnapshot>> references;
    private readonly IReadOnlyDictionary<(uint ItemId, bool? IsHighQuality), int> requiredCounts;

    private GearsetProtectionIndex(
        IReadOnlyDictionary<(uint ItemId, bool? IsHighQuality), IReadOnlyList<GearsetSnapshot>> references,
        IReadOnlyDictionary<(uint ItemId, bool? IsHighQuality), int> requiredCounts)
    {
        this.references = references;
        this.requiredCounts = requiredCounts;
    }

    public static GearsetProtectionIndex Create(IEnumerable<GearsetSnapshot> gearsets)
    {
        var references = gearsets
            .Where(gearset => gearset.IsValid)
            .SelectMany(gearset => gearset.Items.Select(item => (item.ItemId, item.IsHighQuality, Gearset: gearset)))
            .Where(entry => entry.ItemId != 0)
            .GroupBy(entry => (entry.ItemId, entry.IsHighQuality))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<GearsetSnapshot>)group.Select(entry => entry.Gearset).Distinct().ToArray());
        var requiredCounts = gearsets.Where(gearset => gearset.IsValid)
            .SelectMany(gearset => gearset.Items.Where(item => item.ItemId != 0)
                .GroupBy(item => (item.ItemId, item.IsHighQuality))
                .Select(group => (group.Key, Count: group.Count())))
            .GroupBy(entry => entry.Key)
            .ToDictionary(group => group.Key, group => group.Max(entry => entry.Count));

        return new GearsetProtectionIndex(references, requiredCounts);
    }

    public bool IsProtected(uint itemId, bool isHighQuality, int availableExactQualityCount) =>
        RequiredCount(itemId, isHighQuality) is var required && required > 0 && availableExactQualityCount <= required;

    public int RequiredCount(uint itemId, bool isHighQuality) => requiredCounts
        .Where(pair => pair.Key.ItemId == itemId && (pair.Key.IsHighQuality is null || pair.Key.IsHighQuality == isHighQuality))
        .Select(pair => pair.Value)
        .DefaultIfEmpty(0)
        .Max();

    public IReadOnlyList<GearsetSnapshot> GetReferences(uint itemId) =>
        references.Where(pair => pair.Key.ItemId == itemId)
            .SelectMany(pair => pair.Value).Distinct().ToArray();

    public IReadOnlyList<GearsetSnapshot> GetReferences(uint itemId, bool isHighQuality) =>
        references.Where(pair => pair.Key.ItemId == itemId && (pair.Key.IsHighQuality is null || pair.Key.IsHighQuality == isHighQuality))
            .SelectMany(pair => pair.Value).Distinct().ToArray();

    public bool RetainsRequiredMultiplicity(IEnumerable<EquipmentInstanceSnapshot> instances) =>
        requiredCounts.All(requirement => instances.Count(instance =>
            instance.Fingerprint.ItemId == requirement.Key.ItemId &&
            (requirement.Key.IsHighQuality is null || instance.Fingerprint.IsHighQuality == requirement.Key.IsHighQuality)) >= requirement.Value);

    public bool DoesNotReduceRequiredMultiplicity(
        IEnumerable<EquipmentInstanceSnapshot> before,
        IEnumerable<EquipmentInstanceSnapshot> after) =>
        requiredCounts.All(requirement =>
        {
            var beforeCount = CountMatching(before, requirement.Key);
            var afterCount = CountMatching(after, requirement.Key);
            return afterCount >= Math.Min(beforeCount, requirement.Value);
        });

    private static int CountMatching(
        IEnumerable<EquipmentInstanceSnapshot> instances,
        (uint ItemId, bool? IsHighQuality) requirement) => instances.Count(instance =>
            instance.Fingerprint.ItemId == requirement.ItemId &&
            (requirement.IsHighQuality is null || instance.Fingerprint.IsHighQuality == requirement.IsHighQuality));
}

