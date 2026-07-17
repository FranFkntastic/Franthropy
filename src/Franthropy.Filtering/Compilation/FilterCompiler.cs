using Franthropy.Filtering.Diagnostics;
using Franthropy.Filtering.Evaluation;
using Franthropy.Filtering.Semantics;
using Franthropy.Filtering.Syntax;

namespace Franthropy.Filtering.Compilation;

public static class FilterCompiler
{
    public static FilterCompilation<TRecord> Compile<TRecord>(
        string? expression,
        FilterContext<TRecord> context,
        FilterLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var effectiveLimits = (limits ?? FilterLimits.Default).Validate();
        var syntax = FilterSyntaxTree.Parse(expression, effectiveLimits);
        var diagnostics = new DiagnosticBag(effectiveLimits.MaximumDiagnostics);
        diagnostics.AddRange(syntax.Diagnostics);
        var evaluator = Bind(syntax.Root.Expression, context, diagnostics);
        return new FilterCompilation<TRecord>(syntax, diagnostics.Diagnostics.ToArray(), evaluator)
        {
            SemanticExpression = FilterSemanticFormatter.Format(syntax, context.Catalog, context.AvailableKeys),
        };
    }

    private static Func<TRecord, FilterTruth> Bind<TRecord>(
        FilterExpressionSyntax expression,
        FilterContext<TRecord> context,
        DiagnosticBag diagnostics)
    {
        switch (expression)
        {
            case FilterMissingExpressionSyntax:
                return _ => FilterTruth.True;
            case FilterParenthesizedExpressionSyntax parenthesized:
                return Bind(parenthesized.Expression, context, diagnostics);
            case FilterUnaryExpressionSyntax unary:
            {
                var operand = Bind(unary.Operand, context, diagnostics);
                return record => FilterTruthOperations.Not(operand(record));
            }
            case FilterBinaryExpressionSyntax binary:
            {
                var left = Bind(binary.Left, context, diagnostics);
                var right = Bind(binary.Right, context, diagnostics);
                return binary.Operator == FilterBinaryOperator.And
                    ? record => FilterTruthOperations.And(left(record), right(record))
                    : record => FilterTruthOperations.Or(left(record), right(record));
            }
            case FilterFieldExpressionSyntax fieldExpression:
                if (fieldExpression.Comparator.Kind == FilterTokenKind.Colon &&
                    fieldExpression.Value is FilterScalarValueSyntax predicateValue)
                {
                    var predicate = context.Catalog.ResolvePredicate(fieldExpression.Field.Value, predicateValue.Token.Value);
                    if (predicate is not null)
                    {
                        var target = context.Catalog.Resolve(predicate.TargetFieldKey, context.AvailableKeys);
                        var syntheticValue = new FilterScalarValueSyntax(predicateValue.Token with
                        {
                            Text = predicate.TargetValue,
                            Value = predicate.TargetValue,
                        });
                        var syntheticComparator = fieldExpression.Comparator with
                        {
                            Kind = FilterTokenKind.ExactEquals,
                            Text = "==",
                            Value = "==",
                        };
                        return BindResolvedField(target.Field!, syntheticComparator, syntheticValue, context, diagnostics, fieldExpression.Span);
                    }
                }
                return BindField(fieldExpression.Field.Value, fieldExpression.Field.Span, fieldExpression.Comparator,
                    fieldExpression.Value, context, diagnostics);
            case FilterReservedNestedQualifierSyntax nested:
                diagnostics.Add(FilterDiagnosticCodes.ReservedNestedQualifier,
                    $"Nested qualifier '{string.Join(':', nested.Segments.Select(segment => segment.Value))}:' is reserved for future parameterized domains.",
                    nested.Span);
                return _ => FilterTruth.Unknown;
            case FilterFunctionCallSyntax function:
                return BindEvidenceFunction(function, context, diagnostics);
            case FilterFreeTextSyntax freeText:
                return BindFreeText(freeText, context, diagnostics);
            default:
                return _ => FilterTruth.Unknown;
        }
    }

