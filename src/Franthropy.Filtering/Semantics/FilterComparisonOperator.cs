using Franthropy.Filtering.Syntax;

namespace Franthropy.Filtering.Semantics;

public enum FilterComparisonOperator
{
    Match,
    Equals,
    NotEquals,
    ExactEquals,
    ExactNotEquals,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

internal static class FilterComparisonOperatorExtensions
{
    public static bool TryFromToken(FilterToken token, out FilterComparisonOperator value)
    {
        value = token.Kind switch
        {
            FilterTokenKind.Colon => FilterComparisonOperator.Match,
            FilterTokenKind.Equals => FilterComparisonOperator.Equals,
            FilterTokenKind.BangEquals => FilterComparisonOperator.NotEquals,
            FilterTokenKind.ExactEquals => FilterComparisonOperator.ExactEquals,
            FilterTokenKind.ExactNotEquals => FilterComparisonOperator.ExactNotEquals,
            FilterTokenKind.Less => FilterComparisonOperator.Less,
            FilterTokenKind.LessOrEqual => FilterComparisonOperator.LessOrEqual,
            FilterTokenKind.Greater => FilterComparisonOperator.Greater,
            FilterTokenKind.GreaterOrEqual => FilterComparisonOperator.GreaterOrEqual,
            _ => default,
        };
        return token.IsComparator;
    }

    public static string Display(this FilterComparisonOperator value) => value switch
    {
        FilterComparisonOperator.Match => ":",
        FilterComparisonOperator.Equals => "=",
        FilterComparisonOperator.NotEquals => "!=",
        FilterComparisonOperator.ExactEquals => "==",
        FilterComparisonOperator.ExactNotEquals => "!==",
        FilterComparisonOperator.Less => "<",
        FilterComparisonOperator.LessOrEqual => "<=",
        FilterComparisonOperator.Greater => ">",
        FilterComparisonOperator.GreaterOrEqual => ">=",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
