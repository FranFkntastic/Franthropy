using System.Text.Json;
using Franthropy.Dalamud.Travel;

namespace Franthropy.Dalamud.Tests.Travel;

public sealed class DalamudLifestreamObjectInteractorTests
{
    [Fact]
    public void BuildAliasJson_UsesDirectInteractionWhenApproachDistanceIsAbsent()
    {
        using var document = JsonDocument.Parse(
            DalamudLifestreamObjectInteractor.BuildAliasJson(2000401, exportedName: "Open bell"));

        var root = document.RootElement;
        Assert.Equal("Open bell", root.GetProperty("ExportedName").GetString());
        var command = Assert.Single(root.GetProperty("Commands").EnumerateArray());
        Assert.Equal(6, command.GetProperty("Kind").GetInt32());
        Assert.Equal(2000401u, command.GetProperty("DataID").GetUInt32());
        Assert.False(command.TryGetProperty("InteractDistance", out _));
    }

    [Fact]
    public void BuildAliasJson_PreservesExplicitApproachDistance()
    {
        using var document = JsonDocument.Parse(
            DalamudLifestreamObjectInteractor.BuildAliasJson(2000401, 4.5f));

        var command = Assert.Single(document.RootElement.GetProperty("Commands").EnumerateArray());
        Assert.Equal(4.5f, command.GetProperty("InteractDistance").GetSingle());
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void BuildAliasJson_RejectsInvalidApproachDistance(float distance)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DalamudLifestreamObjectInteractor.BuildAliasJson(2000401, distance));
    }
}
