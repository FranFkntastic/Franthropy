namespace Franthropy.Filtering.Semantics;

public enum FilterFieldResolutionKind
{
    Success,
    NotFound,
    Ambiguous,
}

public sealed record FilterFieldResolution(
    FilterFieldResolutionKind Kind,
    FilterField? Field,
    IReadOnlyList<FilterField> Candidates)
{
    public static FilterFieldResolution Success(FilterField field) =>
        new(FilterFieldResolutionKind.Success, field, [field]);
}

public sealed record FilterPredicateAlias(
    string Qualifier,
    string Specifier,
    string TargetFieldKey,
    string TargetValue,
    string Description = "");

public sealed class FilterCatalog
{
    private readonly IReadOnlyDictionary<string, FilterField> exact;
    private readonly IReadOnlyDictionary<string, FilterField> aliases;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<FilterField>> leaves;

    public FilterCatalog(
        IEnumerable<FilterField> fields,
        string version = "1",
        IEnumerable<FilterPredicateAlias>? predicateAliases = null)
    {
        Fields = fields?.ToArray() ?? throw new ArgumentNullException(nameof(fields));
        Version = string.IsNullOrWhiteSpace(version) ? "1" : version.Trim();
        PredicateAliases = predicateAliases?.ToArray() ?? [];

        var exactBuilder = new Dictionary<string, FilterField>(StringComparer.OrdinalIgnoreCase);
        var aliasBuilder = new Dictionary<string, FilterField>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            if (!exactBuilder.TryAdd(field.Key, field))
                throw new ArgumentException($"Field '{field.Key}' is registered more than once.", nameof(fields));

            foreach (var alias in field.Aliases)
            {
                if (exactBuilder.ContainsKey(alias) || !aliasBuilder.TryAdd(alias, field))
                    throw new ArgumentException($"Field alias '{alias}' collides with another field or alias.", nameof(fields));
            }
        }

        foreach (var key in exactBuilder.Keys)
        {
            if (aliasBuilder.ContainsKey(key))
                throw new ArgumentException($"Field key '{key}' collides with an alias.", nameof(fields));
        }

        exact = exactBuilder;
        aliases = aliasBuilder;
        leaves = Fields
            .GroupBy(field => field.Key.Split('.').Last(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<FilterField>)group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var predicate in PredicateAliases)
        {
            if (exact.ContainsKey(predicate.Qualifier) || aliases.ContainsKey(predicate.Qualifier) || leaves.ContainsKey(predicate.Qualifier))
                throw new ArgumentException($"Predicate qualifier '{predicate.Qualifier}' collides with a field name.", nameof(predicateAliases));
            if (!exact.ContainsKey(predicate.TargetFieldKey))
                throw new ArgumentException($"Predicate '{predicate.Qualifier}:{predicate.Specifier}' targets unknown field '{predicate.TargetFieldKey}'.", nameof(predicateAliases));
            if (PredicateAliases.Count(candidate =>
                    candidate.Qualifier.Equals(predicate.Qualifier, StringComparison.OrdinalIgnoreCase) &&
                    candidate.Specifier.Equals(predicate.Specifier, StringComparison.OrdinalIgnoreCase)) > 1)
                throw new ArgumentException($"Predicate '{predicate.Qualifier}:{predicate.Specifier}' is registered more than once.", nameof(predicateAliases));
        }
    }

    public string Version { get; }
    public IReadOnlyList<FilterField> Fields { get; }
    public IReadOnlyList<FilterPredicateAlias> PredicateAliases { get; }

    public FilterPredicateAlias? ResolvePredicate(string qualifier, string specifier) => PredicateAliases.SingleOrDefault(candidate =>
        candidate.Qualifier.Equals(qualifier.Trim(), StringComparison.OrdinalIgnoreCase) &&
        candidate.Specifier.Equals(specifier.Trim(), StringComparison.OrdinalIgnoreCase));

    public FilterFieldResolution Resolve(string text, IReadOnlySet<string>? availableKeys = null)
    {
        var normalized = text.Trim();
        if (exact.TryGetValue(normalized, out var exactField))
            return FilterFieldResolution.Success(exactField);
        if (aliases.TryGetValue(normalized, out var aliasField))
            return FilterFieldResolution.Success(aliasField);
        if (!leaves.TryGetValue(normalized, out var candidates))
            return new FilterFieldResolution(FilterFieldResolutionKind.NotFound, null, []);
        if (candidates.Count == 1)
            return FilterFieldResolution.Success(candidates[0]);

        if (availableKeys is not null)
        {
            var available = candidates.Where(field => availableKeys.Contains(field.Key)).ToArray();
            if (available.Length == 1)
                return FilterFieldResolution.Success(available[0]);
        }

        return new FilterFieldResolution(FilterFieldResolutionKind.Ambiguous, null, candidates);
    }

    public string GetPreferredName(FilterField field, IReadOnlySet<string>? availableKeys = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (!exact.TryGetValue(field.Key, out var registered) || !ReferenceEquals(registered, field))
            throw new ArgumentException($"Field '{field.Key}' does not belong to this catalog.", nameof(field));

        var alias = field.Aliases.OrderBy(value => value.Length).FirstOrDefault();
        if (alias is not null)
            return alias;

        var leaf = field.Key.Split('.').Last();
        var resolution = Resolve(leaf, availableKeys);
        return resolution.Kind == FilterFieldResolutionKind.Success && ReferenceEquals(resolution.Field, field)
            ? leaf
            : field.Key;
    }
}
