namespace Franthropy.Dalamud.Equipment;

public sealed record EquipmentUtilityComponentDefinition(
    string ComponentKey,
    EquipmentStatSemantic Semantic,
    double Divisor,
    double MaximumContribution,
    string Rationale);

public sealed record EquipmentUtilityCapabilityRequirement(
    string ComponentKey,
    long Minimum);

public sealed record EquipmentUtilityCapabilityDefinition(
    string ThresholdId,
    string Label,
    IReadOnlyList<EquipmentUtilityCapabilityRequirement> Requirements,
    double ScoreContribution,
    string Rationale);

public sealed record EquipmentThresholdUtilityModelDefinition(
    JobUtilityProfile Profile,
    EquipmentUtilityContext Context,
    EquipmentSolverUtilityVector Baseline,
    IReadOnlyList<EquipmentUtilityComponentDefinition> Components,
    IReadOnlyList<EquipmentUtilityCapabilityDefinition> Capabilities,
    double UncertaintyRadius,
    IReadOnlyList<string> UncertaintyReasons,
    bool IsSupported = true,
    IReadOnlyList<string>? Diagnostics = null,
    EquipmentSolverUtilityVector? FixedComponents = null);

/// <summary>
/// A deliberately small threshold-aware utility model. Capability steps and bounded monotonic
/// progress remain separately explainable; recommendation authority stays with the consumer.
/// </summary>
public sealed class EquipmentThresholdUtilityModel : IEquipmentExactSolverUtilityModel
{
    private readonly EquipmentThresholdUtilityModelDefinition definition;
    private readonly EquipmentSolverUtilityVector baseline;
    private readonly EquipmentSolverUtilityVector fixedComponents;
    private readonly IReadOnlyDictionary<string, EquipmentUtilityComponentDefinition> components;

    public EquipmentThresholdUtilityModel(EquipmentThresholdUtilityModelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateDefinition(definition);
        this.definition = definition;
        baseline = definition.Baseline.Normalize();
        fixedComponents = (definition.FixedComponents ?? EquipmentSolverUtilityVector.Empty).Normalize();
        components = definition.Components.ToDictionary(component => component.ComponentKey, StringComparer.Ordinal);
        ValidateVector(baseline);
        ValidateVector(fixedComponents);
    }

    public EquipmentThresholdUtilityModelDefinition Definition => definition;

    public EquipmentPartialUtilityDominance ComparePartial(
        EquipmentSolverUtilityVector candidate,
        EquipmentSolverUtilityVector other)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(other);
        candidate = candidate.Normalize();
        other = other.Normalize();
        ValidateVector(candidate);
        ValidateVector(other);

