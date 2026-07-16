using Franthropy.Filtering.Semantics;

namespace Franthropy.Filtering.Documentation;

public sealed record FilterReferenceModel(
    string CatalogVersion,
    string ContextId,
    string ContextSchemaVersion,
    IReadOnlyList<FilterFieldReference> Fields);

public sealed record FilterFieldReference(
    string Key,
    string DisplayName,
    string Description,
    FilterValueKind ValueKind,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Operators,
    bool IsAvailable,
    IReadOnlyList<FilterValueReference> Values);

public static class FilterReferenceGenerator
{
    public static FilterReferenceModel Create<TRecord>(FilterContext<TRecord> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new FilterReferenceModel(
            context.Catalog.Version,
            context.ContextId,
            context.SchemaVersion,
            context.Catalog.Fields.Select(field => new FilterFieldReference(
                field.Key,
                field.DisplayName,
                field.Description,
                field.ValueKind,
                field.Aliases,
                field.Operators.Select(value => value.Display()).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                context.AvailableKeys.Contains(field.Key),
                field.Values)).ToArray());
    }
}
