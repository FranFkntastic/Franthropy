using Franthropy.Filtering.Diagnostics;

namespace Franthropy.Filtering.Syntax;

internal sealed class FilterParser(
    IReadOnlyList<FilterToken> tokens,
    FilterLimits limits,
    DiagnosticBag diagnostics)
{
    private int position;
    private int nestingDepth;

    public FilterQuerySyntax ParseQuery()
    {
        var expression = Current.Kind == FilterTokenKind.EndOfFile
            ? new FilterMissingExpressionSyntax(TextSpan.EmptyAt(Current.Span.Start))
            : ParseOrExpression();

        while (Current.Kind != FilterTokenKind.EndOfFile)
        {
            diagnostics.Add(
                FilterDiagnosticCodes.UnexpectedToken,
                $"Unexpected token '{Current.Text}'.",
                Current.Span);
            position++;
        }

        return new FilterQuerySyntax(expression, Current);
    }

    private FilterExpressionSyntax ParseOrExpression()
    {
        var left = ParseAndExpression();
        while (Current.Kind is FilterTokenKind.OrKeyword or FilterTokenKind.Pipe)
        {
            var operatorToken = NextToken();
            var right = CanStartExpression(Current.Kind)
                ? ParseAndExpression()
                : MissingExpression("Expected an expression after OR.");
            left = new FilterBinaryExpressionSyntax(left, FilterBinaryOperator.Or, operatorToken, right, false);
        }

        return left;
    }

    private FilterExpressionSyntax ParseAndExpression()
    {
        var left = ParseUnaryExpression();
        while (true)
        {
            if (Current.Kind is FilterTokenKind.AndKeyword or FilterTokenKind.AmpersandAmpersand)
            {
                var operatorToken = NextToken();
                var right = CanStartExpression(Current.Kind)
                    ? ParseUnaryExpression()
                    : MissingExpression("Expected an expression after AND.");
                left = new FilterBinaryExpressionSyntax(left, FilterBinaryOperator.And, operatorToken, right, false);
                continue;
            }

            if (!CanStartExpression(Current.Kind))
                break;

            var rightImplicit = ParseUnaryExpression();
            left = new FilterBinaryExpressionSyntax(left, FilterBinaryOperator.And, null, rightImplicit, true);
        }

        return left;
    }

    private FilterExpressionSyntax ParseUnaryExpression()
    {
        if (Current.Kind is FilterTokenKind.NotKeyword or FilterTokenKind.Bang or FilterTokenKind.Minus)
        {
            var operatorToken = NextToken();
            var operand = CanStartExpression(Current.Kind)
                ? ParseUnaryExpression()
                : MissingExpression("Expected an expression after NOT.");
            return new FilterUnaryExpressionSyntax(operatorToken, operand);
        }

        return ParsePrimaryExpression();
    }

    private FilterExpressionSyntax ParsePrimaryExpression()
    {
        if (Current.Kind == FilterTokenKind.LeftParenthesis)
            return ParseParenthesizedExpression();

        if (IsEvidenceFunction(Current) && Peek(1).Kind == FilterTokenKind.LeftParenthesis)
            return ParseFunctionCall();

        if (Current.Kind is FilterTokenKind.Word or FilterTokenKind.QuotedString)
        {
            var token = NextToken();
            FilterToken? separator = null;
            if (token.Kind == FilterTokenKind.Word &&
                Current.Kind == FilterTokenKind.Colon &&
                IsComparisonOperator(Peek(1).Kind) &&
                !Peek(1).HasLeadingWhitespace)
            {
                separator = NextToken();
            }

            if (token.Kind == FilterTokenKind.Word && Current.IsComparator)
            {
                var comparator = NextToken();
                var value = ParseValue();
                return new FilterFieldExpressionSyntax(token, comparator, value) { Separator = separator };
            }

            return new FilterFreeTextSyntax(token);
        }

        var unexpected = NextToken();
        diagnostics.Add(
            FilterDiagnosticCodes.ExpectedExpression,
            $"Expected an expression instead of '{unexpected.Text}'.",
            unexpected.Span);
        return new FilterMissingExpressionSyntax(unexpected.Span);
    }

    private FilterExpressionSyntax ParseParenthesizedExpression()
    {
        var open = NextToken();
        if (++nestingDepth > limits.MaximumNestingDepth)
        {
            diagnostics.Add(
                FilterDiagnosticCodes.NestingTooDeep,
                $"This filter is nested more than {limits.MaximumNestingDepth:N0} levels deep.",
                open.Span);
        }

        var expression = Current.Kind == FilterTokenKind.RightParenthesis
            ? MissingExpression("Expected an expression inside the parentheses.")
            : ParseOrExpression();
        var close = MatchClosingParenthesis();
        nestingDepth--;
        return new FilterParenthesizedExpressionSyntax(open, expression, close);
    }

    private FilterExpressionSyntax ParseFunctionCall()
    {
        var function = NextToken();
        var open = NextToken();
        if (++nestingDepth > limits.MaximumNestingDepth)
        {
            diagnostics.Add(
                FilterDiagnosticCodes.NestingTooDeep,
                $"This filter is nested more than {limits.MaximumNestingDepth:N0} levels deep.",
                open.Span);
        }

        FilterToken field;
        if (Current.Kind == FilterTokenKind.Word)
        {
            field = NextToken();
        }
        else
        {
            diagnostics.Add(
                FilterDiagnosticCodes.ExpectedValue,
                $"Expected a field name inside {function.Value}(...).",
                Current.Span);
            field = MissingToken(FilterTokenKind.Word, Current.Span.Start);
        }

        var close = MatchClosingParenthesis();
        nestingDepth--;
        return new FilterFunctionCallSyntax(function, open, field, close);
    }

    private FilterValueSyntax ParseValue()
    {
        if (Current.Kind == FilterTokenKind.LeftParenthesis)
            return ParseListValue();

        if (Current.Kind == FilterTokenKind.DotDot)
        {
            var separator = NextToken();
            var upper = CanBeScalar(Current.Kind) ? ParseScalar() : null;
            if (upper is null)
            {
                diagnostics.Add(
                    FilterDiagnosticCodes.RangeNeedsEndpoint,
                    "A range needs at least one endpoint.",
                    separator.Span);
            }

            return new FilterRangeValueSyntax(null, separator, upper);
        }

        if (!CanBeScalar(Current.Kind))
            return MissingValue();

        var lower = ParseScalar();
        if (Current.Kind != FilterTokenKind.DotDot)
            return lower;

        var rangeSeparator = NextToken();
        var upperValue = CanBeScalar(Current.Kind) ? ParseScalar() : null;
        return new FilterRangeValueSyntax(lower, rangeSeparator, upperValue);
    }

    private FilterValueSyntax ParseListValue()
    {
        var open = NextToken();
        if (++nestingDepth > limits.MaximumNestingDepth)
        {
            diagnostics.Add(
                FilterDiagnosticCodes.NestingTooDeep,
                $"This filter is nested more than {limits.MaximumNestingDepth:N0} levels deep.",
                open.Span);
        }

        var values = new List<FilterScalarValueSyntax>();
        var separators = new List<FilterToken>();
        while (Current.Kind is not FilterTokenKind.RightParenthesis and not FilterTokenKind.EndOfFile)
        {
            if (!CanBeScalar(Current.Kind))
            {
                diagnostics.Add(
                    FilterDiagnosticCodes.ExpectedValue,
                    "Expected a value in this list.",
                    Current.Span);
                position++;
                continue;
            }

            if (values.Count < limits.MaximumListValues)
            {
                values.Add(ParseScalar());
            }
            else
            {
                diagnostics.Add(
                    FilterDiagnosticCodes.ListTooLong,
                    $"A filter list cannot contain more than {limits.MaximumListValues:N0} values.",
                    Current.Span);
                ParseScalar();
            }

            if (Current.Kind == FilterTokenKind.Pipe)
            {
                separators.Add(NextToken());
                continue;
            }

            if (Current.Kind != FilterTokenKind.RightParenthesis)
            {
                diagnostics.Add(
                    FilterDiagnosticCodes.ExpectedListSeparator,
                    "Expected '|' or ')' after the list value.",
                    Current.Span);
                if (CanBeScalar(Current.Kind))
                    continue;
                position++;
            }
        }

        if (values.Count == 0)
        {
            diagnostics.Add(
                FilterDiagnosticCodes.ExpectedValue,
                "Expected at least one value inside the list.",
                Current.Span);
        }

        var close = MatchClosingParenthesis();
        nestingDepth--;
        return new FilterListValueSyntax(open, values, separators, close);
    }

    private FilterScalarValueSyntax ParseScalar() => new(NextToken());

    private FilterMissingValueSyntax MissingValue()
    {
        diagnostics.Add(
            FilterDiagnosticCodes.ExpectedValue,
            "Expected a value after the field operator.",
            Current.Span);
        return new FilterMissingValueSyntax(TextSpan.EmptyAt(Current.Span.Start));
    }

    private FilterMissingExpressionSyntax MissingExpression(string message)
    {
        diagnostics.Add(FilterDiagnosticCodes.ExpectedExpression, message, Current.Span);
        return new FilterMissingExpressionSyntax(TextSpan.EmptyAt(Current.Span.Start));
    }

    private FilterToken MatchClosingParenthesis()
    {
        if (Current.Kind == FilterTokenKind.RightParenthesis)
            return NextToken();

        diagnostics.Add(
            FilterDiagnosticCodes.ExpectedClosingParenthesis,
            "Expected a closing ')'.",
            Current.Span);
        return MissingToken(FilterTokenKind.RightParenthesis, Current.Span.Start);
    }

    private static bool IsEvidenceFunction(FilterToken token) =>
        token.Kind == FilterTokenKind.Word &&
        (token.Value.Equals("known", StringComparison.OrdinalIgnoreCase) ||
         token.Value.Equals("unknown", StringComparison.OrdinalIgnoreCase));

    private static bool CanStartExpression(FilterTokenKind kind) => kind is
        FilterTokenKind.Word or
        FilterTokenKind.QuotedString or
        FilterTokenKind.LeftParenthesis or
        FilterTokenKind.NotKeyword or
        FilterTokenKind.Bang or
        FilterTokenKind.Minus;

    private static bool CanBeScalar(FilterTokenKind kind) => kind is
        FilterTokenKind.Word or FilterTokenKind.QuotedString;

    private static bool IsComparisonOperator(FilterTokenKind kind) => kind is
        FilterTokenKind.Equals or
        FilterTokenKind.BangEquals or
        FilterTokenKind.Less or
        FilterTokenKind.LessOrEqual or
        FilterTokenKind.Greater or
        FilterTokenKind.GreaterOrEqual;

    private FilterToken Current => Peek(0);

    private FilterToken Peek(int offset)
    {
        var index = Math.Min(position + offset, tokens.Count - 1);
        return tokens[index];
    }

    private FilterToken NextToken()
    {
        var current = Current;
        if (position < tokens.Count - 1)
            position++;
        return current;
    }

    private static FilterToken MissingToken(FilterTokenKind kind, int position) =>
        new(kind, string.Empty, string.Empty, TextSpan.EmptyAt(position));
}
