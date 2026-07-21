using System.Buffers;
using System.Text.Json;

namespace Franthropy.Dalamud.Equipment;

public enum UpgradeAssessment
{
    ClearRegression,
    Equivalent,
    ClearImprovement,
    ContextDependent,
    Unsupported,
}

public enum EquipmentEvaluationConfidence
{
    Unknown,
    Low,
    Medium,
    High,
}

public sealed record EquipmentUtilityProfileKey(string ProfileId, string ProfileVersion);

public enum EquipmentUtilityRuleKind
{
    PreferMore,
    RequiredMinimum,
    ContextualOnly,
    Ignore,
}

public sealed record EquipmentUtilityRule(
    string RuleId,
    EquipmentStatSemantic Semantic,
    EquipmentUtilityRuleKind Kind,
    double Weight,
    double? Threshold,
    string Rationale);

/// <summary>
/// A versioned, inspectable starting rule set rather than a claim that job identity alone
/// proves utility. Supported contexts and evaluator diagnostics remain part of every result.
/// </summary>
public sealed record JobUtilityProfile(
    EquipmentUtilityProfileKey Key,
    string DisplayName,
    IReadOnlySet<uint> SupportedClassJobIds,
    IReadOnlySet<string> SupportedContextIds,
    IReadOnlyList<EquipmentUtilityRule> Rules,
    string CalibrationNotes);

/// <summary>
/// Decision context is explicit because a job alone does not determine equipment utility.
/// Context identifiers are owned by the consuming advisor and versioned independently of
/// the shared comparison machinery.
/// </summary>
public sealed record EquipmentUtilityContext(
    string ContextId,
    uint ClassJobId,
    uint CharacterLevel,
    string Scenario,
    IReadOnlyList<string> Tags);

public sealed record EquipmentUtilityUncertainty(
    double LowerBound,
    double UpperBound,
    IReadOnlyList<string> Reasons);

public sealed record EquipmentStatObservation(
    EquipmentStatSemantic Semantic,
    int Value,
    string Source);

public sealed record EquipmentStatContribution(
    EquipmentStatSemantic Semantic,
    int RawValue,
    double Weight,
    double Contribution,
    string Rationale);

public sealed record EquipmentUtilityThreshold(
    string ThresholdId,
    string Label,
    double? Minimum,
    double? Maximum,
    bool Satisfied,
    string Rationale);

/// <summary>
/// An explainable utility result. Franthropy stores the result and its inputs; it does not
/// claim that the score is a proof of optimal play or silently automate unsupported cases.
/// </summary>
public sealed record EquipmentUtilityEvaluation(
    EquipmentUtilityProfileKey Profile,
    EquipmentUtilityContext Context,
    double UtilityScore,
    EquipmentUtilityUncertainty Uncertainty,
    UpgradeAssessment Assessment,
    IReadOnlyList<EquipmentStatObservation> RawStats,
    IReadOnlyList<EquipmentStatContribution> Contributions,
    IReadOnlyList<EquipmentUtilityThreshold> Thresholds,
    EquipmentEvaluationConfidence Confidence,
    IReadOnlyList<string> Diagnostics);

public sealed record EquipmentLoadoutSelection(
    EquipmentLoadoutPosition Position,
    EquipmentOfferKey OfferKey,
    uint Quantity = 1,
    string? ObservationId = null)
{
    public EquipmentOfferAllocationKey AllocationKey => new(OfferKey, ObservationId);
}

public sealed record EquipmentOfferAllocationKey(
    EquipmentOfferKey OfferKey,
    string? ObservationId);

public sealed record EquipmentLoadoutCandidate(
    string SolutionId,
    IReadOnlyList<EquipmentLoadoutSelection> Selections);

/// <summary>Operational effort dimensions; every component is minimized.</summary>
public sealed record EquipmentAcquisitionBurden(
    int WorldVisits,
    int VendorStops,
    int PurchaseTransactions)
{
    public bool IsNoWorseThan(EquipmentAcquisitionBurden other) =>
        WorldVisits <= other.WorldVisits &&
        VendorStops <= other.VendorStops &&
        PurchaseTransactions <= other.PurchaseTransactions;

    public bool IsStrictlyBetterThan(EquipmentAcquisitionBurden other) =>
        WorldVisits < other.WorldVisits ||
        VendorStops < other.VendorStops ||
        PurchaseTransactions < other.PurchaseTransactions;
}