        var noWorse = definition.Components.All(component =>
            candidate.Get(component.ComponentKey) >= other.Get(component.ComponentKey));
        var strictlyBetter = noWorse && definition.Components.Any(component =>
            candidate.Get(component.ComponentKey) > other.Get(component.ComponentKey));
        return new(noWorse, strictlyBetter);
    }

    public EquipmentUtilityEvaluation Evaluate(EquipmentSolverUtilityVector completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        completed = completed.Normalize();
        ValidateVector(completed);
        completed = completed.Add(fixedComponents);
        var comparisonBaseline = baseline.Add(fixedComponents);

        var rawStats = definition.Components
            .Select(component => new EquipmentStatObservation(
                component.Semantic,
                checked((int)completed.Get(component.ComponentKey)),
                component.ComponentKey))
            .ToArray();
        var contributions = new List<EquipmentStatContribution>();
        var score = 0d;
        foreach (var component in definition.Components)
        {
            var raw = completed.Get(component.ComponentKey);
            var contribution = Math.Min(raw / component.Divisor, component.MaximumContribution);
            score += contribution;
            contributions.Add(new(
                component.Semantic,
                checked((int)raw),
                1d / component.Divisor,
                contribution,
                component.Rationale));
        }

        var thresholds = new List<EquipmentUtilityThreshold>();
        foreach (var capability in definition.Capabilities)
        {
            var satisfied = capability.Requirements.All(requirement =>
                completed.Get(requirement.ComponentKey) >= requirement.Minimum);
            if (satisfied)
                score += capability.ScoreContribution;
            thresholds.Add(new(
                capability.ThresholdId,
                capability.Label,
                capability.Requirements.Count == 1 ? capability.Requirements[0].Minimum : null,
                null,
                satisfied,
                capability.Rationale));
            if (satisfied)
            {
                var semantic = capability.Requirements.Count == 1
                    ? components[capability.Requirements[0].ComponentKey].Semantic
                    : EquipmentStatSemantic.Unknown;
                contributions.Add(new(
                    semantic,
                    capability.Requirements.Count == 1
                        ? checked((int)completed.Get(capability.Requirements[0].ComponentKey))
                        : 0,
                    0,
                    capability.ScoreContribution,
                    capability.Rationale));
            }
        }

        var assessment = Assess(completed, comparisonBaseline);
        var changedCapabilities = definition.Capabilities.Count(capability =>
            capability.Requirements.All(requirement => completed.Get(requirement.ComponentKey) >= requirement.Minimum) !=
            capability.Requirements.All(requirement => comparisonBaseline.Get(requirement.ComponentKey) >= requirement.Minimum));
        var confidence = assessment switch
        {
            UpgradeAssessment.Unsupported => EquipmentEvaluationConfidence.Unknown,
            UpgradeAssessment.ContextDependent => EquipmentEvaluationConfidence.Low,
            UpgradeAssessment.ClearImprovement or UpgradeAssessment.ClearRegression when changedCapabilities > 0 => EquipmentEvaluationConfidence.High,
            UpgradeAssessment.ClearImprovement or UpgradeAssessment.ClearRegression => EquipmentEvaluationConfidence.Medium,
            _ => EquipmentEvaluationConfidence.High,
        };
        var diagnostics = new List<string>(definition.Diagnostics ?? []);
        if (definition.IsSupported &&
            assessment is UpgradeAssessment.ClearImprovement or UpgradeAssessment.ClearRegression &&
            changedCapabilities == 0)
        {
            diagnostics.Add("The score changed monotonically, but no supported capability threshold changed.");
        }
        if (!definition.IsSupported)
            diagnostics.Add("This evaluation context is research-only and cannot grant recommendation authority.");

        var radius = definition.IsSupported ? definition.UncertaintyRadius : Math.Max(definition.UncertaintyRadius, 1d);
        return new(
            definition.Profile.Key,
            definition.Context,
            score,
            new(score - radius, score + radius, definition.UncertaintyReasons),
            assessment,
            rawStats,
            contributions,
            thresholds,
            confidence,
            diagnostics);
    }

    private UpgradeAssessment Assess(
        EquipmentSolverUtilityVector completed,
        EquipmentSolverUtilityVector comparisonBaseline)
    {
        if (!definition.IsSupported)
            return UpgradeAssessment.Unsupported;
        var greater = definition.Components.Any(component =>
            completed.Get(component.ComponentKey) > comparisonBaseline.Get(component.ComponentKey));
        var less = definition.Components.Any(component =>
            completed.Get(component.ComponentKey) < comparisonBaseline.Get(component.ComponentKey));
        return (greater, less) switch
        {
            (false, false) => UpgradeAssessment.Equivalent,
            (true, false) => UpgradeAssessment.ClearImprovement,
            (false, true) => UpgradeAssessment.ClearRegression,
            _ => UpgradeAssessment.ContextDependent,
        };
    }

    private void ValidateVector(EquipmentSolverUtilityVector vector)
    {
        var unknown = vector.Components
            .Select(component => component.Key)
            .Where(key => !components.ContainsKey(key))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException($"Utility vector contains undeclared components: {string.Join(", ", unknown)}.");
    }

    private static void ValidateDefinition(EquipmentThresholdUtilityModelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition.Profile);
        ArgumentNullException.ThrowIfNull(definition.Context);
        ArgumentNullException.ThrowIfNull(definition.Baseline);
        ArgumentNullException.ThrowIfNull(definition.Components);
        ArgumentNullException.ThrowIfNull(definition.Capabilities);
        ArgumentNullException.ThrowIfNull(definition.UncertaintyReasons);
        if (definition.Components.Count == 0)
            throw new ArgumentException("At least one utility component is required.", nameof(definition));
        if (definition.UncertaintyRadius < 0 || !double.IsFinite(definition.UncertaintyRadius))
            throw new ArgumentOutOfRangeException(nameof(definition), "Uncertainty radius must be finite and non-negative.");

        var duplicateComponent = definition.Components
            .GroupBy(component => component.ComponentKey, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateComponent is not null)
            throw new ArgumentException($"Duplicate utility component '{duplicateComponent.Key}'.", nameof(definition));
        foreach (var component in definition.Components)
        {
            if (string.IsNullOrWhiteSpace(component.ComponentKey))
                throw new ArgumentException("Utility component keys must be non-empty.", nameof(definition));
            if (component.Divisor <= 0 || !double.IsFinite(component.Divisor))
                throw new ArgumentOutOfRangeException(nameof(definition), $"Component '{component.ComponentKey}' divisor must be finite and positive.");
            if (component.MaximumContribution < 0 || !double.IsFinite(component.MaximumContribution))
                throw new ArgumentOutOfRangeException(nameof(definition), $"Component '{component.ComponentKey}' maximum contribution must be finite and non-negative.");
        }

        var componentKeys = definition.Components.Select(component => component.ComponentKey).ToHashSet(StringComparer.Ordinal);
        var duplicateThreshold = definition.Capabilities
            .GroupBy(capability => capability.ThresholdId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateThreshold is not null)
            throw new ArgumentException($"Duplicate capability threshold '{duplicateThreshold.Key}'.", nameof(definition));
        foreach (var capability in definition.Capabilities)
        {
            if (string.IsNullOrWhiteSpace(capability.ThresholdId) || string.IsNullOrWhiteSpace(capability.Label))
                throw new ArgumentException("Capability thresholds require an id and label.", nameof(definition));
            ArgumentNullException.ThrowIfNull(capability.Requirements);
            if (capability.Requirements.Count == 0)
                throw new ArgumentException($"Capability '{capability.ThresholdId}' requires at least one component threshold.", nameof(definition));
            var duplicateRequirement = capability.Requirements
                .GroupBy(requirement => requirement.ComponentKey, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateRequirement is not null)
                throw new ArgumentException($"Capability '{capability.ThresholdId}' repeats component '{duplicateRequirement.Key}'.", nameof(definition));
            foreach (var requirement in capability.Requirements)
            {
                if (!componentKeys.Contains(requirement.ComponentKey))
                    throw new ArgumentException($"Capability '{capability.ThresholdId}' references undeclared component '{requirement.ComponentKey}'.", nameof(definition));
                if (requirement.Minimum < 0)
                    throw new ArgumentOutOfRangeException(nameof(definition), $"Capability '{capability.ThresholdId}' minimum cannot be negative.");
            }
            if (capability.ScoreContribution <= 0 || !double.IsFinite(capability.ScoreContribution))
                throw new ArgumentOutOfRangeException(nameof(definition), $"Capability '{capability.ThresholdId}' contribution must be finite and positive.");
        }
    }
}
