namespace Franthropy.Dalamud.Observations;

/// <summary>Pure visit ordering/settlement policy. All times are monotonic milliseconds.</summary>
internal sealed class RetainerVisitProofTracker
{
    internal const double QuietMilliseconds = 300;
    // Live rounds arrived by 660 ms and spanned <=200 ms. Allow a full second
    // (660 + 300, rounded up), AND require an authoritative empty roster count.
    // An unreadable container is never an empty container.
    internal const double EmptyVisitMilliseconds = 1000;
    private double? lastApply;
    private bool boundaryAmbiguous;
    private bool refused;
    private bool timeoutReported;
    private readonly List<double> applies = [];
    internal long Generation { get; private set; }
    internal ulong RetainerId { get; private set; }
    internal double OpenedAt { get; private set; }
    internal bool Proven { get; private set; }
    internal bool FirstBatchIsAmbiguous => boundaryAmbiguous;
    internal HashSet<int> CoveredSlots { get; private set; } = [];
    internal IReadOnlyList<double> Applies => applies;

    internal void Open(ulong retainerId, double now)
    {
        boundaryAmbiguous = lastApply is { } previous && now - previous < QuietMilliseconds;
        Generation++;
        RetainerId = retainerId;
        OpenedAt = now;
        applies.Clear();
        CoveredSlots = [];
        Proven = refused = timeoutReported = false;
    }

    internal bool Apply(long generation, ulong activeRetainer, double now, IEnumerable<int> covered)
    {
        lastApply = now;
        if (generation != Generation || activeRetainer != RetainerId || RetainerId == 0 || now < OpenedAt)
            return false;
        applies.Add(now);
        CoveredSlots = covered.ToHashSet();
        Proven = refused = false;
        return true;
    }

    internal string? Evaluate(double now, ulong activeRetainer, IReadOnlySet<int>? occupied, bool rosterEmpty)
    {
        if (RetainerId == 0 || Proven || activeRetainer != RetainerId)
            return null;
        if (!refused && occupied is not null)
        {
            var ready = applies.Count == 0
                ? rosterEmpty && occupied.Count == 0 && now - OpenedAt >= EmptyVisitMilliseconds
                : applies.Count >= (boundaryAmbiguous ? 2 : 1) && now - applies[^1] >= QuietMilliseconds;
            if (ready)
            {
                if (!CoveredSlots.SetEquals(occupied))
                {
                    refused = true;
                    return "RetainerVisitSlotCoverageMismatch";
                }
                Proven = true;
                return "RetainerVisitProven";
            }
        }
        if (!timeoutReported && now - OpenedAt >= 6000)
        {
            timeoutReported = true;
            return "RetainerVisitProofTimeout";
        }
        return null;
    }

    internal bool CanCapture(ulong retainerId) => Proven && RetainerId != 0 && RetainerId == retainerId;
}
