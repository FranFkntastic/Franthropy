using Franthropy.Filtering.Compilation;
using Franthropy.Filtering.Semantics;
using Franthropy.Filtering.Syntax;

namespace Franthropy.Filtering.Completion;

public static class FilterCompletionService
{
    private static readonly char[] ComparatorCharacters = [':', '=', '!', '<', '>'];
    private static readonly char[] ComparisonOperatorCharacters = ['=', '!', '<', '>'];
    private static readonly FilterComparisonOperator[] OperatorOrder =
    [
        FilterComparisonOperator.Match,
        FilterComparisonOperator.Equals,
        FilterComparisonOperator.NotEquals,
        FilterComparisonOperator.ExactEquals,
        FilterComparisonOperator.ExactNotEquals,
        FilterComparisonOperator.Less,
        FilterComparisonOperator.LessOrEqual,
        FilterComparisonOperator.Greater,
        FilterComparisonOperator.GreaterOrEqual,
    ];

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
        var fieldTarget = FindFieldCompletionTarget(expression, replacement, caret);
        var items = CompletePostColonOperators(context, expression, caret)
                    ?? CompleteOperators(context, expression, caret)
                    ?? CompleteValues(context, expression, replacement, prefix, maximumItems)
                    ?? CompleteFunctionField(context, expression, fieldTarget.Replacement, fieldTarget.Prefix)
                    ?? CompleteFields(context, fieldTarget.Replacement, fieldTarget.Prefix);
        var diagnostics = FilterCompiler.Compile(expression, context).Diagnostics;

