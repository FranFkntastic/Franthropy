using Franthropy.Filtering.Compilation;
using Franthropy.Filtering.Semantics;
using Franthropy.Filtering.Syntax;

namespace Franthropy.Filtering.Completion;

public static class FilterCompletionService
{
    private static readonly char[] ComparatorCharacters = [':', '=', '!', '<', '>'];

    public static FilterCompletionResult Complete<TRecord>(
        FilterContext<TRecord> context,
        FilterCompletionRequest request,
        int maximumItems = 24)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (maximumItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumItems));

        var expression = request.Expression ?? string.Empty;
        var caret = Math.Clamp(request.CaretPosition, 0, expression.Length);
        var replacement = FindReplacementSpan(expression, caret);
        var prefix = expression[replacement.Start..caret].Trim('"');
        var items = CompleteValues(context, expression, replacement, prefix, maximumItems)
                    ?? CompleteFunctionField(context, expression, replacement, prefix)
                    ?? CompleteFields(context, replacement, prefix);
        var diagnostics = FilterCompiler.Compile(expression, context).Diagnostics;

        return new FilterCompletionResult(
            context.Catalog.Version,
            context.ContextId,
            context.SchemaVersion,
            items.Take(maximumItems).ToArray(),
            diagnostics);
    }

    private static IReadOnlyList<FilterCompletionItem>? CompleteValues<TRecord>(
        FilterContext<TRecord> context,
        string expression,
        TextSpan replacement,
        string prefix,
        int maximumItems)
    {
        var before = expression[..replacement.Start].TrimEnd();
        var comparatorStart = before.LastIndexOfAny(ComparatorCharacters);
        if (comparatorStart < 0)
            return null;

        var comparatorEnd = comparatorStart + 1;
        if (comparatorStart > 0 && before[comparatorStart - 1] is '!' or '<' or '>')
            comparatorStart--;
        if (before[comparatorStart..comparatorEnd].Any(char.IsWhiteSpace))
            return null;

        var fieldEnd = comparatorStart;
        while (fieldEnd > 0 && char.IsWhiteSpace(before[fieldEnd - 1]))
            fieldEnd--;
        var fieldStart = fieldEnd;
        while (fieldStart > 0 && IsFieldCharacter(before[fieldStart - 1]))
            fieldStart--;
        if (fieldStart == fieldEnd)
            return null;

        var resolution = context.Catalog.Resolve(before[fieldStart..fieldEnd], context.AvailableKeys);
        if (resolution.Kind != FilterFieldResolutionKind.Success || resolution.Field is null ||
            !context.AvailableKeys.Contains(resolution.Field.Key))
            return null;

        return resolution.Field.Values
            .SelectMany(value => CandidateNames(value).Select(name => (Value: value, Name: name)))
            .Where(candidate => Matches(candidate.Name, prefix))
            .GroupBy(candidate => candidate.Value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(candidate => Rank(candidate.Name, prefix))
            .ThenBy(candidate => candidate.Value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new FilterCompletionItem(
                candidate.Value.DisplayName,
                QuoteWhenNeeded(candidate.Value.DisplayName),
                FilterCompletionKind.Value,
                replacement,
                resolution.Field.Description,
                resolution.Field.Key))
            .Take(maximumItems)
            .ToArray();
    }

    private static IReadOnlyList<FilterCompletionItem>? CompleteFunctionField<TRecord>(
        FilterContext<TRecord> context,
        string expression,
        TextSpan replacement,
        string prefix)
    {
        var before = expression[..replacement.Start];
        var open = before.LastIndexOf('(');
        if (open < 0)
            return null;
        var function = before[..open].TrimEnd();
        var functionStart = function.Length;
        while (functionStart > 0 && char.IsLetter(function[functionStart - 1]))
            functionStart--;
        if (!function[functionStart..].Equals("known", StringComparison.OrdinalIgnoreCase) &&
            !function[functionStart..].Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return null;
        return CompleteFields(context, replacement, prefix);
    }

    private static IReadOnlyList<FilterCompletionItem> CompleteFields<TRecord>(
        FilterContext<TRecord> context,
        TextSpan replacement,
        string prefix)
    {
        var fields = context.Catalog.Fields
            .Where(field => context.AvailableKeys.Contains(field.Key))
            .Select(field => (Field: field, Insertion: PreferredFieldName(field)))
            .Where(candidate => FieldNames(candidate.Field).Any(name => Matches(name, prefix)))
            .OrderBy(candidate => FieldNames(candidate.Field).Min(name => Rank(name, prefix)))
            .ThenBy(candidate => candidate.Insertion, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new FilterCompletionItem(
                candidate.Insertion,
                candidate.Insertion,
                FilterCompletionKind.Field,
                replacement,
                candidate.Field.Description,
                $"{candidate.Field.ValueKind} · {string.Join(" ", candidate.Field.Operators.Select(value => value.Display()))}"));

        var syntax = new[]
        {
            new FilterCompletionItem("unknown(…)", "unknown(", FilterCompletionKind.Function, replacement, "Match records where a field has no usable evidence."),
            new FilterCompletionItem("known(…)", "known(", FilterCompletionKind.Function, replacement, "Match records where a field has usable evidence."),
            new FilterCompletionItem("NOT", "NOT ", FilterCompletionKind.Keyword, replacement, "Negate the next expression."),
        }.Where(item => Matches(item.Label, prefix));

        return fields.Concat(syntax).ToArray();
    }

    private static TextSpan FindReplacementSpan(string expression, int caret)
    {
        var start = caret;
        while (start > 0 && IsValueCharacter(expression[start - 1]))
            start--;
        var end = caret;
        while (end < expression.Length && IsValueCharacter(expression[end]))
            end++;
        return TextSpan.FromBounds(start, end);
    }

    private static bool IsFieldCharacter(char value) => char.IsLetterOrDigit(value) || value is '.' or '_';
    private static bool IsValueCharacter(char value) => !char.IsWhiteSpace(value) && value is not '(' and not ')' and not ':' and not '=' and not '!' and not '<' and not '>' and not '|';
    private static IEnumerable<string> FieldNames(FilterField field) => new[] { field.Key, field.Key.Split('.').Last() }.Concat(field.Aliases);
    private static IEnumerable<string> CandidateNames(FilterValueReference value) => new[] { value.DisplayName }.Concat(value.Aliases);
    private static string PreferredFieldName(FilterField field) => field.Aliases.OrderBy(alias => alias.Length).FirstOrDefault() ?? field.Key;
    private static bool Matches(string candidate, string prefix) => string.IsNullOrEmpty(prefix) || candidate.Contains(prefix, StringComparison.OrdinalIgnoreCase);
    private static int Rank(string candidate, string prefix) => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    private static string QuoteWhenNeeded(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
}
