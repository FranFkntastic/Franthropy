using Franthropy.Filtering.Semantics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Franthropy.Filtering.Documentation;

public sealed record FilterReferenceModel(
    string CatalogVersion,
    string ContextId,
    string ContextSchemaVersion,
    IReadOnlyList<FilterFieldReference> Fields)
{
    private IReadOnlyList<FilterPredicateAlias> predicates = [];
    private IReadOnlyList<FilterPredicateReference> predicateReferences = [];

    [JsonIgnore]
    public IReadOnlyList<FilterPredicateAlias> Predicates
    {
        get => predicates.Count > 0
            ? predicates
            : predicateReferences.Select(ToAlias).ToArray();
        init => predicates = value ?? [];
    }

    [JsonPropertyName("predicates")]
    public IReadOnlyList<FilterPredicateReference> PredicateReferences
    {
        get => predicateReferences.Count > 0
            ? predicateReferences
            : predicates.Select(ToReference).ToArray();
        init => predicateReferences = (value ?? []).Select(NormalizeReference).ToArray();
    }

    private static FilterPredicateReference NormalizeReference(FilterPredicateReference predicate) =>
        string.IsNullOrEmpty(predicate.Operator)
            ? predicate with { Operator = FilterComparisonOperator.ExactEquals.Display() }
            : predicate;

    private static FilterPredicateAlias ToAlias(FilterPredicateReference predicate) => new(
        predicate.Qualifier,
        predicate.Specifier,
        predicate.TargetFieldKey,
        predicate.TargetValue,
        predicate.Description,
        ParseOperator(predicate.Operator));

    private static FilterPredicateReference ToReference(FilterPredicateAlias predicate) => new(
        predicate.Qualifier,
        predicate.Specifier,
        predicate.TargetFieldKey,
        predicate.Operator.Display(),
        predicate.TargetValue,
        predicate.Description);

    private static FilterComparisonOperator ParseOperator(string? value) => value switch
    {
        null or "" => FilterComparisonOperator.ExactEquals,
        ":" => FilterComparisonOperator.Match,
        "=" => FilterComparisonOperator.Equals,
        "!=" => FilterComparisonOperator.NotEquals,
        "==" => FilterComparisonOperator.ExactEquals,
        "!==" => FilterComparisonOperator.ExactNotEquals,
        "<" => FilterComparisonOperator.Less,
        "<=" => FilterComparisonOperator.LessOrEqual,
        ">" => FilterComparisonOperator.Greater,
        ">=" => FilterComparisonOperator.GreaterOrEqual,
        _ => throw new JsonException($"Unknown filter comparison operator '{value}'."),
    };
}

public sealed record FilterPredicateReference(
    string Qualifier,
    string Specifier,
    string TargetFieldKey,
    string Operator,
    string TargetValue,
    string Description);

public sealed record FilterFieldReference(
    string Key,
    string DisplayName,
    string Description,
    FilterValueKind ValueKind,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Operators,
    bool IsAvailable,
    bool IsDefaultText,
    IReadOnlyList<FilterValueReference> Values)
{
    public string PreferredName { get; init; } = Key;
}

public static class FilterReferenceGenerator
{
    public static FilterReferenceModel Create(FilterCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new FilterReferenceModel(
            catalog.Version,
            "catalog",
            catalog.Version,
            catalog.Fields.Select(field => CreateField(
                field,
                true,
                false,
                catalog.GetPreferredName(field))).ToArray())
        {
            Predicates = catalog.PredicateAliases,
            PredicateReferences = CreatePredicates(catalog.PredicateAliases),
        };
    }

    public static FilterReferenceModel Create<TRecord>(FilterContext<TRecord> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new FilterReferenceModel(
            context.Catalog.Version,
            context.ContextId,
            context.SchemaVersion,
            context.Catalog.Fields.Select(field => CreateField(
                field,
                context.AvailableKeys.Contains(field.Key),
                context.DefaultTextBindings.Any(binding => binding.Field == field),
                context.Catalog.GetPreferredName(field, context.AvailableKeys))).ToArray())
        {
            Predicates = context.Catalog.PredicateAliases.Where(predicate => context.AvailableKeys.Contains(predicate.TargetFieldKey)).ToArray(),
            PredicateReferences = CreatePredicates(context.Catalog.PredicateAliases.Where(predicate => context.AvailableKeys.Contains(predicate.TargetFieldKey))),
        };
    }

    private static IReadOnlyList<FilterPredicateReference> CreatePredicates(IEnumerable<FilterPredicateAlias> predicates) => predicates
        .Select(predicate => new FilterPredicateReference(
            predicate.Qualifier,
            predicate.Specifier,
            predicate.TargetFieldKey,
            predicate.Operator.Display(),
            predicate.TargetValue,
            predicate.Description))
        .ToArray();

    private static FilterFieldReference CreateField(
        FilterField field,
        bool isAvailable,
        bool isDefaultText,
        string preferredName) => new(
            field.Key,
            field.DisplayName,
            field.Description,
            field.ValueKind,
            field.Aliases,
            field.Operators.Select(value => value.Display()).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            isAvailable,
            isDefaultText,
            field.Values)
        { PreferredName = preferredName };
}

public static class FilterReferenceWriter
{
    public static string ToJson(FilterReferenceModel reference, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return JsonSerializer.Serialize(reference, new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }

    public static string ToMarkdown(FilterReferenceModel reference, string title = "Filter reference")
    {
        ArgumentNullException.ThrowIfNull(reference);
        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(title).AppendLine();
        builder.Append("Catalog version: `").Append(reference.CatalogVersion).AppendLine("`").AppendLine();
        builder.Append("Context: `").Append(reference.ContextId).Append("` (schema `")
            .Append(reference.ContextSchemaVersion).AppendLine("`)").AppendLine();
        if (reference.PredicateReferences.Count > 0)
        {
            builder.AppendLine("## Predicates").AppendLine();
            foreach (var predicate in reference.PredicateReferences)
                builder.Append("- `").Append(predicate.Qualifier).Append(':').Append(predicate.Specifier)
                    .Append("` -> `").Append(predicate.TargetFieldKey).Append(predicate.Operator).Append(predicate.TargetValue)
                    .Append("`: ").AppendLine(predicate.Description);
            builder.AppendLine();
        }
        foreach (var field in reference.Fields)
        {
            builder.Append("## `").Append(field.Key).AppendLine("`").AppendLine();
            builder.AppendLine(field.Description).AppendLine();
            builder.Append("- Type: `").Append(field.ValueKind).AppendLine("`");
            builder.Append("- Available: ").AppendLine(field.IsAvailable ? "yes" : "no");
            builder.Append("- Operators: ").AppendLine(string.Join(", ", field.Operators.Select(value => $"`{value}`")));
            if (field.Aliases.Count > 0)
                builder.Append("- Aliases: ").AppendLine(string.Join(", ", field.Aliases.Select(value => $"`{value}`")));
            if (field.Values.Count > 0)
            {
                builder.Append("- Values: ").AppendLine(string.Join(", ", field.Values.Select(value =>
                    value.Aliases.Count == 0
                        ? $"`{value.DisplayName}`"
                        : $"`{value.DisplayName}` ({string.Join(", ", value.Aliases.Select(alias => $"`{alias}`"))})")));
            }
            builder.AppendLine();
        }
        return builder.ToString();
    }
}
