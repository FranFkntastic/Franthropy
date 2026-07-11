namespace Franthropy.Dalamud.Equipment;

public sealed class GearsetProtectionIndex
{
    private readonly IReadOnlyDictionary<uint, IReadOnlyList<GearsetSnapshot>> references;

    private GearsetProtectionIndex(IReadOnlyDictionary<uint, IReadOnlyList<GearsetSnapshot>> references)
    {
        this.references = references;
    }

    public static GearsetProtectionIndex Create(IEnumerable<GearsetSnapshot> gearsets)
    {
        var references = gearsets
            .Where(gearset => gearset.IsValid)
            .SelectMany(gearset => gearset.Items.Select(item => (item.ItemId, Gearset: gearset)))
            .Where(entry => entry.ItemId != 0)
            .GroupBy(entry => entry.ItemId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<GearsetSnapshot>)group.Select(entry => entry.Gearset).Distinct().ToArray());

        return new GearsetProtectionIndex(references);
    }

    public bool IsProtected(uint itemId) => references.ContainsKey(itemId);

    public IReadOnlyList<GearsetSnapshot> GetReferences(uint itemId) =>
        references.TryGetValue(itemId, out var gearsets) ? gearsets : [];
}

