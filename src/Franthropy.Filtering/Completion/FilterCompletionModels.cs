using Franthropy.Filtering.Diagnostics;
using Franthropy.Filtering.Syntax;

namespace Franthropy.Filtering.Completion;

public enum FilterCompletionKind
{
    Field,
    Value,
    Operator,
    Function,
    Keyword,
}

public sealed record FilterCompletionItem(
    string Label,
    string InsertionText,
    FilterCompletionKind Kind,
    TextSpan ReplacementSpan,
    string? Description = null,
    string? Detail = null);

public sealed record FilterCompletionRequest(
    string ContextId,
    string Expression,
    int CaretPosition,
    string? CatalogVersion = null,
    string? ContextSchemaVersion = null,
    string? Locale = null);

public sealed record FilterCompletionResult(
    string CatalogVersion,
    string ContextId,
    string ContextSchemaVersion,
    IReadOnlyList<FilterCompletionItem> Items,
    IReadOnlyList<FilterDiagnostic> Diagnostics);
