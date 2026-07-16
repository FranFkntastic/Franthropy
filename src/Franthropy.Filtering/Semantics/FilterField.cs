using Franthropy.Filtering.Diagnostics;
using Franthropy.Filtering.Evaluation;
using Franthropy.Filtering.Syntax;

namespace Franthropy.Filtering.Semantics;

public enum FilterValueKind
{
    Text,
    Boolean,
    Integer,
    Decimal,
    Duration,
    Enumeration,
    Named,
    Set,
}

public abstract class FilterField
{
    protected FilterField(
        string key,
        string displayName,
        string description,
        FilterValueKind valueKind,
        IEnumerable<string>? aliases)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("A canonical field key is required.", nameof(key));

        Key = key.Trim();
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? Key : displayName.Trim();
        Description = description?.Trim() ?? string.Empty;
        ValueKind = valueKind;
        Aliases = aliases?
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
    }

    public string Key { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public FilterValueKind ValueKind { get; }
    public IReadOnlyList<string> Aliases { get; }
    public abstract IReadOnlySet<FilterComparisonOperator> Operators { get; }
    public abstract IReadOnlyList<FilterValueReference> Values { get; }
    internal virtual bool CanBeBareBoolean => false;

}

public sealed class FilterField<T> : FilterField
{
    private readonly IFilterLiteralCodec<T> codec;
    private readonly IEqualityComparer<T> equalityComparer;
    private readonly IComparer<T>? orderComparer;
    private readonly bool textMatching;
    private readonly IReadOnlySet<FilterComparisonOperator> operators;

    internal FilterField(
        string key,
        string displayName,
        string description,
        FilterValueKind valueKind,
        IFilterLiteralCodec<T> codec,
        IEnumerable<FilterComparisonOperator> operators,
        IEnumerable<string>? aliases,
        IEqualityComparer<T>? equalityComparer = null,
        IComparer<T>? orderComparer = null,
        bool textMatching = false)
        : base(key, displayName, description, valueKind, aliases)
    {
        this.codec = codec;
        this.equalityComparer = equalityComparer ?? EqualityComparer<T>.Default;
        this.orderComparer = orderComparer;
        this.textMatching = textMatching;
        this.operators = new HashSet<FilterComparisonOperator>(operators);
    }

    public override IReadOnlySet<FilterComparisonOperator> Operators => operators;
    public IReadOnlyList<FilterLiteralCandidate<T>> KnownValues => codec.Values;
    public override IReadOnlyList<FilterValueReference> Values => codec.Values
        .Select(candidate => new FilterValueReference(candidate.DisplayName, candidate.Aliases ?? []))
        .ToArray();
    internal override bool CanBeBareBoolean => typeof(T) == typeof(bool);

    internal BoundFieldTest<T>? BindTyped(
        FilterComparisonOperator comparison,
        FilterValueSyntax value,
        DiagnosticBag diagnostics)
    {
        if (!operators.Contains(comparison))
        {
            diagnostics.Add(
                FilterDiagnosticCodes.InvalidOperator,
                $"Field '{Key}' does not support '{comparison.Display()}'.",
                value.Span);
            return null;
        }

        if (!TryResolveValue(comparison, value, diagnostics, out var operand))
            return null;

        return evidence =>
        {
            if (!evidence.IsKnown)
                return FilterTruth.Unknown;
            if (evidence.Value is not T actual)
                return FilterTruth.Unknown;

            return Evaluate(actual, comparison, operand!);
        };
    }

    private FilterTruth Evaluate(T actual, FilterComparisonOperator comparison, BoundOperand<T> operand)
    {
        bool result;
        if (operand is BoundRangeOperand<T> range)
        {
            if (orderComparer is null)
                return FilterTruth.Unknown;
            result = (!range.HasLower || orderComparer.Compare(actual, range.Lower) >= 0) &&
                     (!range.HasUpper || orderComparer.Compare(actual, range.Upper) <= 0);
            return comparison == FilterComparisonOperator.NotEquals
                ? ToTruth(!result)
                : ToTruth(result);
        }

        var values = ((BoundValuesOperand<T>)operand).Values;
        result = comparison switch
        {
            FilterComparisonOperator.Match when textMatching && actual is string text =>
                TextContainsAny(text, values),
            FilterComparisonOperator.Match or FilterComparisonOperator.Equals =>
                EqualsAny(actual, values),
            FilterComparisonOperator.NotEquals =>
                !EqualsAny(actual, values),
            FilterComparisonOperator.Less => orderComparer!.Compare(actual, values[0]) < 0,
            FilterComparisonOperator.LessOrEqual => orderComparer!.Compare(actual, values[0]) <= 0,
            FilterComparisonOperator.Greater => orderComparer!.Compare(actual, values[0]) > 0,
            FilterComparisonOperator.GreaterOrEqual => orderComparer!.Compare(actual, values[0]) >= 0,
            _ => false,
        };
        return ToTruth(result);
    }

