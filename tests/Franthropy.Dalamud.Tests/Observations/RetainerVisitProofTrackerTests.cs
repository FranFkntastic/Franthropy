using Franthropy.Dalamud.Observations;

namespace Franthropy.Dalamud.Tests.Observations;

public sealed class RetainerVisitProofTrackerTests
{
    // Admission: an innocent debounce simplification can borrow an identical-layout
    // retainer's prices; neither item checks nor a normal visit reproduces it. This
    // ordered trace exercises the native-boundary policy without a live fast switch.
    [Fact]
    public void LateRoundsFromAnUnsettledPreviousVisitNeverProveTheNextRetainer()
    {
        var tracker = new RetainerVisitProofTracker();
        HashSet<int> occupied = [0, 1];

        // Ordinary visit: one batch, then 300 ms quiet.
        tracker.Open(1, 0);
        Assert.True(tracker.Apply(tracker.Generation, 1, 500, occupied));
        Assert.Null(tracker.Evaluate(799, 1, occupied, false));
        Assert.Equal("RetainerVisitProven", tracker.Evaluate(800, 1, occupied, false));
        Assert.True(tracker.CanCapture(1));

        // Fast switch before A's rounds arrive: A's late batch lands after B opened while
        // the previous apply is long past. It must not prove B, nor may B's own rounds
        // inside A's round window, since the two are indistinguishable on the wire.
        tracker.Open(1, 10_000);
        tracker.Open(2, 10_200);
        Assert.True(tracker.Apply(tracker.Generation, 2, 10_250, occupied));
        Assert.Null(tracker.Evaluate(10_550, 2, occupied, false));
        Assert.True(tracker.Apply(tracker.Generation, 2, 10_800, occupied));
        Assert.Null(tracker.Evaluate(11_200, 2, occupied, false));
        Assert.False(tracker.CanCapture(2));
        Assert.Equal("RetainerVisitBoundaryUnresolved", tracker.Evaluate(16_200, 2, occupied, false));
        Assert.False(tracker.CanCapture(2));

        // A settled previous visit leaves the next one unambiguous.
        tracker.Open(3, 20_000);
        Assert.Null(tracker.AttributableFrom);
        Assert.True(tracker.Apply(tracker.Generation, 3, 20_400, occupied));
        Assert.Equal("RetainerVisitProven", tracker.Evaluate(20_700, 3, occupied, false));
        Assert.True(tracker.Apply(tracker.Generation, 3, 20_750, occupied));
        Assert.False(tracker.CanCapture(3)); // a later batch revokes proof until quiet again
        Assert.Equal("RetainerVisitProven", tracker.Evaluate(21_050, 3, occupied, false));

        // Empty visit after a fast switch: the previous retainer's late batch is not
        // evidence, and emptiness proves only once that retainer's window has closed.
        tracker.Open(5, 30_000);
        tracker.Open(6, 30_100);
        Assert.True(tracker.Apply(tracker.Generation, 6, 30_400, occupied));
        Assert.Null(tracker.Evaluate(31_100, 6, new HashSet<int>(), true));
        Assert.Null(tracker.Evaluate(31_600, 6, null, true));
        Assert.Null(tracker.Evaluate(31_600, 6, new HashSet<int>(), false));
        Assert.Equal("RetainerVisitProven", tracker.Evaluate(31_600, 6, new HashSet<int>(), true));

        // Coverage must equal the occupied slots; refusal persists until a new batch.
        tracker.Open(7, 40_000);
        Assert.True(tracker.Apply(tracker.Generation, 7, 40_400, occupied));
        Assert.Equal("RetainerVisitSlotCoverageMismatch", tracker.Evaluate(40_700, 7, new HashSet<int> { 0 }, false));
        Assert.False(tracker.CanCapture(7));
        Assert.Equal("RetainerVisitProofTimeout", tracker.Evaluate(46_000, 7, new HashSet<int> { 0 }, false));
        Assert.Null(tracker.Evaluate(47_000, 7, new HashSet<int> { 0 }, false));

        // A batch reported for a superseded generation is rejected as a late arrival.
        tracker.Open(8, 50_000);
        Assert.False(tracker.Apply(tracker.Generation - 1, 7, 50_100, occupied));
    }
}
