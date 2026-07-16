using System.Globalization;

namespace Franthropy.Filtering.Semantics;

public enum FilterLiteralResolutionKind
{
    Success,
    NotFound,
    Ambiguous,
}

public sealed record FilterLiteralCandidate<T>(T Value, string DisplayName, IReadOnlyList<string>? Aliases = null);

public sealed record FilterLiteralResolution<T>(
    FilterLiteralResolutionKind Kind,
    T? Value,
    string? Message = null,
    IReadOnlyList<FilterLiteralCandidate<T>>? Candidates = null)
{
    public static FilterLiteralResolution<T> Success(T value) =>
        new(FilterLiteralResolutionKind.Success, value);

    public static FilterLiteralResolution<T> NotFound(string message) =>
        new(FilterLiteralResolutionKind.NotFound, default, message);

    public static FilterLiteralResolution<T> Ambiguous(
        string message,
        IReadOnlyList<FilterLiteralCandidate<T>> candidates) =>
        new(FilterLiteralResolutionKind.Ambiguous, default, message, candidates);
}

public interface IFilterLiteralCodec<T>
{
    string TypeName { get; }
    FilterLiteralResolution<T> Resolve(string text);
    IReadOnlyList<FilterLiteralCandidate<T>> Values { get; }
}

public interface IFilterNamedValueResolver<T>
{
    IReadOnlyList<FilterLiteralCandidate<T>> Values { get; }
    IReadOnlyList<FilterLiteralCandidate<T>> Resolve(string text);
}

public sealed class FilterNamedValueCatalog<T> : IFilterNamedValueResolver<T>
{
    private readonly IReadOnlyList<FilterLiteralCandidate<T>> values;

    public FilterNamedValueCatalog(IEnumerable<FilterLiteralCandidate<T>> values)
    {
        this.values = values?.ToArray() ?? throw new ArgumentNullException(nameof(values));
    }

    public IReadOnlyList<FilterLiteralCandidate<T>> Values => values;

    public IReadOnlyList<FilterLiteralCandidate<T>> Resolve(string text)
    {
        var normalized = text.Trim();
        return values
            .Where(candidate =>
                candidate.DisplayName.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                (candidate.Aliases?.Any(alias => alias.Equals(normalized, StringComparison.OrdinalIgnoreCase)) ?? false))
            .ToArray();
    }
}

internal sealed class TextLiteralCodec : IFilterLiteralCodec<string>
{
    public string TypeName => "text";
    public IReadOnlyList<FilterLiteralCandidate<string>> Values => [];
    public FilterLiteralResolution<string> Resolve(string text) => FilterLiteralResolution<string>.Success(text);
}

internal sealed class BooleanLiteralCodec : IFilterLiteralCodec<bool>
{
    private static readonly IReadOnlyList<FilterLiteralCandidate<bool>> KnownValues =
    [
        new(true, "true", ["yes"]),
        new(false, "false", ["no"]),
    ];

    public string TypeName => "boolean";
    public IReadOnlyList<FilterLiteralCandidate<bool>> Values => KnownValues;

    public FilterLiteralResolution<bool> Resolve(string text) => text.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" => FilterLiteralResolution<bool>.Success(true),
        "false" or "no" => FilterLiteralResolution<bool>.Success(false),
        _ => FilterLiteralResolution<bool>.NotFound($"'{text}' is not a boolean value. Use true, false, yes, or no."),
    };
}

internal sealed class Int64LiteralCodec(string typeName) : IFilterLiteralCodec<long>
{
    public string TypeName => typeName;
    public IReadOnlyList<FilterLiteralCandidate<long>> Values => [];

    public FilterLiteralResolution<long> Resolve(string text)
    {
        var normalized = text.Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        return long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? FilterLiteralResolution<long>.Success(value)
            : FilterLiteralResolution<long>.NotFound($"'{text}' is not a valid {typeName}.");
    }
}

internal sealed class DecimalLiteralCodec(string typeName) : IFilterLiteralCodec<decimal>
{
    public string TypeName => typeName;
    public IReadOnlyList<FilterLiteralCandidate<decimal>> Values => [];

    public FilterLiteralResolution<decimal> Resolve(string text)
    {
        var normalized = text.Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? FilterLiteralResolution<decimal>.Success(value)
            : FilterLiteralResolution<decimal>.NotFound($"'{text}' is not a valid {typeName}.");
    }
}

