namespace Franthropy.Filtering;

public sealed record FilterLimits(
    int MaximumExpressionLength = 2048,
    int MaximumTokenCount = 256,
    int MaximumNestingDepth = 32,
    int MaximumListValues = 128,
    int MaximumDiagnostics = 20)
{
    public static FilterLimits Default { get; } = new();

    internal FilterLimits Validate()
    {
        if (MaximumExpressionLength < 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumExpressionLength));
        if (MaximumTokenCount < 2)
            throw new ArgumentOutOfRangeException(nameof(MaximumTokenCount));
        if (MaximumNestingDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumNestingDepth));
        if (MaximumListValues < 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumListValues));
        if (MaximumDiagnostics < 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumDiagnostics));

        return this;
    }
}
