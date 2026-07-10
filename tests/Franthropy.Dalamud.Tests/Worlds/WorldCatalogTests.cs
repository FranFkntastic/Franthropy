using Franthropy.Dalamud.Worlds;

namespace Franthropy.Dalamud.Tests.Worlds;

public sealed class WorldCatalogTests
{
    [Fact]
    public void TryGetWorld_MatchesTrimmedNameCaseInsensitively()
    {
        var catalog = new WorldCatalog(
        [
            new WorldInfo("Siren", "Aether", "North America", 57),
        ]);

        var found = catalog.TryGetWorld("  siren  ", out var world);

        Assert.True(found);
        Assert.Equal("Siren", world.Name);
        Assert.Equal("Aether", world.DataCenter);
        Assert.Equal("North America", world.Region);
        Assert.Equal(57u, world.RowId);
    }

    [Fact]
    public void TryGetWorld_ReturnsFalseForBlankOrUnknownWorld()
    {
        var catalog = new WorldCatalog(
        [
            new WorldInfo("Siren", "Aether", "North America", 57),
        ]);

        Assert.False(catalog.TryGetWorld("", out _));
        Assert.False(catalog.TryGetWorld("NotAWorld", out _));
    }

    [Fact]
    public void Constructor_UsesFirstWorldWhenDuplicateNamesAreProvided()
    {
        var catalog = new WorldCatalog(
        [
            new WorldInfo("Siren", "Aether", "North America", 57),
            new WorldInfo("siren", "Other", "Other Region", 999),
        ]);

        var found = catalog.TryGetWorld("Siren", out var world);

        Assert.True(found);
        Assert.Equal("Aether", world.DataCenter);
        Assert.Equal(57u, world.RowId);
    }
}
