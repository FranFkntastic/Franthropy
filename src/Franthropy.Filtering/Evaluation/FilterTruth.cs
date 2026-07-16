namespace Franthropy.Filtering.Evaluation;

public enum FilterTruth
{
    False,
    True,
    Unknown,
}

public static class FilterTruthOperations
{
    public static FilterTruth Not(FilterTruth value) => value switch
    {
        FilterTruth.True => FilterTruth.False,
        FilterTruth.False => FilterTruth.True,
        _ => FilterTruth.Unknown,
    };

    public static FilterTruth And(FilterTruth left, FilterTruth right)
    {
        if (left == FilterTruth.False || right == FilterTruth.False)
            return FilterTruth.False;
        if (left == FilterTruth.True && right == FilterTruth.True)
            return FilterTruth.True;
        return FilterTruth.Unknown;
    }

    public static FilterTruth Or(FilterTruth left, FilterTruth right)
    {
        if (left == FilterTruth.True || right == FilterTruth.True)
            return FilterTruth.True;
        if (left == FilterTruth.False && right == FilterTruth.False)
            return FilterTruth.False;
        return FilterTruth.Unknown;
    }
}
