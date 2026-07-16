using System.Text;

namespace Franthropy.Filtering.Syntax;

public static class FilterFormatter
{
    public static string Format(FilterSyntaxTree tree) => Format(tree.Root.Expression);

    public static string Format(FilterExpressionSyntax expression)
    {
        var builder = new StringBuilder();
        WriteExpression(builder, expression, 0);
        return builder.ToString();
    }

    private static void WriteExpression(StringBuilder builder, FilterExpressionSyntax expression, int parentPrecedence)
    {
        switch (expression)
        {
            case FilterMissingExpressionSyntax:
                return;
            case FilterFreeTextSyntax freeText:
                WriteTokenValue(builder, freeText.Text);
                return;
            case FilterFieldExpressionSyntax field:
                builder.Append(field.Field.Value);
                builder.Append(field.Comparator.Text);
                WriteValue(builder, field.Value);
                return;
            case FilterFunctionCallSyntax function:
                builder.Append(function.Function.Value.ToLowerInvariant());
                builder.Append('(');
                builder.Append(function.Field.Value);
                builder.Append(')');
                return;
            case FilterUnaryExpressionSyntax unary:
            {
                const int precedence = 3;
                var needsParentheses = precedence < parentPrecedence;
                if (needsParentheses)
                    builder.Append('(');
                builder.Append("NOT ");
                WriteExpression(builder, unary.Operand, precedence);
                if (needsParentheses)
                    builder.Append(')');
                return;
            }
            case FilterBinaryExpressionSyntax binary:
            {
                var precedence = binary.Operator == FilterBinaryOperator.And ? 2 : 1;
                var needsParentheses = precedence < parentPrecedence;
                if (needsParentheses)
                    builder.Append('(');
                WriteExpression(builder, binary.Left, precedence);
                builder.Append(binary.Operator == FilterBinaryOperator.And ? " AND " : " OR ");
                WriteExpression(builder, binary.Right, precedence + 1);
                if (needsParentheses)
                    builder.Append(')');
                return;
            }
            case FilterParenthesizedExpressionSyntax parenthesized:
                builder.Append('(');
                WriteExpression(builder, parenthesized.Expression, 0);
                builder.Append(')');
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(expression), expression.GetType().Name);
        }
    }

    private static void WriteValue(StringBuilder builder, FilterValueSyntax value)
    {
        switch (value)
        {
            case FilterMissingValueSyntax:
                return;
            case FilterScalarValueSyntax scalar:
                WriteTokenValue(builder, scalar.Token);
                return;
            case FilterRangeValueSyntax range:
                if (range.Lower is not null)
                    WriteTokenValue(builder, range.Lower.Token);
                builder.Append("..");
                if (range.Upper is not null)
                    WriteTokenValue(builder, range.Upper.Token);
                return;
            case FilterListValueSyntax list:
                builder.Append('(');
                for (var i = 0; i < list.Values.Count; i++)
                {
                    if (i > 0)
                        builder.Append(" | ");
                    WriteTokenValue(builder, list.Values[i].Token);
                }
                builder.Append(')');
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value.GetType().Name);
        }
    }

    private static void WriteTokenValue(StringBuilder builder, FilterToken token)
    {
        if (token.Kind != FilterTokenKind.QuotedString)
        {
            builder.Append(token.Value);
            return;
        }

        builder.Append('"');
        builder.Append(token.Value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal));
        builder.Append('"');
    }
}
