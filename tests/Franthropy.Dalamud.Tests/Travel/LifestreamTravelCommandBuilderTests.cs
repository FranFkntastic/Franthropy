using Franthropy.Dalamud.Travel;
using Franthropy.Dalamud.Worlds;

namespace Franthropy.Dalamud.Tests.Travel;

public sealed class LifestreamTravelCommandBuilderTests
{
    [Fact]
    public void TryBuildMarketBoardTravel_UsesNearestMarketBoardCommandForCurrentWorld()
    {
        var builder = CreateBuilder();

        var result = builder.TryBuildMarketBoardTravel("Siren", "siren", out var request);

        Assert.True(result.Success);
        Assert.Equal("/li mb", result.Command);
        Assert.NotNull(request);
        Assert.Equal("Siren", request.TargetWorld);
        Assert.Equal("siren", request.CurrentWorld);
        Assert.Equal("/li mb", request.Command);
        Assert.True(request.IsCurrentWorld);
    }

    [Fact]
    public void TryBuildMarketBoardTravel_UsesWorldMarketBoardCommandForDifferentWorld()
    {
        var builder = CreateBuilder();

        var result = builder.TryBuildMarketBoardTravel("Siren", "Gilgamesh", out var request);

        Assert.True(result.Success);
        Assert.Equal("/li Siren mb", result.Command);
        Assert.NotNull(request);
        Assert.Equal("Siren", request.TargetWorld);
        Assert.Equal("Gilgamesh", request.CurrentWorld);
        Assert.Equal("/li Siren mb", request.Command);
        Assert.False(request.IsCurrentWorld);
    }

    [Theory]
    [InlineData("", "Siren", "Target world is required.")]
    [InlineData("Unknown", "Siren", "Unknown world: Unknown.")]
    [InlineData("Siren", "", "Current world is unavailable.")]
    public void TryBuildMarketBoardTravel_RejectsInvalidInputs(
        string targetWorld,
        string currentWorld,
        string expectedMessage)
    {
        var builder = CreateBuilder();

        var result = builder.TryBuildMarketBoardTravel(targetWorld, currentWorld, out var request);

        Assert.False(result.Success);
        Assert.Null(result.Command);
        Assert.Null(request);
        Assert.Equal(expectedMessage, result.Message);
    }

    private static LifestreamTravelCommandBuilder CreateBuilder() =>
        new(new WorldCatalog(
        [
            new WorldInfo("Siren", "Aether", "North America", 57),
            new WorldInfo("Gilgamesh", "Aether", "North America", 63),
        ]));
}
