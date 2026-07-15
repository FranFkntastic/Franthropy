namespace Franthropy.Dalamud.Automation.Retainers;

/// <summary>
/// Canonical item identifiers and per-item storage rules for FFXIV elemental currencies.
/// Each elemental type has an independent fixed slot on characters and retainers.
/// </summary>
public static class ElementalCurrencyCatalog
{
    public const int PerItemCapacity = 9_999;

    public static readonly IReadOnlyList<uint> ShardItemIds = BuildRange(2, 6);
    public static readonly IReadOnlyList<uint> CrystalItemIds = BuildRange(8, 6);
    public static readonly IReadOnlyList<uint> ClusterItemIds = BuildRange(14, 6);
    public static readonly IReadOnlyList<uint> ShardAndCrystalItemIds = [.. ShardItemIds, .. CrystalItemIds];
    public static readonly IReadOnlyList<uint> AllItemIds = [.. ShardAndCrystalItemIds, .. ClusterItemIds];

    public static bool IsShard(uint itemId) => itemId is >= 2 and <= 7;
    public static bool IsCrystal(uint itemId) => itemId is >= 8 and <= 13;
    public static bool IsCluster(uint itemId) => itemId is >= 14 and <= 19;
    public static bool IsShardOrCrystal(uint itemId) => IsShard(itemId) || IsCrystal(itemId);
    public static bool IsElementalCurrency(uint itemId) => IsShardOrCrystal(itemId) || IsCluster(itemId);

    private static IReadOnlyList<uint> BuildRange(int start, int count) =>
        Enumerable.Range(start, count).Select(value => (uint)value).ToArray();
}
