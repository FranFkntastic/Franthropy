using Franthropy.Dalamud.Automation.Inventory;

namespace Franthropy.Dalamud.Tests.Automation.Inventory;

public sealed class DalamudDesynthesisUiTransactionTests
{
    [Theory]
    [InlineData("Desynthesis")]
    [InlineData("Desynthesize")]
    public void FindDesynthesisEntry_AcceptsObservedAndLegacyLabels(string label)
    {
        var result = DalamudContextMenuOptionParser.Find(
            ["Try On", label, "Discard"],
            new DalamudContextMenuOptionSpec("Desynthesis", new HashSet<string> { "Desynthesis", "Desynthesize" }));

        Assert.True(result.Success);
        Assert.Equal(1, result.Index);
    }

    [Fact]
    public void FindDesynthesisEntry_DoesNotConfuseDiscard()
    {
        var result = DalamudContextMenuOptionParser.Find(
            ["Try On", "Discard"],
            new DalamudContextMenuOptionSpec("Desynthesis", new HashSet<string> { "Desynthesis", "Desynthesize" }));

        Assert.False(result.Success);
        Assert.Equal("OptionMissing", result.Code);
    }

}
