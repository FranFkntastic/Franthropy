using Franthropy.Dalamud.Automation.Inventory;

namespace Franthropy.Dalamud.Tests.Automation.Inventory;

public sealed class DalamudDesynthesisUiTransactionTests
{
    [Theory]
    [InlineData("Desynthesis")]
    [InlineData("Desynthesize")]
    public void FindDesynthesisEntry_AcceptsObservedAndLegacyLabels(string label)
    {
        Assert.Equal(1, DalamudDesynthesisUiTransaction.FindDesynthesisEntry(["Try On", label, "Discard"]));
    }

    [Fact]
    public void FindDesynthesisEntry_DoesNotConfuseDiscard()
    {
        Assert.Equal(-1, DalamudDesynthesisUiTransaction.FindDesynthesisEntry(["Try On", "Discard"]));
    }
}
