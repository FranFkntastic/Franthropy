using Franthropy.Dalamud.Travel;

namespace Franthropy.Dalamud.Tests.Travel;

public sealed class LifestreamLoginRequestTests
{
    [Fact]
    public void TryCreate_normalizes_an_explicit_character_and_home_world()
    {
        var created = LifestreamLoginRequest.TryCreate("  Wei Ning ", " Siren ", out var request, out var error);

        Assert.True(created, error);
        Assert.Equal("Wei Ning", request!.CharacterName);
        Assert.Equal("Siren", request.HomeWorld);
    }

    [Theory]
    [InlineData("", "Siren")]
    [InlineData("Wei Ning", "")]
    [InlineData("Wei\nNing", "Siren")]
    [InlineData("Wei Ning", "Sir\nen")]
    public void TryCreate_rejects_missing_or_control_character_identity(string name, string world)
    {
        Assert.False(LifestreamLoginRequest.TryCreate(name, world, out var request, out var error));
        Assert.Null(request);
        Assert.NotEmpty(error);
    }
}