/// <summary>Evidence risk dimensions; lower values represent safer evidence.</summary>
public sealed record EquipmentEvidenceRisk(
    int FreshnessBucket,
    int IncompleteCoverageCount,
    int ConfidencePenalty)
{
    public bool IsNoWorseThan(EquipmentEvidenceRisk other) =>
        FreshnessBucket <= other.FreshnessBucket &&
        IncompleteCoverageCount <= other.IncompleteCoverageCount &&
        ConfidencePenalty <= other.ConfidencePenalty;

    public bool IsStrictlyBetterThan(EquipmentEvidenceRisk other) =>
        FreshnessBucket < other.FreshnessBucket ||
        IncompleteCoverageCount < other.IncompleteCoverageCount ||
        ConfidencePenalty < other.ConfidencePenalty;
}

public sealed record EquipmentAcquisitionCostEstimate(
    ulong OptimisticCostGil,
    ulong ExpectedCostGil,
    ulong PlanningCostGil,
    double PlanningConfidence,
    IReadOnlyList<string> Reasons);

public sealed record EquipmentDecisionSolution(
    EquipmentLoadoutCandidate Candidate,
    EquipmentUtilityEvaluation Utility,
    ulong AcquisitionCostGil,
    EquipmentAcquisitionBurden Burden,
    EquipmentEvidenceRisk EvidenceRisk,
    IReadOnlyList<string> VariantLabels,
    EquipmentAcquisitionCostEstimate? AcquisitionCostEstimate = null);

public sealed record EquipmentDominatedSolution(
    EquipmentDecisionSolution Solution,
    IReadOnlyList<string> DominatingSolutionIds);

public sealed record EquipmentEquivalentSolutions(
    string GroupId,
    IReadOnlyList<EquipmentDecisionSolution> Variants);

public sealed record EquipmentLoadoutPositionChange(
    EquipmentLoadoutPosition Position,
    EquipmentOfferKey? Before,
    EquipmentOfferKey? After,
    uint BeforeQuantity = 0,
    uint AfterQuantity = 0,
    string? BeforeObservationId = null,
    string? AfterObservationId = null);

public sealed record EquipmentLoadoutStructuralDiff(
    IReadOnlyList<EquipmentLoadoutPositionChange> Changes)
{
    public int ChangedPositionCount => Changes.Count;
}

public sealed record EquipmentParetoAdjacency(
    string FromSolutionId,
    string ToSolutionId,
    long CostDeltaGil,
    double UtilityDelta,
    EquipmentLoadoutStructuralDiff StructuralDiff);

public sealed record EquipmentParetoResult(
    IReadOnlyList<EquipmentDecisionSolution> Frontier,
    IReadOnlyList<EquipmentDominatedSolution> Dominated,
    IReadOnlyList<EquipmentEquivalentSolutions> EquivalenceGroups,
    IReadOnlyList<EquipmentParetoAdjacency> Adjacencies);

public static class EquipmentDecisionDominance
{
    private const double Epsilon = 1e-9;

    public static bool CanCompare(EquipmentDecisionSolution left, EquipmentDecisionSolution right) =>
        left.Utility.Profile == right.Utility.Profile &&
        string.Equals(left.Utility.Context.ContextId, right.Utility.Context.ContextId, StringComparison.Ordinal) &&
        left.Utility.Context.ClassJobId == right.Utility.Context.ClassJobId &&
        left.Utility.Context.CharacterLevel == right.Utility.Context.CharacterLevel;

    public static bool Dominates(EquipmentDecisionSolution candidate, EquipmentDecisionSolution other)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(other);
        if (!CanCompare(candidate, other))
            return false;

        var costNoWorse = candidate.AcquisitionCostGil <= other.AcquisitionCostGil;
        var utilityNoWorse = candidate.Utility.UtilityScore + Epsilon >= other.Utility.UtilityScore;
        var burdenNoWorse = candidate.Burden.IsNoWorseThan(other.Burden);
        var riskNoWorse = candidate.EvidenceRisk.IsNoWorseThan(other.EvidenceRisk);
        if (!costNoWorse || !utilityNoWorse || !burdenNoWorse || !riskNoWorse)
            return false;

        return candidate.AcquisitionCostGil < other.AcquisitionCostGil ||
            candidate.Utility.UtilityScore > other.Utility.UtilityScore + Epsilon ||
            candidate.Burden.IsStrictlyBetterThan(other.Burden) ||
            candidate.EvidenceRisk.IsStrictlyBetterThan(other.EvidenceRisk);
    }

    public static bool IsUtilityCostEquivalent(EquipmentDecisionSolution left, EquipmentDecisionSolution right) =>
        CanCompare(left, right) &&
        left.AcquisitionCostGil == right.AcquisitionCostGil &&
        Math.Abs(left.Utility.UtilityScore - right.Utility.UtilityScore) <= Epsilon;
}

