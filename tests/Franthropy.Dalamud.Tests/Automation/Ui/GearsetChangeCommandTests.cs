using Franthropy.Dalamud.Automation.Ui;

namespace Franthropy.Dalamud.Tests.Automation.Ui;

public sealed class GearsetChangeCommandTests
{
    [Theory]
    [InlineData("1", "1", "/gearset change 1")]
    [InlineData(" 100 ", "100", "/gearset change 100")]
    public void TryCreateSlotBuildsBoundedNumericCommand(string target, string expectedGearset, string expectedCommand)
    {
        Assert.True(GearsetChangeCommand.TryCreateSlot(target, out var command));
        Assert.NotNull(command);
        Assert.Equal(expectedGearset, command.GearsetName);
        Assert.Equal(expectedCommand, command.Command);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("MIN")]
    public void TryCreateSlotRejectsInvalidTargets(string target) =>
        Assert.False(GearsetChangeCommand.TryCreateSlot(target, out _));

    [Theory]
    [InlineData("Miner", "Miner", "MIN", "/gearset change \"MIN\"")]
    [InlineData("MIN", "Miner", "MIN", "/gearset change \"MIN\"")]
    [InlineData(" Botanist ", "Botanist", "BTN", "/gearset change \"BTN\"")]
    [InlineData("Blacksmith", "Blacksmith", "BSM", "/gearset change \"BSM\"")]
    public void TryCreate_UsesConventionalJobAbbreviationGearsetName(
        string target,
        string expectedJob,
        string expectedGearset,
        string expectedCommand)
    {
        Assert.True(GearsetChangeCommand.TryCreate(target, out var command));

        Assert.Equal(expectedJob, command.JobName);
        Assert.Equal(expectedGearset, command.GearsetName);
        Assert.Equal(expectedCommand, command.Command);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Monk")]
    public void TryCreate_RejectsUnsupportedTargets(string? target)
    {
        Assert.False(GearsetChangeCommand.TryCreate(target, out var command));
        Assert.Null(command);
    }
}
