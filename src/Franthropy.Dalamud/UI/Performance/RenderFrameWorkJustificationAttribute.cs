namespace Franthropy.Dalamud.UI.Performance;

/// <summary>
/// Documents a deliberately bounded loop in a render path. Consumer source-policy tests should
/// reject render loops without this marker and a concrete maximum iteration count.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RenderFrameWorkJustificationAttribute : Attribute
{
    public RenderFrameWorkJustificationAttribute(string reason, int maximumIterations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (maximumIterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumIterations));
        Reason = reason;
        MaximumIterations = maximumIterations;
    }

    public string Reason { get; }
    public int MaximumIterations { get; }
}
