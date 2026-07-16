using System.Numerics;
using Franthropy.Dalamud.UI.Windows;

namespace Franthropy.Dalamud.Tests.UI.Windows;

public sealed class CompanionWindowPlacementTests
{
    [Fact]
    public void Calculate_PrefersRightSideWhenItFits()
    {
        var result = CompanionWindowPlacement.Calculate(
            new Vector2(100, 100), new Vector2(300, 400), new Vector2(200, 250),
            Vector2.Zero, new Vector2(1000, 800));

        Assert.Equal(CompanionWindowPlacementKind.Right, result.Kind);
        Assert.Equal(new Vector2(408, 100), result.Position);
    }

    [Fact]
    public void Calculate_UsesLeftSideWhenRightDoesNotFit()
    {
        var result = CompanionWindowPlacement.Calculate(
            new Vector2(500, 100), new Vector2(450, 400), new Vector2(300, 250),
            Vector2.Zero, new Vector2(1000, 800));

        Assert.Equal(CompanionWindowPlacementKind.Left, result.Kind);
        Assert.Equal(new Vector2(192, 100), result.Position);
    }

    [Fact]
    public void Calculate_OverlaysAndClampsWhenNeitherSideFits()
    {
        var result = CompanionWindowPlacement.Calculate(
            new Vector2(100, 700), new Vector2(800, 200), new Vector2(680, 420),
            Vector2.Zero, new Vector2(1000, 800));

        Assert.Equal(CompanionWindowPlacementKind.Overlay, result.Kind);
        Assert.Equal(new Vector2(212, 380), result.Position);
    }
}
