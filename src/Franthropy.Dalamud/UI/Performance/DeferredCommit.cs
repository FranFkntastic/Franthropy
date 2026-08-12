namespace Franthropy.Dalamud.UI.Performance;

/// <summary>
/// Coalesces continuous immediate-mode edits into one durable commit after a quiet interval.
/// The commit still runs on the caller's thread so consumers retain ownership of mutable state.
/// </summary>
public sealed class DeferredCommit
{
    private readonly TimeSpan quietInterval;
    private readonly TimeProvider timeProvider;
    private DateTimeOffset lastChangedAt;

    public DeferredCommit(
        TimeSpan quietInterval,
        string reason,
        TimeProvider? timeProvider = null)
    {
        if (quietInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(quietInterval), "Deferred persistence requires a positive quiet interval.");
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        this.quietInterval = quietInterval;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        Reason = reason;
    }

    public string Reason { get; }
    public bool IsPending { get; private set; }

    public void MarkChanged()
    {
        IsPending = true;
        lastChangedAt = timeProvider.GetUtcNow();
    }

    public bool TryCommit(Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (!IsPending)
            return false;

        var now = timeProvider.GetUtcNow();
        if (now - lastChangedAt < quietInterval)
            return false;

        try
        {
            commit();
            IsPending = false;
            return true;
        }
        catch
        {
            // A failed commit gets another full quiet interval instead of becoming a frame-rate retry loop.
            lastChangedAt = now;
            throw;
        }
    }

    public bool Flush(Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (!IsPending)
            return false;

        commit();
        IsPending = false;
        return true;
    }
}