    private static bool TextContainsAny(string actual, IReadOnlyList<T> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (actual.Contains((string)(object)values[i]!, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool EqualsAny(T actual, IReadOnlyList<T> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (equalityComparer.Equals(actual, values[i]))
                return true;
        }
        return false;
    }

    private bool TryResolveValue(
        FilterComparisonOperator comparison,
        FilterValueSyntax syntax,
        DiagnosticBag diagnostics,
        out BoundOperand<T>? operand)
    {
        operand = null;
        if (syntax is FilterMissingValueSyntax)
            return false;

        if (syntax is FilterRangeValueSyntax range)
        {
            if (comparison is not (FilterComparisonOperator.Match or FilterComparisonOperator.Equals or FilterComparisonOperator.NotEquals))
            {
                diagnostics.Add(FilterDiagnosticCodes.InvalidOperator,
                    $"Operator '{comparison.Display()}' compares one value and cannot be used with a range.", syntax.Span);
                return false;
            }
            if (orderComparer is null)
            {
                diagnostics.Add(FilterDiagnosticCodes.InvalidValue, $"Field '{Key}' does not support ranges.", syntax.Span);
                return false;
            }

            if (!TryResolveOptional(range.Lower, diagnostics, out var hasLower, out var lower) ||
                !TryResolveOptional(range.Upper, diagnostics, out var hasUpper, out var upper))
                return false;

            if (hasLower && hasUpper && orderComparer.Compare(lower!, upper!) > 0)
            {
                diagnostics.Add(FilterDiagnosticCodes.InvalidValue,
                    "The lower range endpoint cannot be greater than the upper endpoint.", syntax.Span);
                return false;
            }

            operand = new BoundRangeOperand<T>(hasLower, lower!, hasUpper, upper!);
            return true;
        }

        var scalarValues = syntax switch
        {
            FilterScalarValueSyntax scalar => new[] { scalar },
            FilterListValueSyntax list => list.Values.ToArray(),
            _ => [],
        };

        if (scalarValues.Length > 1 && comparison is FilterComparisonOperator.Less or FilterComparisonOperator.LessOrEqual
            or FilterComparisonOperator.Greater or FilterComparisonOperator.GreaterOrEqual)
        {
            diagnostics.Add(FilterDiagnosticCodes.InvalidOperator,
                $"Operator '{comparison.Display()}' compares one value and cannot be used with a list.", syntax.Span);
            return false;
        }

        var values = new List<T>(scalarValues.Length);
        foreach (var scalar in scalarValues)
        {
            var resolution = codec.Resolve(scalar.Token.Value);
            if (resolution.Kind != FilterLiteralResolutionKind.Success)
            {
                var code = resolution.Kind == FilterLiteralResolutionKind.Ambiguous
                    ? FilterDiagnosticCodes.AmbiguousValue
                    : FilterDiagnosticCodes.InvalidValue;
                diagnostics.Add(code, resolution.Message ?? $"Invalid {codec.TypeName} value.", scalar.Span);
                return false;
            }

            values.Add(resolution.Value!);
        }

        if (values.Count == 0)
            return false;
        operand = new BoundValuesOperand<T>(values);
        return true;
    }

    private bool TryResolveOptional(
        FilterScalarValueSyntax? scalar,
        DiagnosticBag diagnostics,
        out bool hasValue,
        out T? value)
    {
        hasValue = scalar is not null;
        value = default;
        if (scalar is null)
            return true;

        var resolution = codec.Resolve(scalar.Token.Value);
        if (resolution.Kind == FilterLiteralResolutionKind.Success)
        {
            value = resolution.Value;
            return true;
        }

        diagnostics.Add(
            resolution.Kind == FilterLiteralResolutionKind.Ambiguous
                ? FilterDiagnosticCodes.AmbiguousValue
                : FilterDiagnosticCodes.InvalidValue,
            resolution.Message ?? $"Invalid {codec.TypeName} value.",
            scalar.Span);
        return false;
    }

    private static FilterTruth ToTruth(bool value) => value ? FilterTruth.True : FilterTruth.False;
}

public sealed class FilterSetField<T> : FilterField
{
    private readonly IFilterLiteralCodec<T> codec;
    private readonly IEqualityComparer<T> comparer;
    private readonly IReadOnlySet<FilterComparisonOperator> operators = new HashSet<FilterComparisonOperator>
    {
        FilterComparisonOperator.Match,
        FilterComparisonOperator.Equals,
        FilterComparisonOperator.NotEquals,
    };

    internal FilterSetField(
        string key,
        string displayName,
        string description,
        IFilterLiteralCodec<T> codec,
        IEnumerable<string>? aliases,
        IEqualityComparer<T>? comparer)
        : base(key, displayName, description, FilterValueKind.Set, aliases)
    {
        this.codec = codec;
        this.comparer = comparer ?? EqualityComparer<T>.Default;
    }

    public override IReadOnlySet<FilterComparisonOperator> Operators => operators;
    public IReadOnlyList<FilterLiteralCandidate<T>> KnownValues => codec.Values;
    public override IReadOnlyList<FilterValueReference> Values => codec.Values
        .Select(candidate => new FilterValueReference(candidate.DisplayName, candidate.Aliases ?? []))
        .ToArray();

    internal BoundSetFieldTest<T>? BindTyped(
        FilterComparisonOperator comparison,
        FilterValueSyntax value,
        DiagnosticBag diagnostics)
    {
        if (!operators.Contains(comparison))
        {
            diagnostics.Add(FilterDiagnosticCodes.InvalidOperator, $"Field '{Key}' does not support '{comparison.Display()}'.", value.Span);
            return null;
        }

        var scalarValues = value switch
        {
            FilterScalarValueSyntax scalar => new[] { scalar },
            FilterListValueSyntax list => list.Values.ToArray(),
            _ => [],
        };
        if (scalarValues.Length == 0)
        {
            diagnostics.Add(FilterDiagnosticCodes.InvalidValue, $"Field '{Key}' expects a value or value list.", value.Span);
            return null;
        }

        var expected = new List<T>(scalarValues.Length);
        foreach (var scalar in scalarValues)
        {
            var resolution = codec.Resolve(scalar.Token.Value);
            if (resolution.Kind != FilterLiteralResolutionKind.Success)
            {
                diagnostics.Add(
                    resolution.Kind == FilterLiteralResolutionKind.Ambiguous
                        ? FilterDiagnosticCodes.AmbiguousValue
                        : FilterDiagnosticCodes.InvalidValue,
                    resolution.Message ?? $"Invalid {codec.TypeName} value.",
                    scalar.Span);
                return null;
            }
            expected.Add(resolution.Value!);
        }

        return evidence =>
        {
            if (!evidence.IsKnown || evidence.Value is not IReadOnlyCollection<T> actual)
                return FilterTruth.Unknown;

            var overlaps = expected.Any(wanted => actual.Any(candidate => comparer.Equals(candidate, wanted)));
            var exact = actual.Count == expected.Count &&
                        actual.All(candidate => expected.Any(wanted => comparer.Equals(candidate, wanted)));
            var result = comparison switch
            {
                FilterComparisonOperator.Match => overlaps,
                FilterComparisonOperator.Equals => exact,
                FilterComparisonOperator.NotEquals => !exact,
                _ => false,
            };
            return result ? FilterTruth.True : FilterTruth.False;
        };
    }
}

internal delegate FilterTruth BoundFieldTest<T>(FieldEvidence<T> evidence);
internal delegate FilterTruth BoundSetFieldTest<T>(FieldEvidence<IReadOnlyCollection<T>> evidence);
internal abstract record BoundOperand<T>;
internal sealed record BoundValuesOperand<T>(IReadOnlyList<T> Values) : BoundOperand<T>;
internal sealed record BoundRangeOperand<T>(bool HasLower, T Lower, bool HasUpper, T Upper) : BoundOperand<T>;

public sealed record FilterValueReference(string DisplayName, IReadOnlyList<string> Aliases);

public static class FilterFields
{
    private static readonly FilterComparisonOperator[] EqualityOperators =
    [
        FilterComparisonOperator.Match,
        FilterComparisonOperator.Equals,
        FilterComparisonOperator.NotEquals,
    ];

    private static readonly FilterComparisonOperator[] OrderedOperators =
    [
        .. EqualityOperators,
        FilterComparisonOperator.Less,
        FilterComparisonOperator.LessOrEqual,
        FilterComparisonOperator.Greater,
        FilterComparisonOperator.GreaterOrEqual,
    ];

    public static FilterField<string> Text(
        string key,
        string? displayName = null,
        string description = "",
        IEnumerable<string>? aliases = null) =>
        new(key, displayName ?? key, description, FilterValueKind.Text, new TextLiteralCodec(), EqualityOperators,
            aliases, StringComparer.OrdinalIgnoreCase, textMatching: true);

    public static FilterField<bool> Boolean(
        string key,
        string? displayName = null,
        string description = "",
        IEnumerable<string>? aliases = null) =>
        new(key, displayName ?? key, description, FilterValueKind.Boolean, new BooleanLiteralCodec(), EqualityOperators, aliases);

    public static FilterField<long> Integer(
        string key,
        string? displayName = null,
        string description = "",
        IEnumerable<string>? aliases = null,
        long? minimum = null,
        long? maximum = null) =>
        new(key, displayName ?? key, description, FilterValueKind.Integer,
            Validate(new Int64LiteralCodec("integer"), minimum, maximum), OrderedOperators,
            aliases, orderComparer: Comparer<long>.Default);

    public static FilterField<decimal> Decimal(
        string key,
        string? displayName = null,
        string description = "",
        IEnumerable<string>? aliases = null,
        decimal? minimum = null,
        decimal? maximum = null) =>
        new(key, displayName ?? key, description, FilterValueKind.Decimal,
            Validate(new DecimalLiteralCodec("number"), minimum, maximum), OrderedOperators,
            aliases, orderComparer: Comparer<decimal>.Default);

    public static FilterField<TimeSpan> Duration(
        string key,
        string? displayName = null,
        string description = "",
        IEnumerable<string>? aliases = null) =>
        new(key, displayName ?? key, description, FilterValueKind.Duration, new DurationLiteralCodec(), OrderedOperators,
            aliases, orderComparer: Comparer<TimeSpan>.Default);

    public static FilterField<TEnum> Enumeration<TEnum>(
        string key,
        string? displayName = null,
        string description = "",
        IEnumerable<string>? aliases = null,
        IReadOnlyDictionary<string, TEnum>? valueAliases = null)
        where TEnum : struct, Enum =>
        new(key, displayName ?? key, description, FilterValueKind.Enumeration, new EnumLiteralCodec<TEnum>(valueAliases),
            EqualityOperators, aliases);

    public static FilterField<T> Named<T>(
        string key,
        IFilterNamedValueResolver<T> resolver,
        string typeName,
        string? displayName = null,
        string description = "",
        IEnumerable<string>? aliases = null,
        IEqualityComparer<T>? comparer = null) =>
        new(key, displayName ?? key, description, FilterValueKind.Named, new NamedLiteralCodec<T>(resolver, typeName),
            EqualityOperators, aliases, comparer);

    public static FilterSetField<T> Set<T>(
        string key,
        IFilterNamedValueResolver<T> resolver,
        string typeName,
        string? displayName = null,
        string description = "",
        IEnumerable<string>? aliases = null,
        IEqualityComparer<T>? comparer = null) =>
        new(key, displayName ?? key, description, new NamedLiteralCodec<T>(resolver, typeName), aliases, comparer);

    private static IFilterLiteralCodec<T> Validate<T>(
        IFilterLiteralCodec<T> codec,
        T? minimum,
        T? maximum)
        where T : struct, IComparable<T>
    {
        if (minimum is null && maximum is null)
            return codec;
        return new ValidatedLiteralCodec<T>(codec,
            value => (minimum is null || value.CompareTo(minimum.Value) >= 0) &&
                     (maximum is null || value.CompareTo(maximum.Value) <= 0),
            value => $"'{value}' is outside the allowed range{FormatBounds(minimum, maximum)}.");
    }

    private static string FormatBounds<T>(T? minimum, T? maximum) where T : struct =>
        minimum is not null && maximum is not null
            ? $" {minimum} through {maximum}"
            : minimum is not null
                ? $" beginning at {minimum}"
                : $" ending at {maximum}";
}
