using Franthropy.Filtering.Syntax;

namespace Franthropy.Filtering.Diagnostics;

public enum FilterDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record FilterFixSuggestion(string Replacement, TextSpan Span, string Description);

public sealed record FilterDiagnostic(
    string Code,
    FilterDiagnosticSeverity Severity,
    string Message,
    TextSpan Span,
    IReadOnlyList<FilterFixSuggestion>? Fixes = null);

public static class FilterDiagnosticCodes
{
    public const string QueryTooLong = "FLT0001";
    public const string TooManyTokens = "FLT0002";
    public const string NestingTooDeep = "FLT0003";
    public const string ListTooLong = "FLT0004";
    public const string UnexpectedCharacter = "FLT1001";
    public const string UnterminatedString = "FLT1002";
    public const string InvalidEscape = "FLT1003";
    public const string ExpectedExpression = "FLT2001";
    public const string ExpectedValue = "FLT2002";
    public const string ExpectedClosingParenthesis = "FLT2003";
    public const string ExpectedListSeparator = "FLT2004";
    public const string RangeNeedsEndpoint = "FLT2005";
    public const string UnexpectedToken = "FLT2006";
    public const string UnknownField = "FLT3001";
    public const string UnavailableField = "FLT3002";
    public const string AmbiguousField = "FLT3003";
    public const string InvalidValue = "FLT3004";
    public const string AmbiguousValue = "FLT3005";
    public const string InvalidOperator = "FLT3006";
    public const string NoDefaultTextField = "FLT3007";
}

internal sealed class DiagnosticBag(int maximumCount)
{
    private readonly List<FilterDiagnostic> diagnostics = [];

    public IReadOnlyList<FilterDiagnostic> Diagnostics => diagnostics;

    public void Add(FilterDiagnostic diagnostic)
    {
        if (diagnostics.Count < maximumCount)
            diagnostics.Add(diagnostic);
    }

    public void Add(string code, string message, TextSpan span) =>
        Add(new FilterDiagnostic(code, FilterDiagnosticSeverity.Error, message, span));

    public void AddRange(IEnumerable<FilterDiagnostic> source)
    {
        foreach (var diagnostic in source)
            Add(diagnostic);
    }
}
