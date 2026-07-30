using Dalamud.Game.ClientState.Objects.Enums;
using Franthropy.Dalamud.Automation.Retainers;

namespace Franthropy.Dalamud.Tests.Automation.Retainers;

public sealed class DalamudSummoningBellInteractorTests
{
    [Theory]
    [InlineData(ObjectKind.EventObj, "Summoning Bell")]
    [InlineData(ObjectKind.HousingEventObject, " summoning bell ")]
    [InlineData(ObjectKind.EventObj, "リテイナーベル")]
    public void IsSummoningBellObject_AcceptsSupportedKindsAndLocalizedNames(ObjectKind kind, string name)
    {
        Assert.True(DalamudSummoningBellInteractor.IsSummoningBellObject(
            kind,
            name,
            ["Summoning Bell", "リテイナーベル"]));
    }

    [Theory]
    [InlineData(ObjectKind.BattleNpc, "Summoning Bell")]
    [InlineData(ObjectKind.EventObj, "Market Board")]
    [InlineData(ObjectKind.EventObj, "")]
    public void IsSummoningBellObject_RejectsWrongKindOrName(ObjectKind kind, string name)
    {
        Assert.False(DalamudSummoningBellInteractor.IsSummoningBellObject(
            kind,
            name,
            ["Summoning Bell", "リテイナーベル"]));
    }

    [Fact]
    public void GetInteractionDistance_AllowsHousingBellReach()
    {
        Assert.Equal(6.5f, DalamudSummoningBellInteractor.GetInteractionDistance(ObjectKind.HousingEventObject));
        Assert.Equal(4.75f, DalamudSummoningBellInteractor.GetInteractionDistance(ObjectKind.EventObj));
    }

    [Theory]
    [InlineData(4.74f, ObjectKind.EventObj, false)]
    [InlineData(4.75f, ObjectKind.EventObj, true)]
    [InlineData(6.49f, ObjectKind.HousingEventObject, false)]
    [InlineData(6.5f, ObjectKind.HousingEventObject, true)]
    public void IsOutsideInteractionRange_MatchesOrdinaryInteractionBoundary(
        float distance,
        ObjectKind kind,
        bool expected)
    {
        Assert.Equal(expected, DalamudSummoningBellInteractor.IsOutsideInteractionRange(distance, kind));
    }

    [Theory]
    [InlineData(0.5f, 25f, 26f)]
    [InlineData(30f, 25f, 30f)]
    public void GetTemporaryHitboxRadius_CoversDistanceWithoutShrinking(
        float originalRadius,
        float distance,
        float expected)
    {
        Assert.Equal(expected, DalamudSummoningBellInteractor.GetTemporaryHitboxRadius(originalRadius, distance));
    }
}
