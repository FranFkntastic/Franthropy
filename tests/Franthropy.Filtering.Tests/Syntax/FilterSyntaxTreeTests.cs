using Franthropy.Filtering.Diagnostics;
using Franthropy.Filtering.Syntax;

namespace Franthropy.Filtering.Tests.Syntax;

public sealed class FilterSyntaxTreeTests
{
    [Fact]
    public void EmptyExpression_IsAValidMatchAllSyntaxTree()
    {
        var tree = FilterSyntaxTree.Parse(string.Empty);

        Assert.False(tree.HasErrors);
        Assert.IsType<FilterMissingExpressionSyntax>(tree.Root.Expression);
        Assert.Equal(string.Empty, FilterFormatter.Format(tree));
    }

    [Fact]
    public void Tokenizer_PreservesDecodedQuotedValueAndExactSpan()
    {
        const string expression = "item:\"Aetheryte \\\"Ring\\\"\"";

        var tree = FilterSyntaxTree.Parse(expression);

        Assert.False(tree.HasErrors);
        var field = Assert.IsType<FilterFieldExpressionSyntax>(tree.Root.Expression);
        var scalar = Assert.IsType<FilterScalarValueSyntax>(field.Value);
        Assert.Equal("item", field.Field.Value);
        Assert.Equal("Aetheryte \"Ring\"", scalar.Token.Value);
        Assert.Equal(expression.IndexOf('"'), scalar.Span.Start);
        Assert.Equal(expression.Length, scalar.Span.End);
    }

    [Fact]
    public void Parser_UsesAndBeforeOrPrecedence()
    {
        var tree = FilterSyntaxTree.Parse("a b OR c");

        var root = Assert.IsType<FilterBinaryExpressionSyntax>(tree.Root.Expression);
        Assert.Equal(FilterBinaryOperator.Or, root.Operator);
        var left = Assert.IsType<FilterBinaryExpressionSyntax>(root.Left);
        Assert.Equal(FilterBinaryOperator.And, left.Operator);
        Assert.True(left.IsImplicit);
    }

    [Fact]
    public void Parser_ParsesQualifiedFieldRangeAndOpenEndpoints()
    {
        var closed = FilterSyntaxTree.Parse("offer.price:1000..2500");
        var open = FilterSyntaxTree.Parse("condition:80..");

        var closedField = Assert.IsType<FilterFieldExpressionSyntax>(closed.Root.Expression);
        var closedRange = Assert.IsType<FilterRangeValueSyntax>(closedField.Value);
        Assert.Equal("offer.price", closedField.Field.Value);
        Assert.Equal("1000", closedRange.Lower?.Token.Value);
        Assert.Equal("2500", closedRange.Upper?.Token.Value);

        var openRange = Assert.IsType<FilterRangeValueSyntax>(
            Assert.IsType<FilterFieldExpressionSyntax>(open.Root.Expression).Value);
        Assert.Equal("80", openRange.Lower?.Token.Value);
        Assert.Null(openRange.Upper);
    }

    [Fact]
    public void Parser_AcceptsFriendlyColonBeforeAnOrderedComparator()
    {
        const string expression = "quantity:>99";

        var tree = FilterSyntaxTree.Parse(expression);

        Assert.False(tree.HasErrors);
        var field = Assert.IsType<FilterFieldExpressionSyntax>(tree.Root.Expression);
        Assert.Equal(":", field.Separator?.Text);
        Assert.Equal(">", field.Comparator.Text);
        Assert.Equal("99", Assert.IsType<FilterScalarValueSyntax>(field.Value).Token.Value);
        Assert.Equal(expression, FilterFormatter.Format(tree));
    }

    [Fact]
    public void Parser_ParsesFieldValueListWithoutTreatingPipeAsExpressionOr()
    {
        var tree = FilterSyntaxTree.Parse("job:(WHM | SCH | AST)");

        Assert.False(tree.HasErrors);
        var field = Assert.IsType<FilterFieldExpressionSyntax>(tree.Root.Expression);
        var list = Assert.IsType<FilterListValueSyntax>(field.Value);
        Assert.Equal(["WHM", "SCH", "AST"], list.Values.Select(x => x.Token.Value));
        Assert.Equal(2, list.Separators.Count);
    }

