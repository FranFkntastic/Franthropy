using Franthropy.Dalamud.Automation.Retainers;

namespace Franthropy.Dalamud.Tests.Automation.Retainers;

public sealed class ElementalCurrencyCatalogTests
{
    [Fact]
    public void Catalog_exposes_independent_shard_crystal_and_cluster_ranges()
    {
        Assert.Equal([2u, 3, 4, 5, 6, 7], ElementalCurrencyCatalog.ShardItemIds);
        Assert.Equal([8u, 9, 10, 11, 12, 13], ElementalCurrencyCatalog.CrystalItemIds);
        Assert.Equal([14u, 15, 16, 17, 18, 19], ElementalCurrencyCatalog.ClusterItemIds);
        Assert.Equal(12, ElementalCurrencyCatalog.ShardAndCrystalItemIds.Count);
        Assert.Equal(9_999, ElementalCurrencyCatalog.PerItemCapacity);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(13, true)]
    [InlineData(14, false)]
    [InlineData(20, false)]
    public void IsShardOrCrystal_excludes_clusters_and_unknown_items(uint itemId, bool expected) =>
        Assert.Equal(expected, ElementalCurrencyCatalog.IsShardOrCrystal(itemId));
}

public sealed class RetainerCrystalTransferObservationTests
{
    [Fact]
    public void Matches_accepts_immediate_transfer_without_requiring_quantity_ui()
    {
        Assert.True(RetainerCrystalTransferObservation.Matches(
            expected: 1,
            playerQuantityBefore: 1,
            playerQuantityAfter: 0,
            retainerQuantityBefore: 25,
            retainerQuantityAfter: 26));
    }

    [Theory]
    [InlineData(2, 10, 8, 20, 21)]
    [InlineData(2, 10, 9, 20, 22)]
    [InlineData(0, 10, 10, 20, 20)]
    public void Matches_requires_symmetric_expected_deltas(
        int expected,
        int playerBefore,
        int playerAfter,
        int retainerBefore,
        int retainerAfter)
    {
        Assert.False(RetainerCrystalTransferObservation.Matches(
            expected,
            playerBefore,
            playerAfter,
            retainerBefore,
            retainerAfter));
    }
}