internal sealed class DurationLiteralCodec : IFilterLiteralCodec<TimeSpan>
{
    public string TypeName => "duration";
    public IReadOnlyList<FilterLiteralCandidate<TimeSpan>> Values => [];

    public FilterLiteralResolution<TimeSpan> Resolve(string text)
    {
        var normalized = text.Trim();
        var split = 0;
        while (split < normalized.Length && (char.IsDigit(normalized[split]) || normalized[split] is '.' or ',' or '_'))
            split++;

        if (split == 0 || split == normalized.Length)
            return FilterLiteralResolution<TimeSpan>.NotFound($"'{text}' is not a duration. Use units such as 30m, 6h, or 2d.");

        var numberText = normalized[..split].Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        if (!double.TryParse(numberText, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ||
            number < 0 || double.IsInfinity(number) || double.IsNaN(number))
        {
            return FilterLiteralResolution<TimeSpan>.NotFound($"'{text}' is not a valid duration.");
        }

        try
        {
            var value = normalized[split..].ToLowerInvariant() switch
            {
                "ms" => TimeSpan.FromMilliseconds(number),
                "s" => TimeSpan.FromSeconds(number),
                "m" => TimeSpan.FromMinutes(number),
                "h" => TimeSpan.FromHours(number),
                "d" => TimeSpan.FromDays(number),
                "w" => TimeSpan.FromDays(number * 7),
                _ => throw new FormatException(),
            };
            return FilterLiteralResolution<TimeSpan>.Success(value);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            return FilterLiteralResolution<TimeSpan>.NotFound($"'{text}' is not a valid duration.");
        }
    }
}

internal sealed class EnumLiteralCodec<TEnum> : IFilterLiteralCodec<TEnum> where TEnum : struct, Enum
{
    private readonly FilterNamedValueCatalog<TEnum> catalog;

    public EnumLiteralCodec(IReadOnlyDictionary<string, TEnum>? aliases)
    {
        var candidates = Enum.GetValues<TEnum>()
            .Select(value => new FilterLiteralCandidate<TEnum>(
                value,
                value.ToString(),
                aliases?
                    .Where(pair => EqualityComparer<TEnum>.Default.Equals(pair.Value, value))
                    .Select(pair => pair.Key)
                    .ToArray()))
            .ToArray();
        catalog = new FilterNamedValueCatalog<TEnum>(candidates);
    }

    public string TypeName => typeof(TEnum).Name;
    public IReadOnlyList<FilterLiteralCandidate<TEnum>> Values => catalog.Values;

    public FilterLiteralResolution<TEnum> Resolve(string text)
    {
        var matches = catalog.Resolve(text);
        return matches.Count switch
        {
            1 => FilterLiteralResolution<TEnum>.Success(matches[0].Value),
            > 1 => FilterLiteralResolution<TEnum>.Ambiguous($"'{text}' matches more than one {TypeName}.", matches),
            _ => FilterLiteralResolution<TEnum>.NotFound($"'{text}' is not a known {TypeName}."),
        };
    }
}

internal sealed class NamedLiteralCodec<T>(IFilterNamedValueResolver<T> resolver, string typeName) : IFilterLiteralCodec<T>
{
    public string TypeName => typeName;
    public IReadOnlyList<FilterLiteralCandidate<T>> Values => resolver.Values;

    public FilterLiteralResolution<T> Resolve(string text)
    {
        var matches = resolver.Resolve(text);
        return matches.Count switch
        {
            1 => FilterLiteralResolution<T>.Success(matches[0].Value),
            > 1 => FilterLiteralResolution<T>.Ambiguous($"'{text}' matches more than one {typeName}.", matches),
            _ => FilterLiteralResolution<T>.NotFound($"No {typeName} named '{text}' was found."),
        };
    }
}

internal sealed class ValidatedLiteralCodec<T>(
    IFilterLiteralCodec<T> inner,
    Func<T, bool> predicate,
    Func<T, string> errorMessage) : IFilterLiteralCodec<T>
{
    public string TypeName => inner.TypeName;
    public IReadOnlyList<FilterLiteralCandidate<T>> Values => inner.Values;

    public FilterLiteralResolution<T> Resolve(string text)
    {
        var resolution = inner.Resolve(text);
        if (resolution.Kind != FilterLiteralResolutionKind.Success)
            return resolution;
        return predicate(resolution.Value!)
            ? resolution
            : FilterLiteralResolution<T>.NotFound(errorMessage(resolution.Value!));
    }
}
