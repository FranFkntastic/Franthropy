namespace Franthropy.Filtering.Syntax;

public abstract record FilterSyntaxNode
{
    public abstract TextSpan Span { get; }
}

public sealed record FilterQuerySyntax(FilterExpressionSyntax Expression, FilterToken EndOfFile) : FilterSyntaxNode
{
    public override TextSpan Span => Expression.Span;
}

public abstract record FilterExpressionSyntax : FilterSyntaxNode;

public sealed record FilterMissingExpressionSyntax(TextSpan MissingSpan) : FilterExpressionSyntax
{
    public override TextSpan Span => MissingSpan;
}

public sealed record FilterFreeTextSyntax(FilterToken Text) : FilterExpressionSyntax
{
    public override TextSpan Span => Text.Span;
}

public sealed record FilterFieldExpressionSyntax(
    FilterToken Field,
    FilterToken Comparator,
    FilterValueSyntax Value) : FilterExpressionSyntax
{
    public FilterToken? Separator { get; init; }
    public override TextSpan Span => Field.Span.Union(Value.Span);
}

public sealed record FilterReservedNestedQualifierSyntax(
    IReadOnlyList<FilterToken> Segments,
    IReadOnlyList<FilterToken> Separators,
    FilterToken? Comparator,
    FilterValueSyntax Value) : FilterExpressionSyntax
{
    public override TextSpan Span => Segments[0].Span.Union(Value.Span);
}

public sealed record FilterFunctionCallSyntax(
    FilterToken Function,
    FilterToken OpenParenthesis,
    FilterToken Field,
    FilterToken CloseParenthesis) : FilterExpressionSyntax
{
    public override TextSpan Span => Function.Span.Union(CloseParenthesis.Span);
}

public sealed record FilterUnaryExpressionSyntax(
    FilterToken Operator,
    FilterExpressionSyntax Operand) : FilterExpressionSyntax
{
    public override TextSpan Span => Operator.Span.Union(Operand.Span);
}

public enum FilterBinaryOperator
{
    And,
    Or,
}

public sealed record FilterBinaryExpressionSyntax(
    FilterExpressionSyntax Left,
    FilterBinaryOperator Operator,
    FilterToken? OperatorToken,
    FilterExpressionSyntax Right,
    bool IsImplicit) : FilterExpressionSyntax
{
    public override TextSpan Span => Left.Span.Union(Right.Span);
}

public sealed record FilterParenthesizedExpressionSyntax(
    FilterToken OpenParenthesis,
    FilterExpressionSyntax Expression,
    FilterToken CloseParenthesis) : FilterExpressionSyntax
{
    public override TextSpan Span => OpenParenthesis.Span.Union(CloseParenthesis.Span);
}

public abstract record FilterValueSyntax : FilterSyntaxNode;

public sealed record FilterMissingValueSyntax(TextSpan MissingSpan) : FilterValueSyntax
{
    public override TextSpan Span => MissingSpan;
}

public sealed record FilterScalarValueSyntax(FilterToken Token) : FilterValueSyntax
{
    public override TextSpan Span => Token.Span;
}

public sealed record FilterRangeValueSyntax(
    FilterScalarValueSyntax? Lower,
    FilterToken Separator,
    FilterScalarValueSyntax? Upper) : FilterValueSyntax
{
    public override TextSpan Span =>
        (Lower?.Span ?? Separator.Span).Union(Upper?.Span ?? Separator.Span);
}

public sealed record FilterListValueSyntax(
    FilterToken OpenParenthesis,
    IReadOnlyList<FilterScalarValueSyntax> Values,
    IReadOnlyList<FilterToken> Separators,
    FilterToken CloseParenthesis) : FilterValueSyntax
{
    public override TextSpan Span => OpenParenthesis.Span.Union(CloseParenthesis.Span);
}
