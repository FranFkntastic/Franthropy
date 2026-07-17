using System.Text;
using Franthropy.Filtering.Semantics;
using Franthropy.Filtering.Syntax;

namespace Franthropy.Filtering.Compilation;

internal static class FilterSemanticFormatter
{
    public static string Format(FilterSyntaxTree tree, FilterCatalog catalog, IReadOnlySet<string> availableKeys)
    {
        var builder = new StringBuilder();
        Write(builder, tree.Root.Expression, catalog, availableKeys, 0);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, FilterExpressionSyntax expression, FilterCatalog catalog,
        IReadOnlySet<string> availableKeys, int parentPrecedence)
    {
        switch (expression)
        {
            case FilterMissingExpressionSyntax:
                return;
            case FilterFreeTextSyntax free:
                builder.Append("$text=").Append(Quote(FilterText.Normalize(free.Text.Value)));
                return;
            case FilterFieldExpressionSyntax field:
                WriteField(builder, field, catalog, availableKeys);
                return;
            case FilterReservedNestedQualifierSyntax nested:
                builder.Append("$reserved:").Append(string.Join(':', nested.Segments.Select(segment => segment.Value.ToLowerInvariant()))).Append(':');
                if (nested.Comparator is not null)
                    builder.Append(nested.Comparator.Text);
                WriteRawValue(builder, nested.Value);
                return;
            case FilterFunctionCallSyntax function:
                var resolvedFunctionField = catalog.Resolve(function.Field.Value, availableKeys);
                builder.Append(function.Function.Value.ToLowerInvariant()).Append('(')
                    .Append(resolvedFunctionField.Field?.Key ?? function.Field.Value.ToLowerInvariant()).Append(')');
                return;
            case FilterUnaryExpressionSyntax unary:
                builder.Append('!');
                Write(builder, unary.Operand, catalog, availableKeys, 3);
                return;
            case FilterParenthesizedExpressionSyntax parenthesized:
                builder.Append('(');
                Write(builder, parenthesized.Expression, catalog, availableKeys, 0);
                builder.Append(')');
                return;
            case FilterBinaryExpressionSyntax binary:
                var precedence = binary.Operator == FilterBinaryOperator.And ? 2 : 1;
                var parentheses = precedence < parentPrecedence;
                if (parentheses) builder.Append('(');
                Write(builder, binary.Left, catalog, availableKeys, precedence);
                builder.Append(binary.Operator == FilterBinaryOperator.And ? " AND " : " OR ");
                Write(builder, binary.Right, catalog, availableKeys, precedence + 1);
                if (parentheses) builder.Append(')');
                return;
        }
    }

    private static void WriteField(StringBuilder builder, FilterFieldExpressionSyntax syntax, FilterCatalog catalog,
        IReadOnlySet<string> availableKeys)
    {
        if (syntax.Comparator.Kind == FilterTokenKind.Colon && syntax.Value is FilterScalarValueSyntax scalar)
        {
            var predicate = catalog.ResolvePredicate(syntax.Field.Value, scalar.Token.Value);
            if (predicate is not null)
            {
                var target = catalog.Resolve(predicate.TargetFieldKey, availableKeys).Field;
                builder.Append(predicate.TargetFieldKey).Append("==")
                    .Append(Quote(target?.NormalizeLiteral(predicate.TargetValue, false) ?? predicate.TargetValue));
                return;
            }
        }

        var resolution = catalog.Resolve(syntax.Field.Value, availableKeys);
        var field = resolution.Field;
        builder.Append(field?.Key ?? syntax.Field.Value.ToLowerInvariant());
        var comparison = FilterComparisonOperatorExtensions.TryFromToken(syntax.Comparator, out var parsed)
            ? parsed
            : FilterComparisonOperator.Match;
        var semanticOperator = comparison switch
        {
            FilterComparisonOperator.Match when field?.ValueKind == FilterValueKind.Text => "=",
            FilterComparisonOperator.Match when field?.MatchUsesRecordFuzzy == true => ":",
            FilterComparisonOperator.Match => "==",
            _ => comparison.Display(),
        };
        builder.Append(semanticOperator);
        if (comparison == FilterComparisonOperator.Match && field?.MatchUsesRecordFuzzy == true)
        {
            WriteValue(builder, syntax.Value, null, false);
            return;
        }
        WriteValue(builder, syntax.Value, field,
            comparison is FilterComparisonOperator.Equals or FilterComparisonOperator.NotEquals);
    }

    private static void WriteValue(StringBuilder builder, FilterValueSyntax value, FilterField? field, bool fuzzy)
    {
        switch (value)
        {
            case FilterScalarValueSyntax scalar:
                builder.Append(Quote(field?.NormalizeLiteral(scalar.Token.Value, fuzzy) ?? FilterText.Normalize(scalar.Token.Value)));
                break;
            case FilterRangeValueSyntax range:
                if (range.Lower is not null) builder.Append(Quote(field?.NormalizeLiteral(range.Lower.Token.Value, false) ?? range.Lower.Token.Value));
                builder.Append("..");
                if (range.Upper is not null) builder.Append(Quote(field?.NormalizeLiteral(range.Upper.Token.Value, false) ?? range.Upper.Token.Value));
                break;
            case FilterListValueSyntax list:
                builder.Append('(');
                for (var i = 0; i < list.Values.Count; i++)
                {
                    if (i > 0) builder.Append('|');
                    builder.Append(Quote(field?.NormalizeLiteral(list.Values[i].Token.Value, fuzzy) ?? list.Values[i].Token.Value));
                }
                builder.Append(')');
                break;
        }
    }

    private static void WriteRawValue(StringBuilder builder, FilterValueSyntax value) =>
        builder.Append(FilterFormatter.Format(new FilterFieldExpressionSyntax(
            new FilterToken(FilterTokenKind.Word, string.Empty, string.Empty, TextSpan.EmptyAt(0)),
            new FilterToken(FilterTokenKind.Colon, string.Empty, string.Empty, TextSpan.EmptyAt(0)), value)));

    private static string Quote(string value) => value.Any(character => char.IsWhiteSpace(character) || character is '(' or ')' or '|')
        ? $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\""
        : value;
}
