using Franthropy.Dalamud.Observations;
using Franthropy.Observations.V1;

namespace Franthropy.Dalamud.Tests.Observations;

public sealed class SharedObservationCapturePlanTests
{
    [Fact]
    public void UnstableOwnerAbstainsWithoutThrowingOrReportingDiagnostic()
    {
        var diagnostics = new List<Exception>();
        var captures = new List<string>();
        var plan = SharedObservationCapturePlan.Create(true, false, false, false, null, null);

        var exception = Record.Exception(() => Execute(plan, captures, diagnostics));

        Assert.Null(exception);
        Assert.Empty(captures);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void MissingRetainerPreservesIndependentlyOwnedInventoryWithoutDiagnostic()
    {
        var diagnostics = new List<Exception>();
        var captures = new List<string>();
        var owner = new ObservationOwner(100, 74);

        var plan = SharedObservationCapturePlan.Create(
            hasPlayerInventoryChanges: true,
            hasSaddlebagChanges: true,
            hasRetainerInventoryChanges: true,
            hasRetainerListingChanges: false,
            owner,
            retainerId: null);

        Execute(plan, captures, diagnostics);

        Assert.Equal(["player", "saddlebag"], captures);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void RetainerMarketAfterTeardownAbstainsWithoutThrowingOrReportingDiagnostic()
    {
        var diagnostics = new List<Exception>();
        var captures = new List<string>();
        var owner = new ObservationOwner(100, 74);
        var plan = SharedObservationCapturePlan.Create(false, false, false, true, owner, null);

        var exception = Record.Exception(() => Execute(plan, captures, diagnostics));

        Assert.Null(exception);
        Assert.Empty(captures);
        Assert.Empty(diagnostics);
    }

    private static void Execute(
        SharedObservationCapturePlan plan,
        ICollection<string> captures,
        ICollection<Exception> diagnostics) =>
        plan.Execute(
            () => captures.Add("player"),
            () => captures.Add("saddlebag"),
            () => captures.Add("retainer"),
            () => captures.Add("listings"),
            diagnostics.Add);
}
