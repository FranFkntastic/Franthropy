using Franthropy.Dalamud.Automation;
using Franthropy.Dalamud.Travel;
using Franthropy.Dalamud.Worlds;

namespace Franthropy.Dalamud.Tests.Automation.Trades;

public sealed class TravelSubmissionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public void CharacterSwitchRequiresDurableSubmissionAndDoesNotReplay()
    {
        int calls = 0;
        bool journaled = false;
        var state = new CharacterSwitchGameState { LoggedIn = true, Name = "Original", HomeWorld = "Alpha",
            CurrentDataCenterId = 1, HomeDataCenterId = 1, IsConditionSet = _ => false };
        CharacterSwitchOperation Create(Action save) => new(_ =>
        {
            calls++;
            throw new IOException("Lost reply");
        }, new("Target", "Alpha"), Now.AddMinutes(1), _ => { }, save, journaled);
        Assert.Equal("SubmissionNotPersisted", Create(() => throw new IOException()).Tick(state, Now).Code);
        Assert.Equal(0, calls);
        Assert.Equal(OperationStatus.Indeterminate, Create(() => journaled = true).Tick(state, Now).Status);
        var resumed = Create(() => throw new IOException());
        Assert.Equal(OperationStatus.Pending, resumed.Tick(state, Now).Status);
        Assert.Equal(OperationStatus.Succeeded, resumed.Tick(state with { Name = "Target" }, Now).Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void WorldVisitRequiresDurableSubmissionAndSettledArrivalWithoutReplay()
    {
        int calls = 0;
        bool journaled = false, zoning = false;
        uint world = 1;
        var catalog = new WorldCatalog([new("Alpha", "DC", "Region", 2)]);
        WorldVisitOperation Create(Action save) => new(catalog, "Alpha", () => (99UL, world, true, zoning),
            _ => { calls++; throw new IOException("Lost reply"); }, Now.AddMinutes(1), 99, _ => { }, save, journaled);
        Assert.Equal("SubmissionNotPersisted", Create(() => throw new IOException()).Tick(Now).Code);
        Assert.Equal(0, calls);
        Assert.Equal(OperationStatus.Indeterminate, Create(() => journaled = true).Tick(Now).Status);
        var resumed = Create(() => throw new IOException());
        Assert.Equal(OperationStatus.Pending, resumed.Tick(Now).Status);
        world = 2;
        zoning = true;
        Assert.Equal(OperationStatus.Pending, resumed.Tick(Now).Status);
        zoning = false;
        Assert.Equal(OperationStatus.Succeeded, resumed.Tick(Now).Status);
        Assert.Equal(1, calls);
    }
}
