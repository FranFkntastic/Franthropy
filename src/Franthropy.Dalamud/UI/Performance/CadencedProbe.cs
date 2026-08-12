namespace Franthropy.Dalamud.UI.Performance;

public readonly record struct CadencedProbeResult<T>(T Value, bool Refreshed);

/// <summary>
/// Retains the last truthful result of work that may be observed every frame but must not execute
/// every frame. The required reason makes exceptional render-time polling visible in review.
/// </summary>
public sealed class CadencedProbe<T>
{
    private readonly TimeSpan minimumInterval;
    private readonly TimeProvider timeProvider;
    private DateTimeOffset nextRefreshAt;
    private T value = default!;
    private bool hasValue;

    public CadencedProbe(
        TimeSpan minimumInterval,
        string reason,
        TimeProvider? timeProvider = null)
    {
        if (minimumInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumInterval), "A frame-visible probe requires a positive minimum interval.");
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        this.minimumInterval = minimumInterval;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        Reason = reason;
    }

    public string Reason { get; }
    public bool HasValue => hasValue;
    public T Value => hasValue
        ? value
        : throw new InvalidOperationException("The probe has not produced a value yet.");

    public CadencedProbeResult<T> Read(
        Func<T> probe,
        Func<Exception, T> recover)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(recover);
        var now = timeProvider.GetUtcNow();
        if (hasValue && now < nextRefreshAt)
            return new(value, false);

        try
        {
            value = probe();
        }
        catch (Exception exception)
        {
            value = recover(exception);
        }

        hasValue = true;
        nextRefreshAt = now + minimumInterval;
        return new(value, true);
    }

    public void Invalidate() => nextRefreshAt = DateTimeOffset.MinValue;
}