    private static Func<TRecord, FilterTruth> BindFreeText<TRecord>(
        FilterFreeTextSyntax freeText,
        FilterContext<TRecord> context,
        DiagnosticBag diagnostics)
    {
        if (context.DefaultTextBindings.Count == 0)
        {
            diagnostics.Add(FilterDiagnosticCodes.NoDefaultTextField,
                "This filter context has no default text field for free-text search.", freeText.Span);
            return _ => FilterTruth.Unknown;
        }

        var searchText = freeText.Text.Value;
        var evaluators = context.DefaultTextBindings
            .Select(binding => (Func<TRecord, FilterTruth>)(record =>
            {
                var evidence = binding.Accessor(record);
                if (!evidence.IsKnown)
                    return FilterTruth.Unknown;
                return FilterText.Contains(evidence.Value, searchText)
                    ? FilterTruth.True
                    : FilterTruth.False;
            }))
            .ToArray();
        return record =>
        {
            var truth = FilterTruth.False;
            for (var i = 0; i < evaluators.Length; i++)
                truth = FilterTruthOperations.Or(truth, evaluators[i](record));
            return truth;
        };
    }

    private static Func<TRecord, FilterTruth> BindField<TRecord>(
        string fieldText,
        TextSpan fieldSpan,
        FilterToken comparator,
        FilterValueSyntax value,
        FilterContext<TRecord> context,
        DiagnosticBag diagnostics)
    {
        var resolution = context.Catalog.Resolve(fieldText, context.AvailableKeys);
        if (resolution.Kind == FilterFieldResolutionKind.NotFound)
        {
            diagnostics.Add(FilterDiagnosticCodes.UnknownField, $"Unknown field '{fieldText}'.", fieldSpan);
            return _ => FilterTruth.Unknown;
        }
        if (resolution.Kind == FilterFieldResolutionKind.Ambiguous)
        {
            diagnostics.Add(FilterDiagnosticCodes.AmbiguousField,
                $"Field '{fieldText}' is ambiguous. Use {string.Join(" or ", resolution.Candidates.Select(field => $"'{field.Key}'"))}.",
                fieldSpan);
            return _ => FilterTruth.Unknown;
        }

        return BindResolvedField(resolution.Field!, comparator, value, context, diagnostics, fieldSpan);
    }

    private static Func<TRecord, FilterTruth> BindResolvedField<TRecord>(
        FilterField field,
        FilterToken comparator,
        FilterValueSyntax value,
        FilterContext<TRecord> context,
        DiagnosticBag diagnostics,
        TextSpan fieldSpan)
    {
        if (!context.Bindings.TryGetValue(field.Key, out var accessor))
        {
            diagnostics.Add(FilterDiagnosticCodes.UnavailableField,
                $"Field '{field.Key}' is known, but is not available in this view.", fieldSpan);
            return _ => FilterTruth.Unknown;
        }
        if (!FilterComparisonOperatorExtensions.TryFromToken(comparator, out var comparison))
            return _ => FilterTruth.Unknown;

        var evaluator = accessor.Bind(comparison, value, diagnostics);
        return evaluator ?? (_ => FilterTruth.Unknown);
    }

    private static Func<TRecord, FilterTruth> BindEvidenceFunction<TRecord>(
        FilterFunctionCallSyntax function,
        FilterContext<TRecord> context,
        DiagnosticBag diagnostics)
    {
        var resolution = context.Catalog.Resolve(function.Field.Value, context.AvailableKeys);
        if (resolution.Kind == FilterFieldResolutionKind.NotFound)
        {
            diagnostics.Add(FilterDiagnosticCodes.UnknownField, $"Unknown field '{function.Field.Value}'.", function.Field.Span);
            return _ => FilterTruth.Unknown;
        }
        if (resolution.Kind == FilterFieldResolutionKind.Ambiguous)
        {
            diagnostics.Add(FilterDiagnosticCodes.AmbiguousField,
                $"Field '{function.Field.Value}' is ambiguous. Use a canonical field path.", function.Field.Span);
            return _ => FilterTruth.Unknown;
        }
        if (!context.Bindings.TryGetValue(resolution.Field!.Key, out var accessor))
        {
            diagnostics.Add(FilterDiagnosticCodes.UnavailableField,
                $"Field '{resolution.Field.Key}' is known, but is not available in this view.", function.Field.Span);
            return _ => FilterTruth.Unknown;
        }

        var wantsKnown = function.Function.Value.Equals("known", StringComparison.OrdinalIgnoreCase);
        return record => accessor.IsKnown(record) == wantsKnown ? FilterTruth.True : FilterTruth.False;
    }
}
