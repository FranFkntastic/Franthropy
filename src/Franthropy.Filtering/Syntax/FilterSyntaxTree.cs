using Franthropy.Filtering.Diagnostics;

namespace Franthropy.Filtering.Syntax;

public sealed record FilterSyntaxTree(
    string Source,
    FilterQuerySyntax Root,
    IReadOnlyList<FilterToken> Tokens,
    IReadOnlyList<FilterDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(x => x.Severity == FilterDiagnosticSeverity.Error);

    public static FilterSyntaxTree Parse(string? expression, FilterLimits? limits = null)
    {
        var source = expression ?? string.Empty;
        var parseSource = source;
        var effectiveLimits = (limits ?? FilterLimits.Default).Validate();
        var diagnostics = new DiagnosticBag(effectiveLimits.MaximumDiagnostics);
        if (source.Length > effectiveLimits.MaximumExpressionLength)
        {
            diagnostics.Add(
                FilterDiagnosticCodes.QueryTooLong,
                $"This filter is longer than {effectiveLimits.MaximumExpressionLength:N0} characters.",
                TextSpan.FromBounds(effectiveLimits.MaximumExpressionLength, source.Length));
            parseSource = source[..effectiveLimits.MaximumExpressionLength];
        }

        var tokenizer = new FilterTokenizer(parseSource, effectiveLimits, diagnostics);
        var tokens = tokenizer.Tokenize();
        var parser = new FilterParser(tokens, effectiveLimits, diagnostics);
        var root = parser.ParseQuery();
        return new FilterSyntaxTree(source, root, tokens, diagnostics.Diagnostics.ToArray());
    }
}
