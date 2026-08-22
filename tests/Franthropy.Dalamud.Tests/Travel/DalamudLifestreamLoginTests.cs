using ECommons;
using Franthropy.Dalamud.Travel;

namespace Franthropy.Dalamud.Tests.Travel;

public sealed class DalamudLifestreamLoginTests
{
    [Theory]
    [InlineData(ErrorCode.Success, true, "Submitted")]
    [InlineData(ErrorCode.Plugin_is_busy, false, "NotReady")]
    [InlineData(ErrorCode.Player_is_not_logged_in, false, "NotReady")]
    [InlineData(ErrorCode.Invalid_world_specified, false, "Rejected")]
    public void ChangeCharacter_MapsLifestreamResult(ErrorCode code, bool success, string expectedCode)
    {
        var transport = new DalamudLifestreamLogin(
            () => false,
            (_, _) => false,
            () => false,
            (_, _) => false,
            (_, _) => code);

        var result = transport.TryChangeCharacter(new("Wei Ning", "Siren"));

        Assert.Equal(success, result.Success);
        Assert.Equal(expectedCode, result.Code);
        Assert.Equal("CharacterSwitch", result.SubmissionMode);
    }

    [Fact]
    public void ChangeCharacter_IpcFailureIsAmbiguous()
    {
        var transport = new DalamudLifestreamLogin(
            () => false,
            (_, _) => false,
            () => false,
            (_, _) => false,
            (_, _) => throw new InvalidOperationException("disconnected"));

        var result = transport.TryChangeCharacter(new("Wei Ning", "Siren"));

        Assert.False(result.Success);
        Assert.Equal("IpcFailure", result.Code);
        Assert.Equal("CharacterSwitch", result.SubmissionMode);
    }
}
