using Franthropy.Filtering.Evaluation;

namespace Franthropy.Filtering.Semantics;

public sealed class FilterContext<TRecord>
{
    internal FilterContext(
        FilterCatalog catalog,
        IReadOnlyDictionary<string, Func<TRecord, UntypedFieldEvidence>> bindings,
        IReadOnlyList<FilterField> defaultTextFields,
        string contextId,
        string schemaVersion)
    {
        Catalog = catalog;
        Bindings = bindings;
        DefaultTextFields = defaultTextFields;
        ContextId = contextId;
        SchemaVersion = schemaVersion;
        AvailableKeys = new HashSet<string>(bindings.Keys, StringComparer.OrdinalIgnoreCase);
    }

    public FilterCatalog Catalog { get; }
    public string ContextId { get; }
    public string SchemaVersion { get; }
    public IReadOnlySet<string> AvailableKeys { get; }
    internal IReadOnlyDictionary<string, Func<TRecord, UntypedFieldEvidence>> Bindings { get; }
    internal IReadOnlyList<FilterField> DefaultTextFields { get; }
}

public sealed class FilterContextBuilder<TRecord>
{
    private readonly FilterCatalog catalog;
    private readonly Dictionary<string, Func<TRecord, UntypedFieldEvidence>> bindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FilterField> defaultTextFields = [];

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
        if (!bindings.TryAdd(field.Key, record =>
            {
                var evidence = accessor(record);
                return evidence.IsKnown
                    ? new UntypedFieldEvidence(true, evidence.Value, null)
                    : new UntypedFieldEvidence(false, null, evidence.UnknownReason);
            }))
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
        if (!bindings.TryAdd(field.Key, record =>
            {
                var evidence = accessor(record);
                return evidence.IsKnown
                    ? new UntypedFieldEvidence(true, evidence.Value, null)
                    : new UntypedFieldEvidence(false, null, evidence.UnknownReason);
            }))
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
        if (!defaultTextFields.Contains(field))
            defaultTextFields.Add(field);
        return this;
    }

    public FilterContext<TRecord> Build(string contextId = "default", string schemaVersion = "1") =>
        new(catalog, new Dictionary<string, Func<TRecord, UntypedFieldEvidence>>(bindings, StringComparer.OrdinalIgnoreCase),
            defaultTextFields.ToArray(),
            string.IsNullOrWhiteSpace(contextId) ? "default" : contextId.Trim(),
            string.IsNullOrWhiteSpace(schemaVersion) ? "1" : schemaVersion.Trim());

    private void ValidateField(FilterField field)
    {
        if (!catalog.Fields.Contains(field))
            throw new ArgumentException($"Field '{field.Key}' is not part of this catalog.", nameof(field));
    }
}
