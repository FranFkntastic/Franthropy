namespace Franthropy.Filtering.Syntax;

public enum FilterTokenKind
{
    EndOfFile,
    Bad,
    Word,
    QuotedString,
    LeftParenthesis,
    RightParenthesis,
    Colon,
    Equals,
    BangEquals,
    ExactEquals,
    ExactNotEquals,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    DotDot,
    Pipe,
    AmpersandAmpersand,
    Bang,
    Minus,
    AndKeyword,
    OrKeyword,
    NotKeyword,
}

public sealed record FilterToken(
    FilterTokenKind Kind,
    string Text,
    string Value,
    TextSpan Span,
    bool HasLeadingWhitespace = false)
{
    public bool IsComparator => Kind is
        FilterTokenKind.Colon or
        FilterTokenKind.Equals or
        FilterTokenKind.BangEquals or
        FilterTokenKind.ExactEquals or
        FilterTokenKind.ExactNotEquals or
        FilterTokenKind.Less or
        FilterTokenKind.LessOrEqual or
        FilterTokenKind.Greater or
        FilterTokenKind.GreaterOrEqual;

    public bool IsComparisonOperator => Kind is
        FilterTokenKind.Equals or
        FilterTokenKind.BangEquals or
        FilterTokenKind.ExactEquals or
        FilterTokenKind.ExactNotEquals or
        FilterTokenKind.Less or
        FilterTokenKind.LessOrEqual or
        FilterTokenKind.Greater or
        FilterTokenKind.GreaterOrEqual;
}