        return new FilterCompletionResult(
            context.Catalog.Version,
            context.ContextId,
            context.SchemaVersion,
            items.Take(maximumItems).ToArray(),
            diagnostics);
    }

    private static IReadOnlyList<FilterCompletionItem>? CompletePostColonOperators<TRecord>(
        FilterContext<TRecord> context,
        string expression,
        int caret)
    {
        var operatorStart = caret;
        while (operatorStart > 0 && ComparisonOperatorCharacters.Contains(expression[operatorStart - 1]))
            operatorStart--;
        if (operatorStart == 0 || expression[operatorStart - 1] != ':')
            return null;

        var fieldEnd = operatorStart - 1;
        var fieldStart = fieldEnd;
        while (fieldStart > 0 && IsFieldCharacter(expression[fieldStart - 1]))
            fieldStart--;
        if (fieldStart == fieldEnd)
            return null;

        var resolution = context.Catalog.Resolve(expression[fieldStart..fieldEnd], context.AvailableKeys);
        if (resolution.Kind != FilterFieldResolutionKind.Success || resolution.Field is null ||
            !context.AvailableKeys.Contains(resolution.Field.Key) ||
            !resolution.Field.Operators.Any(IsOrderedOperator))
            return null;

        var operatorPrefix = expression[operatorStart..caret];
        var candidates = OperatorOrder
            .Where(value => value != FilterComparisonOperator.Match)
            .Where(resolution.Field.Operators.Contains)
            .Select(value => (Value: value, Display: value.Display()))
            .Where(candidate => candidate.Display.StartsWith(operatorPrefix, StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length == 0)
            return operatorPrefix.Length == 0 ? null : [];

        var exact = candidates.Any(candidate => candidate.Display.Equals(operatorPrefix, StringComparison.Ordinal));
        var hasLonger = candidates.Any(candidate => candidate.Display.Length > operatorPrefix.Length);
        if (operatorPrefix.Length > 0 && exact && !hasLonger)
            return null;

        var replacement = TextSpan.FromBounds(operatorStart, caret);
        return candidates.Select(candidate => new FilterCompletionItem(
                candidate.Display,
                candidate.Display,
                FilterCompletionKind.Operator,
                replacement,
                DescribeOperator(candidate.Value),
                $"{resolution.Field.DisplayName} · {resolution.Field.ValueKind}"))
            .ToArray();
    }

    private static IReadOnlyList<FilterCompletionItem>? CompleteOperators<TRecord>(
        FilterContext<TRecord> context,
        string expression,
        int caret)
    {
        var operatorStart = caret;
        while (operatorStart > 0 && ComparatorCharacters.Contains(expression[operatorStart - 1]))
            operatorStart--;

        var fieldEnd = operatorStart;
        while (fieldEnd > 0 && char.IsWhiteSpace(expression[fieldEnd - 1]))
            fieldEnd--;
        var fieldStart = fieldEnd;
        while (fieldStart > 0 && IsFieldCharacter(expression[fieldStart - 1]))
            fieldStart--;
        if (fieldStart == fieldEnd || IsEvidenceFunctionArgument(expression, fieldStart) ||
            IsInsideListValue(expression, fieldStart))
            return null;

        var resolution = context.Catalog.Resolve(expression[fieldStart..fieldEnd], context.AvailableKeys);
        if (resolution.Kind != FilterFieldResolutionKind.Success || resolution.Field is null ||
            !context.AvailableKeys.Contains(resolution.Field.Key))
            return null;

        var operatorPrefix = expression[operatorStart..caret];
        var candidates = OperatorOrder
            .Where(resolution.Field.Operators.Contains)
            .Select(value => (Value: value, Display: value.Display()))
            .Where(candidate => candidate.Display.StartsWith(operatorPrefix, StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length == 0)
            return operatorPrefix.Length == 0 ? null : [];

        var exact = candidates.Any(candidate => candidate.Display.Equals(operatorPrefix, StringComparison.Ordinal));
        var hasLonger = candidates.Any(candidate => candidate.Display.Length > operatorPrefix.Length);
        if (operatorPrefix.Length > 0 && exact && !hasLonger)
            return null;

        var replacement = TextSpan.FromBounds(operatorStart, caret);
        return candidates.Select(candidate => new FilterCompletionItem(
                candidate.Display,
                candidate.Display,
                FilterCompletionKind.Operator,
                replacement,
                DescribeOperator(candidate.Value),
                $"{resolution.Field.DisplayName} · {resolution.Field.ValueKind}"))
            .ToArray();
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
        var valueContext = before[comparatorEnd..];
        if (valueContext.Any(character => !char.IsWhiteSpace(character)) &&
            valueContext.Count(character => character == '(') <= valueContext.Count(character => character == ')'))
            return null;

        var fieldEnd = comparatorStart;
        if (fieldEnd > 0 && before[fieldEnd - 1] == ':' && before[comparatorStart] != ':')
            fieldEnd--;
        while (fieldEnd > 0 && char.IsWhiteSpace(before[fieldEnd - 1]))
            fieldEnd--;
        var fieldStart = fieldEnd;
        while (fieldStart > 0 && IsFieldCharacter(before[fieldStart - 1]))
            fieldStart--;
        if (fieldStart == fieldEnd)
            return null;

        var fieldText = before[fieldStart..fieldEnd];
        var predicates = context.Catalog.PredicateAliases
            .Where(candidate => candidate.Qualifier.Equals(fieldText, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => context.AvailableKeys.Contains(candidate.TargetFieldKey))
            .Where(candidate => Matches(candidate.Specifier, prefix))
            .OrderBy(candidate => Rank(candidate.Specifier, prefix))
            .ThenBy(candidate => candidate.Specifier, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new FilterCompletionItem(
                candidate.Specifier,
                candidate.Specifier,
                FilterCompletionKind.Value,
                replacement,
                candidate.Description,
                $"{candidate.Qualifier}: predicate"))
            .Take(maximumItems)
            .ToArray();
        if (predicates.Length > 0 || context.Catalog.PredicateAliases.Any(candidate =>
                candidate.Qualifier.Equals(fieldText, StringComparison.OrdinalIgnoreCase) &&
                context.AvailableKeys.Contains(candidate.TargetFieldKey)))
            return predicates;

        var resolution = context.Catalog.Resolve(fieldText, context.AvailableKeys);
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
            .Select(field => (Field: field, Insertion: context.Catalog.GetPreferredName(field, context.AvailableKeys)))
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

        var predicates = context.Catalog.PredicateAliases
            .Where(candidate => context.AvailableKeys.Contains(candidate.TargetFieldKey))
            .Select(candidate => candidate.Qualifier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(qualifier => Matches(qualifier, prefix))
            .Select(qualifier => new FilterCompletionItem(
                qualifier,
                qualifier,
                FilterCompletionKind.Field,
                replacement,
                "Human-readable state predicate.",
                "predicate namespace"));

        var syntax = new[]
        {
            new FilterCompletionItem("unknown(…)", "unknown(", FilterCompletionKind.Function, replacement, "Match records where a field has no usable evidence."),
            new FilterCompletionItem("known(…)", "known(", FilterCompletionKind.Function, replacement, "Match records where a field has usable evidence."),
            new FilterCompletionItem("NOT", "NOT ", FilterCompletionKind.Keyword, replacement, "Negate the next expression."),
        }.Where(item => Matches(item.Label, prefix));

        return predicates.Concat(fields).Concat(syntax).ToArray();
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

    private static (TextSpan Replacement, string Prefix) FindFieldCompletionTarget(
        string expression,
        TextSpan replacement,
        int caret)
    {
        var start = replacement.Start;
        if (start < caret && expression[start] == '-' &&
            (start == 0 || char.IsWhiteSpace(expression[start - 1]) || expression[start - 1] == '('))
        {
            start++;
        }

        return (
            TextSpan.FromBounds(start, replacement.End),
            expression[start..caret].Trim('"'));
    }

    private static bool IsFieldCharacter(char value) => char.IsLetterOrDigit(value) || value is '.' or '_';
    private static bool IsValueCharacter(char value) => !char.IsWhiteSpace(value) && value is not '(' and not ')' and not ':' and not '=' and not '!' and not '<' and not '>' and not '|';
    private static IEnumerable<string> FieldNames(FilterField field) => new[] { field.Key, field.Key.Split('.').Last() }.Concat(field.Aliases);
    private static IEnumerable<string> CandidateNames(FilterValueReference value) => new[] { value.DisplayName }.Concat(value.Aliases);
    private static bool IsOrderedOperator(FilterComparisonOperator value) => value is
        FilterComparisonOperator.Less or
        FilterComparisonOperator.LessOrEqual or
        FilterComparisonOperator.Greater or
        FilterComparisonOperator.GreaterOrEqual;
    private static bool Matches(string candidate, string prefix) => string.IsNullOrEmpty(prefix) || candidate.Contains(prefix, StringComparison.OrdinalIgnoreCase);
    private static int Rank(string candidate, string prefix) => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    private static string QuoteWhenNeeded(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;

    private static bool IsEvidenceFunctionArgument(string expression, int fieldStart)
    {
        if (fieldStart == 0 || expression[fieldStart - 1] != '(')
            return false;

        var functionEnd = fieldStart - 1;
        var functionStart = functionEnd;
        while (functionStart > 0 && char.IsLetter(expression[functionStart - 1]))
            functionStart--;
        var function = expression[functionStart..functionEnd];
        return function.Equals("known", StringComparison.OrdinalIgnoreCase) ||
               function.Equals("unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInsideListValue(string expression, int fieldStart)
    {
        var beforeField = expression[..fieldStart];
        var comparator = beforeField.LastIndexOfAny(ComparatorCharacters);
        if (comparator < 0)
            return false;

        var valueContext = beforeField[(comparator + 1)..];
        return valueContext.Count(character => character == '(') > valueContext.Count(character => character == ')');
    }

    private static string DescribeOperator(FilterComparisonOperator value) => value switch
    {
        FilterComparisonOperator.Match => "Match a value using the field's normal search semantics.",
        FilterComparisonOperator.Equals => "Fuzzy-match text or a uniquely resolved named value.",
        FilterComparisonOperator.NotEquals => "Exclude a fuzzy text or uniquely resolved named-value match.",
        FilterComparisonOperator.ExactEquals => "Match the complete normalized value.",
        FilterComparisonOperator.ExactNotEquals => "Exclude the complete normalized value.",
        FilterComparisonOperator.Less => "Match values below the supplied value.",
        FilterComparisonOperator.LessOrEqual => "Match values at or below the supplied value.",
        FilterComparisonOperator.Greater => "Match values above the supplied value.",
        FilterComparisonOperator.GreaterOrEqual => "Match values at or above the supplied value.",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