/// <summary>
/// Computes a deterministic frontier over already-generated whole-loadout solutions. Candidate
/// generation and utility calibration intentionally remain separate concerns.
/// </summary>
public sealed class EquipmentParetoFrontierBuilder : IEquipmentParetoAdvisor
{
    public EquipmentParetoResult Build(IReadOnlyList<EquipmentDecisionSolution> solutions)
    {
        ArgumentNullException.ThrowIfNull(solutions);
        var ordered = solutions
            .OrderBy(solution => solution.AcquisitionCostGil)
            .ThenByDescending(solution => solution.Utility.UtilityScore)
            .ThenBy(solution => solution.Candidate.SolutionId, StringComparer.Ordinal)
            .ToArray();
        var solutionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var solution in ordered)
        {
            if (!solutionIds.Add(solution.Candidate.SolutionId))
                throw new ArgumentException($"Duplicate solution id '{solution.Candidate.SolutionId}'.", nameof(solutions));
        }

        var frontier = new List<EquipmentDecisionSolution>();
        var dominated = new List<EquipmentDominatedSolution>();
        var witnessBuffer = ArrayPool<string>.Shared.Rent(Math.Max(1, ordered.Length));
        var witnessCount = 0;
        try
        {
            foreach (var solution in ordered)
            {
                witnessCount = 0;
                foreach (var candidate in ordered)
                {
                    if (!ReferenceEquals(candidate, solution) && EquipmentDecisionDominance.Dominates(candidate, solution))
                        witnessBuffer[witnessCount++] = candidate.Candidate.SolutionId;
                }
                if (witnessCount == 0)
                    frontier.Add(solution);
                else
                {
                    var witnesses = new string[witnessCount];
                    Array.Copy(witnessBuffer, witnesses, witnessCount);
                    Array.Sort(witnesses, StringComparer.Ordinal);
                    dominated.Add(new(solution, witnesses));
                }
            }
        }
        finally
        {
            ArrayPool<string>.Shared.Return(witnessBuffer, clearArray: true);
        }

        var equivalence = BuildEquivalenceGroups(frontier);

        var adjacency = new List<EquipmentParetoAdjacency>();
        for (var index = 1; index < frontier.Count; index++)
        {
            var before = frontier[index - 1];
            var after = frontier[index];
            if (!EquipmentDecisionDominance.CanCompare(before, after))
                continue;
            adjacency.Add(new(
                before.Candidate.SolutionId,
                after.Candidate.SolutionId,
                checked((long)after.AcquisitionCostGil - (long)before.AcquisitionCostGil),
                after.Utility.UtilityScore - before.Utility.UtilityScore,
                Diff(before.Candidate, after.Candidate)));
        }

        return new(frontier, dominated, equivalence, adjacency);
    }

    public static EquipmentLoadoutStructuralDiff Diff(
        EquipmentLoadoutCandidate before,
        EquipmentLoadoutCandidate after)
    {
        var beforeByPosition = before.Selections.ToDictionary(selection => selection.Position);
        var afterByPosition = after.Selections.ToDictionary(selection => selection.Position);
        var changes = beforeByPosition.Keys
            .Union(afterByPosition.Keys)
            .OrderBy(position => position)
            .Select(position => new EquipmentLoadoutPositionChange(
                position,
                beforeByPosition.GetValueOrDefault(position)?.OfferKey,
                afterByPosition.GetValueOrDefault(position)?.OfferKey,
                beforeByPosition.GetValueOrDefault(position)?.Quantity ?? 0,
                afterByPosition.GetValueOrDefault(position)?.Quantity ?? 0,
                beforeByPosition.GetValueOrDefault(position)?.ObservationId,
                afterByPosition.GetValueOrDefault(position)?.ObservationId))
            .Where(change => change.Before != change.After ||
                change.BeforeQuantity != change.AfterQuantity ||
                !string.Equals(change.BeforeObservationId, change.AfterObservationId, StringComparison.Ordinal))
            .ToArray();
        return new(changes);
    }

    private static IReadOnlyList<EquipmentEquivalentSolutions> BuildEquivalenceGroups(
        IReadOnlyList<EquipmentDecisionSolution> frontier)
    {
        var remaining = new HashSet<string>(frontier.Select(value => value.Candidate.SolutionId), StringComparer.Ordinal);
        var groups = new List<EquipmentEquivalentSolutions>();
        foreach (var seed in frontier)
        {
            if (!remaining.Remove(seed.Candidate.SolutionId))
                continue;
            var variants = frontier
                .Where(candidate => remaining.Contains(candidate.Candidate.SolutionId))
                .Where(candidate => EquipmentDecisionDominance.IsUtilityCostEquivalent(seed, candidate))
                .Prepend(seed)
                .OrderBy(candidate => candidate.Candidate.SolutionId, StringComparer.Ordinal)
                .ToArray();
            foreach (var variant in variants)
                remaining.Remove(variant.Candidate.SolutionId);
            if (variants.Length > 1)
                groups.Add(new(
                    $"equivalent:{string.Join("+", variants.Select(solution => solution.Candidate.SolutionId))}",
                    variants));
        }
        return groups;
    }
}

