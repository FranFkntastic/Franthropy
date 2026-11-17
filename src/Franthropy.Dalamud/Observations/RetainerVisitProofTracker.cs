namespace Franthropy.Dalamud.Observations;

/// <summary>Pure visit ordering/settlement policy. All times are monotonic milliseconds.</summary>
/// <remarks>
/// Price batches carry no retainer identity, so ownership is proven by timing alone.
/// Live capture: a selection's batch rounds arrived 360-660 ms after it, spanning at most
/// 200 ms, and the last round matched the occupied slots. A selection made before the
/// previous one's rounds finished can therefore receive that retainer's late rounds.
/// </remarks>
internal sealed class RetainerVisitProofTracker
{
    internal const double QuietMilliseconds = 300;
    // Live rounds arrived by 660 ms and spanned <=200 ms. Allow a full second
    // (660 + 300, rounded up), AND require an authoritative empty roster count.
    // An unreadable container is never an empty container.
    internal const double EmptyVisitMilliseconds = 1000;
    // A batch landing earlier than this after the previous (unsettled) selection or
    // batch may still belong to the previous retainer. 660 ms observed, >2x margin:
    // a refused visit costs one capture, a misattributed one publishes wrong prices.
    internal const double PreviousRoundWindowMilliseconds = 1500;
    private double? lastApply;
    private bool refused;
    private bool timeoutReported;
    private readonly List<double> applies = [];
    internal long Generation { get; private set; }
    internal ulong RetainerId { get; private set; }
    internal double OpenedAt { get; private set; }
    internal bool Proven { get; private set; }
    /// <summary>Applies before this time may be the previous retainer's late rounds and never prove this visit.</summary>
    internal double? AttributableFrom { get; private set; }
    internal HashSet<int> CoveredSlots { get; private set; } = [];
    internal IReadOnlyList<double> Applies => applies;

    internal void Open(ulong retainerId, double now)
    {
        // The previous visit settled only if its last batch was followed by a quiet period
        // before this selection. Anything else (no batch yet, or a batch still in flight)
        // leaves its rounds possibly outstanding.
        var previousSettled = Generation == 0 ||
            (applies.Count > 0 && now - applies[^1] >= QuietMilliseconds) ||
            (Proven && (lastApply is null || now - lastApply.Value >= QuietMilliseconds));
        var recentApply = lastApply is { } previousApply && now - previousApply < QuietMilliseconds;
        var recentOpen = Generation > 0 && now - OpenedAt < PreviousRoundWindowMilliseconds;
        AttributableFrom = !previousSettled && (recentOpen || recentApply)
            ? Math.Max(OpenedAt, lastApply ?? OpenedAt) + PreviousRoundWindowMilliseconds
            : null;
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

    internal bool IsAttributable(double applyTime) => AttributableFrom is not { } from || applyTime >= from;

    internal string? Evaluate(double now, ulong activeRetainer, IReadOnlySet<int>? occupied, bool rosterEmpty)
    {
        if (RetainerId == 0 || Proven || activeRetainer != RetainerId)
            return null;
        if (!refused && occupied is not null)
        {
            var batchProof = applies.Count > 0 && IsAttributable(applies[^1]);
            // No attributable batch: the table may hold the previous retainer's prices, so
            // only a visit independently proven empty (no prices to read) can settle.
            var emptyProof = !applies.Any(IsAttributable) && rosterEmpty && occupied.Count == 0 &&
                now - OpenedAt >= EmptyVisitMilliseconds && IsAttributable(now);
            var quiet = applies.Count == 0 || now - applies[^1] >= QuietMilliseconds;
            if ((batchProof || emptyProof) && quiet)
            {
                if (batchProof && !CoveredSlots.SetEquals(occupied))
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
            return AttributableFrom is not null && !applies.Any(IsAttributable)
                ? "RetainerVisitBoundaryUnresolved"
                : "RetainerVisitProofTimeout";
        }
        return null;
    }

    internal bool CanCapture(ulong retainerId) => Proven && RetainerId != 0 && RetainerId == retainerId;
}
