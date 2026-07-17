using Franthropy.Filtering.Diagnostics;
using Franthropy.Filtering.Evaluation;
using Franthropy.Filtering.Syntax;

namespace Franthropy.Filtering.Compilation;

public sealed class FilterCompilation<TRecord>
{
    internal FilterCompilation(
        FilterSyntaxTree syntax,
        IReadOnlyList<FilterDiagnostic> diagnostics,
        Func<TRecord, FilterTruth> evaluator)
    {
        Syntax = syntax;
        Diagnostics = diagnostics;
        this.evaluator = evaluator;
        NormalizedExpression = FilterFormatter.Format(syntax);
    }

    private readonly Func<TRecord, FilterTruth> evaluator;
    public FilterSyntaxTree Syntax { get; }
    public IReadOnlyList<FilterDiagnostic> Diagnostics { get; }
    public string NormalizedExpression { get; }
    public string SemanticExpression { get; internal init; } = string.Empty;
    public bool IsValid => !Diagnostics.Any(diagnostic => diagnostic.Severity == FilterDiagnosticSeverity.Error);
    public FilterTruth Evaluate(TRecord record) => evaluator(record);
    public bool Matches(TRecord record) => IsValid && Evaluate(record) == FilterTruth.True;
}