public sealed record EquipmentDecisionReplay(
    Guid EvidenceGenerationId,
    DateTimeOffset CapturedAt,
    IReadOnlyList<EquipmentDecisionSolution> Solutions,
    string SchemaVersion = "franthropy-equipment-decision/v1");

public interface IEquipmentUtilityEvaluator
{
    EquipmentUtilityEvaluation Evaluate(
        JobUtilityProfile profile,
        EquipmentUtilityContext context,
        EquipmentLoadoutCandidate candidate,
        IReadOnlyList<EquipmentStatObservation> stats);
}

public interface IEquipmentParetoAdvisor
{
    EquipmentParetoResult Build(IReadOnlyList<EquipmentDecisionSolution> solutions);
}

public static class EquipmentDecisionReplayJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string Serialize(EquipmentDecisionReplay replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        if (FindUnsupportedSourceKind(replay) is { } unsupported)
            throw new ArgumentException($"Equipment decision replay has unsupported acquisition source '{unsupported}'.", nameof(replay));
        return JsonSerializer.Serialize(Normalize(replay), Options);
    }

    public static EquipmentDecisionReplay Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var replay = JsonSerializer.Deserialize<EquipmentDecisionReplay>(json, Options)
            ?? throw new JsonException("Equipment decision replay was empty.");
        if (!string.Equals(replay.SchemaVersion, "franthropy-equipment-decision/v1", StringComparison.Ordinal))
            throw new JsonException($"Unsupported equipment decision replay schema '{replay.SchemaVersion}'.");
        if (FindUnsupportedSourceKind(replay) is { } unsupported)
            throw new JsonException($"Equipment decision replay has unsupported acquisition source '{unsupported}'.");
        return Normalize(replay);
    }

    public static EquipmentDecisionReplay Normalize(EquipmentDecisionReplay replay) => replay with
    {
        Solutions = replay.Solutions
            .Select(Normalize)
            .OrderBy(solution => solution.Candidate.SolutionId, StringComparer.Ordinal)
            .ToArray(),
    };

    private static EquipmentDecisionSolution Normalize(EquipmentDecisionSolution solution) => solution with
    {
        Candidate = solution.Candidate with
        {
            Selections = solution.Candidate.Selections
                .OrderBy(selection => selection.Position)
                .ThenBy(selection => selection.OfferKey.ItemId)
                .ThenBy(selection => selection.OfferKey.Quality)
                .ThenBy(selection => selection.OfferKey.SourceKind)
                .ThenBy(selection => selection.OfferKey.SourceCatalogKey, StringComparer.Ordinal)
                .ThenBy(selection => selection.ObservationId, StringComparer.Ordinal)
                .ToArray(),
        },
        Utility = solution.Utility with
        {
            Context = solution.Utility.Context with
            {
                Tags = solution.Utility.Context.Tags.Order(StringComparer.Ordinal).ToArray(),
            },
            Uncertainty = solution.Utility.Uncertainty with
            {
                Reasons = solution.Utility.Uncertainty.Reasons.Order(StringComparer.Ordinal).ToArray(),
            },
            RawStats = solution.Utility.RawStats
                .OrderBy(stat => stat.Semantic)
                .ThenBy(stat => stat.Source, StringComparer.Ordinal)
                .ToArray(),
            Contributions = solution.Utility.Contributions
                .OrderBy(contribution => contribution.Semantic)
                .ThenBy(contribution => contribution.Rationale, StringComparer.Ordinal)
                .ToArray(),
            Thresholds = solution.Utility.Thresholds
                .OrderBy(threshold => threshold.ThresholdId, StringComparer.Ordinal)
                .ToArray(),
            Diagnostics = solution.Utility.Diagnostics.Order(StringComparer.Ordinal).ToArray(),
        },
        VariantLabels = solution.VariantLabels.Order(StringComparer.Ordinal).ToArray(),
        AcquisitionCostEstimate = solution.AcquisitionCostEstimate is null
            ? null
            : solution.AcquisitionCostEstimate with
            {
                Reasons = solution.AcquisitionCostEstimate.Reasons.Order(StringComparer.Ordinal).ToArray(),
            },
    };

    private static EquipmentAcquisitionSourceKind? FindUnsupportedSourceKind(EquipmentDecisionReplay replay)
    {
        foreach (var sourceKind in replay.Solutions
            .SelectMany(solution => solution.Candidate.Selections)
            .Select(selection => selection.OfferKey.SourceKind))
        {
            if (!Enum.IsDefined(sourceKind))
                return sourceKind;
        }
        return null;
    }
}
