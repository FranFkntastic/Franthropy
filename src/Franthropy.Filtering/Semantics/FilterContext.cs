using Franthropy.Filtering.Evaluation;
using Franthropy.Filtering.Diagnostics;
using Franthropy.Filtering.Syntax;

namespace Franthropy.Filtering.Semantics;

public sealed class FilterContext<TRecord>
{
    internal FilterContext(
        FilterCatalog catalog,
        IReadOnlyDictionary<string, FilterFieldBinding<TRecord>> bindings,
        IReadOnlyList<DefaultTextBinding<TRecord>> defaultTextBindings,
        string contextId,
        string schemaVersion)
    {
        Catalog = catalog;
        Bindings = bindings;
        DefaultTextBindings = defaultTextBindings;
        ContextId = contextId;
        SchemaVersion = schemaVersion;
        AvailableKeys = new HashSet<string>(bindings.Keys, StringComparer.OrdinalIgnoreCase);
    }

    public FilterCatalog Catalog { get; }
    public string ContextId { get; }
    public string SchemaVersion { get; }
    public IReadOnlySet<string> AvailableKeys { get; }
    internal IReadOnlyDictionary<string, FilterFieldBinding<TRecord>> Bindings { get; }
    internal IReadOnlyList<DefaultTextBinding<TRecord>> DefaultTextBindings { get; }
}

public sealed class FilterContextBuilder<TRecord>
{
    private readonly FilterCatalog catalog;
    private readonly Dictionary<string, FilterFieldBinding<TRecord>> bindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DefaultTextBinding<TRecord>> defaultTextBindings = [];

    public FilterContextBuilder(FilterCatalog catalog)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public FilterContextBuilder<TRecord> Bind<TValue>(
        FilterField<TValue> field,
        Func<TRecord, FieldEvidence<TValue>> accessor)
    {
        ValidateField(field);
        ArgumentNullException.ThrowIfNull(accessor);
        if (!bindings.TryAdd(field.Key, new ScalarFieldBinding<TRecord, TValue>(field, accessor)))
        {
            throw new InvalidOperationException($"Field '{field.Key}' is already bound in this context.");
        }
        return this;
    }

    public FilterContextBuilder<TRecord> BindSet<TValue>(
        FilterSetField<TValue> field,
        Func<TRecord, FieldEvidence<IReadOnlyCollection<TValue>>> accessor)
    {
        ValidateField(field);
        ArgumentNullException.ThrowIfNull(accessor);
        if (!bindings.TryAdd(field.Key, new SetFieldBinding<TRecord, TValue>(field, accessor)))
        {
            throw new InvalidOperationException($"Field '{field.Key}' is already bound in this context.");
        }
        return this;
    }

    public FilterContextBuilder<TRecord> UseDefaultText(FilterField<string> field)
    {
        ValidateField(field);
        if (!bindings.ContainsKey(field.Key))
            throw new InvalidOperationException($"Default text field '{field.Key}' must be bound first.");
        if (defaultTextBindings.All(binding => binding.Field != field))
        {
            var accessor = bindings[field.Key];
            defaultTextBindings.Add(new DefaultTextBinding<TRecord>(field, record =>
            {
                var evidence = accessor.ReadString(record);
                return evidence;
            }));
        }
        return this;
    }

    public FilterContextBuilder<TRecord> UseDefaultText(
        FilterField field,
        Func<TRecord, FieldEvidence<string>> searchTextAccessor)
    {
        ValidateField(field);
        ArgumentNullException.ThrowIfNull(searchTextAccessor);
        if (!bindings.ContainsKey(field.Key))
            throw new InvalidOperationException($"Default text field '{field.Key}' must be bound first.");
        if (defaultTextBindings.All(binding => binding.Field != field))
            defaultTextBindings.Add(new DefaultTextBinding<TRecord>(field, searchTextAccessor));
        return this;
    }

    public FilterContext<TRecord> Build(string contextId = "default", string schemaVersion = "1") =>
        new(catalog, new Dictionary<string, FilterFieldBinding<TRecord>>(bindings, StringComparer.OrdinalIgnoreCase),
            defaultTextBindings.ToArray(),
            string.IsNullOrWhiteSpace(contextId) ? "default" : contextId.Trim(),
            string.IsNullOrWhiteSpace(schemaVersion) ? "1" : schemaVersion.Trim());

    private void ValidateField(FilterField field)
    {
        if (!catalog.Fields.Contains(field))
            throw new ArgumentException($"Field '{field.Key}' is not part of this catalog.", nameof(field));
    }
}

internal sealed record DefaultTextBinding<TRecord>(
    FilterField Field,
    Func<TRecord, FieldEvidence<string>> Accessor);

internal abstract class FilterFieldBinding<TRecord>
{
    public abstract Func<TRecord, FilterTruth>? Bind(
        FilterComparisonOperator comparison,
        FilterValueSyntax value,
        DiagnosticBag diagnostics);

    public abstract bool IsKnown(TRecord record);

    public virtual FieldEvidence<string> ReadString(TRecord record) =>
        FieldEvidence<string>.Unknown("The search text was not observed.");
}

internal sealed class ScalarFieldBinding<TRecord, TValue>(
    FilterField<TValue> field,
    Func<TRecord, FieldEvidence<TValue>> accessor) : FilterFieldBinding<TRecord>
{
    public override Func<TRecord, FilterTruth>? Bind(
        FilterComparisonOperator comparison,
        FilterValueSyntax value,
        DiagnosticBag diagnostics)
    {
        var test = field.BindTyped(comparison, value, diagnostics);
        return test is null ? null : record => test(accessor(record));
    }

    public override bool IsKnown(TRecord record) => accessor(record).IsKnown;

    public override FieldEvidence<string> ReadString(TRecord record)
    {
        if (typeof(TValue) != typeof(string))
            return base.ReadString(record);
        var evidence = accessor(record);
        if (!evidence.IsKnown)
            return FieldEvidence<string>.Unknown(evidence.UnknownReason ?? "The search text was not observed.");
        return FieldEvidence<string>.Known((string)(object)evidence.Value!);
    }
}

internal sealed class SetFieldBinding<TRecord, TValue>(
    FilterSetField<TValue> field,
    Func<TRecord, FieldEvidence<IReadOnlyCollection<TValue>>> accessor) : FilterFieldBinding<TRecord>
{
    public override Func<TRecord, FilterTruth>? Bind(
        FilterComparisonOperator comparison,
        FilterValueSyntax value,
        DiagnosticBag diagnostics)
    {
        var test = field.BindTyped(comparison, value, diagnostics);
        return test is null ? null : record => test(accessor(record));
    }

    public override bool IsKnown(TRecord record) => accessor(record).IsKnown;
}
