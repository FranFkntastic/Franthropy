using Franthropy.Dalamud.Diagnostics;

namespace Franthropy.Dalamud.Tests.Diagnostics;

public sealed class GamePatchCompatibilityGateTests
{
    [Fact]
    public void Evaluate_ApprovesOnlyTheExactReviewedBuild()
    {
        var approved = GamePatchCompatibilityGate.Evaluate(
            "retainer-item-command",
            "2026.06.18.0000.0000",
            "2026.06.18.0000.0000");
        var changed = GamePatchCompatibilityGate.Evaluate(
            "retainer-item-command",
            "2026.06.18.0000.0000",
            "2026.07.28.0000.0000");

        Assert.True(approved.IsApproved);
        Assert.False(changed.IsApproved);
        Assert.Equal(GamePatchCompatibility.FailureCode, "UnsupportedGameBuild");
        Assert.Contains("2026.07.28.0000.0000", changed.Message, StringComparison.Ordinal);
        Assert.Contains("2026.06.18.0000.0000", changed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Require_ThrowsTypedFailureForUnknownBuild()
    {
        var failure = Assert.Throws<GamePatchCompatibilityException>(() =>
            GamePatchCompatibilityGate.Require(
                "render-manager-active-flag",
                "2026.06.18.0000.0000",
                "unknown"));

        Assert.Equal("render-manager-active-flag", failure.Compatibility.ContractId);
        Assert.False(failure.Compatibility.IsApproved);
    }
}
