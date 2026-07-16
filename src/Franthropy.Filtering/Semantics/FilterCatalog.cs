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

public sealed class FilterCatalog
{
    private readonly IReadOnlyDictionary<string, FilterField> exact;
    private readonly IReadOnlyDictionary<string, FilterField> aliases;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<FilterField>> leaves;

    public FilterCatalog(IEnumerable<FilterField> fields, string version = "1")
    {
        Fields = fields?.ToArray() ?? throw new ArgumentNullException(nameof(fields));
        Version = string.IsNullOrWhiteSpace(version) ? "1" : version.Trim();

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
    }

    public string Version { get; }
    public IReadOnlyList<FilterField> Fields { get; }

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
}
