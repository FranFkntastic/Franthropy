using Franthropy.Dalamud.Observations;

namespace Franthropy.Dalamud.Tests.Observations;

public sealed class RetainerVisitProofTrackerTests
{
    // Admission: an innocent debounce simplification can borrow an identical-layout
    // retainer's prices; neither item checks nor a normal visit reproduces it. This
    // ordered trace exercises the native-boundary policy without a live fast switch.
    [Fact]
    public void FastSwitchRequiresSecondApplyAndEmptyVisitsRequireIndependentEvidence()
    {
        var tracker = new RetainerVisitProofTracker();
        HashSet<int> occupied = [0, 1];
        tracker.Open(1, 0);
        Assert.True(tracker.Apply(tracker.Generation, 1, 500, occupied));
        Assert.Null(tracker.Evaluate(799, 1, occupied, false));
        Assert.Equal("RetainerVisitProven", tracker.Evaluate(800, 1, occupied, false));
        Assert.True(tracker.CanCapture(1));

        Assert.True(tracker.Apply(tracker.Generation, 1, 850, occupied));
        tracker.Open(2, 900); // previous apply <300 ms before selection
        Assert.False(tracker.CanCapture(2));
        Assert.True(tracker.Apply(tracker.Generation, 2, 950, occupied));
        Assert.Null(tracker.Evaluate(1250, 2, occupied, false));
        Assert.True(tracker.Apply(tracker.Generation, 2, 1300, occupied));
        Assert.Equal("RetainerVisitSlotCoverageMismatch", tracker.Evaluate(1600, 2, new HashSet<int> { 0 }, false));
        Assert.False(tracker.CanCapture(2));
        Assert.True(tracker.Apply(tracker.Generation, 2, 1700, occupied));
        Assert.Equal("RetainerVisitProven", tracker.Evaluate(2000, 2, occupied, false));
        Assert.True(tracker.Apply(tracker.Generation, 2, 2100, occupied));
        Assert.False(tracker.CanCapture(2));
        Assert.Equal("RetainerVisitProven", tracker.Evaluate(2400, 2, occupied, false));

        tracker.Open(3, 3000);
        Assert.False(tracker.Apply(tracker.Generation - 1, 2, 3100, occupied));
        Assert.Null(tracker.Evaluate(3999, 3, new HashSet<int>(), true));
        Assert.Null(tracker.Evaluate(4000, 3, null, true));
        Assert.Null(tracker.Evaluate(4000, 3, new HashSet<int>(), false));
        Assert.Equal("RetainerVisitProven", tracker.Evaluate(4000, 3, new HashSet<int>(), true));

        tracker.Open(4, 5000);
        Assert.Equal("RetainerVisitProofTimeout", tracker.Evaluate(11000, 4, occupied, false));
        Assert.Null(tracker.Evaluate(12000, 4, occupied, false));
        Assert.False(tracker.CanCapture(4));

        // Exactly 300 ms is not the ambiguous (<300 ms) boundary.
        tracker.Open(5, 13000);
        Assert.True(tracker.Apply(tracker.Generation, 5, 13500, occupied));
        tracker.Open(6, 13800);
        Assert.True(tracker.Apply(tracker.Generation, 6, 13900, occupied));
        Assert.Equal("RetainerVisitProven", tracker.Evaluate(14200, 6, occupied, false));
        // No-batch empty visits still recover after a fast selection.
        Assert.True(tracker.Apply(tracker.Generation, 6, 14250, occupied));
        tracker.Open(7, 14300);
        Assert.Equal("RetainerVisitProven", tracker.Evaluate(15300, 7, new HashSet<int>(), true));
    }
}