    [Theory]
    [InlineData("known(price)", "known", "price")]
    [InlineData("unknown(instance.condition)", "unknown", "instance.condition")]
    public void Parser_ParsesEvidenceFunctions(string expression, string functionName, string fieldName)
    {
        var tree = FilterSyntaxTree.Parse(expression);

        var function = Assert.IsType<FilterFunctionCallSyntax>(tree.Root.Expression);
        Assert.Equal(functionName, function.Function.Value);
        Assert.Equal(fieldName, function.Field.Value);
    }

    [Fact]
    public void Parser_ReportsIncompleteFieldValueAtInsertionPoint()
    {
        var tree = FilterSyntaxTree.Parse("job:");

        var diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal(FilterDiagnosticCodes.ExpectedValue, diagnostic.Code);
        Assert.Equal(4, diagnostic.Span.Start);
        Assert.Equal(0, diagnostic.Span.Length);
    }

    [Fact]
    public void Parser_ReportsUnterminatedStringWithoutThrowingAwayValue()
    {
        var tree = FilterSyntaxTree.Parse("item:\"Aetheryte Ring");

        Assert.Contains(tree.Diagnostics, x => x.Code == FilterDiagnosticCodes.UnterminatedString);
        var scalar = Assert.IsType<FilterScalarValueSyntax>(
            Assert.IsType<FilterFieldExpressionSyntax>(tree.Root.Expression).Value);
        Assert.Equal("Aetheryte Ring", scalar.Token.Value);
    }

    [Fact]
    public void Parser_ReportsRangeWithNoEndpoint()
    {
        var tree = FilterSyntaxTree.Parse("price:..");

        Assert.Contains(tree.Diagnostics, x => x.Code == FilterDiagnosticCodes.RangeNeedsEndpoint);
    }

    [Fact]
    public void Limits_ReportLengthTokenNestingAndListFailures()
    {
        var structuralLimits = new FilterLimits(
            MaximumExpressionLength: 20,
            MaximumTokenCount: 20,
            MaximumNestingDepth: 1,
            MaximumListValues: 2,
            MaximumDiagnostics: 20);
        var tokenLimits = structuralLimits with { MaximumTokenCount = 6 };

        var tooLong = FilterSyntaxTree.Parse(new string('a', 21), structuralLimits);
        var tooManyTokens = FilterSyntaxTree.Parse("a b c d e f", tokenLimits);
        var tooDeep = FilterSyntaxTree.Parse("((a))", structuralLimits);
        var tooManyValues = FilterSyntaxTree.Parse("j:(a|b|c)", structuralLimits);

        Assert.Contains(tooLong.Diagnostics, x => x.Code == FilterDiagnosticCodes.QueryTooLong);
        Assert.Contains(tooManyTokens.Diagnostics, x => x.Code == FilterDiagnosticCodes.TooManyTokens);
        Assert.Contains(tooDeep.Diagnostics, x => x.Code == FilterDiagnosticCodes.NestingTooDeep);
        Assert.Contains(tooManyValues.Diagnostics, x => x.Code == FilterDiagnosticCodes.ListTooLong);
    }

    [Fact]
    public void Formatter_PreservesHumanSpellingAndPrecedence()
    {
        var tree = FilterSyntaxTree.Parse("-unique (job:WHM||job:SCH) ilvl>=660");

        var formatted = FilterFormatter.Format(tree);
        var reparsed = FilterSyntaxTree.Parse(formatted);

        Assert.Equal("-unique (job:WHM || job:SCH) ilvl>=660", formatted);
        Assert.False(reparsed.HasErrors);
        Assert.Equal(formatted, FilterFormatter.Format(reparsed));
    }

    [Theory]
    [InlineData("name=iron", FilterTokenKind.Equals)]
    [InlineData("name!=iron", FilterTokenKind.BangEquals)]
    [InlineData("name==iron", FilterTokenKind.ExactEquals)]
    [InlineData("name!==iron", FilterTokenKind.ExactNotEquals)]
    public void Tokenizer_UsesLongestComparisonOperator(string expression, FilterTokenKind expected)
    {
        var field = Assert.IsType<FilterFieldExpressionSyntax>(FilterSyntaxTree.Parse(expression).Root.Expression);
        Assert.Equal(expected, field.Comparator.Kind);
    }

    [Theory]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("a AND")]
    [InlineData("a OR")]
    [InlineData("!")]
    [InlineData("job:(WHM|")]
    [InlineData("known(")]
    [InlineData("&&&&&&")]
    [InlineData("\"\\")]
    public void IncompleteOrHostileInput_NeverThrows(string expression)
    {
        var exception = Record.Exception(() => FilterSyntaxTree.Parse(expression));

        Assert.Null(exception);
    }
}
