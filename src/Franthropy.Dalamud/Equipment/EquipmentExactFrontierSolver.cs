using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;

namespace Franthropy.Dalamud.Equipment;

public sealed record EquipmentSolverUtilityComponent(string Key, long Units);

/// <summary>
/// Deterministic monotonic benefit dimensions carried through the exact solver. Components are
/// not the final universal score; the contextual utility model evaluates the completed vector.
/// </summary>
public sealed record EquipmentSolverUtilityVector(IReadOnlyList<EquipmentSolverUtilityComponent> Components)
{
    public static EquipmentSolverUtilityVector Empty { get; } = new([]);

    public EquipmentSolverUtilityVector Normalize()
    {
        var alreadyNormalized = true;
        string? previousKey = null;
        foreach (var component in Components)
        {
            if (string.IsNullOrWhiteSpace(component.Key) || component.Units <= 0 ||
                previousKey is not null && string.CompareOrdinal(previousKey, component.Key) >= 0)
            {
                alreadyNormalized = false;
                break;
            }
            previousKey = component.Key;
        }
        if (alreadyNormalized)
            return this;
        var normalized = Components
            .GroupBy(component => component.Key, StringComparer.Ordinal)
            .Select(group => new EquipmentSolverUtilityComponent(
                group.Key,
                group.Aggregate(0L, (sum, component) => checked(sum + component.Units))))
            .Where(component => component.Units != 0)
            .OrderBy(component => component.Key, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Any(component => string.IsNullOrWhiteSpace(component.Key)))
            throw new InvalidOperationException("Utility component keys must be non-empty.");
        if (normalized.Any(component => component.Units < 0))
            throw new InvalidOperationException("Solver utility components must be monotonic benefits with non-negative units.");
        return new(normalized);
    }

    public EquipmentSolverUtilityVector Add(EquipmentSolverUtilityVector other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var left = Normalize().Components;
        var right = other.Normalize().Components;
        var merged = new List<EquipmentSolverUtilityComponent>(left.Count + right.Count);
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Count || rightIndex < right.Count)
        {
            if (rightIndex >= right.Count || leftIndex < left.Count && string.CompareOrdinal(left[leftIndex].Key, right[rightIndex].Key) < 0)
            {
                merged.Add(left[leftIndex++]);
                continue;
            }
            if (leftIndex >= left.Count || string.CompareOrdinal(right[rightIndex].Key, left[leftIndex].Key) < 0)
            {
                merged.Add(right[rightIndex++]);
                continue;
            }
            merged.Add(new(left[leftIndex].Key, checked(left[leftIndex].Units + right[rightIndex].Units)));
            leftIndex++;
            rightIndex++;
        }
        return new(merged);
    }

    public long Get(string key) => Components.FirstOrDefault(component => string.Equals(component.Key, key, StringComparison.Ordinal))?.Units ?? 0;

    public string CanonicalText => string.Join('|', Normalize().Components.Select(component => $"{component.Key}:{component.Units}"));
}

public readonly record struct EquipmentPartialUtilityDominance(
    bool IsNoWorse,
    bool IsStrictlyBetter);

public interface IEquipmentExactSolverUtilityModel
{
    EquipmentPartialUtilityDominance ComparePartial(
        EquipmentSolverUtilityVector candidate,
        EquipmentSolverUtilityVector other);

    EquipmentUtilityEvaluation Evaluate(EquipmentSolverUtilityVector completed);
}

/// <summary>
/// Optional exact coordinates for dominance indexing. Implementations promise that partial
/// utility is no worse exactly when every returned coordinate is greater than or equal to the
/// corresponding coordinate. The solver still calls ComparePartial before accepting dominance.
/// </summary>
public interface IEquipmentPartialDominanceCoordinateModel
{
    IReadOnlyList<long> GetPartialDominanceCoordinates(EquipmentSolverUtilityVector utility);
}

/// <summary>
/// Optional proof that partial utility above a contextual ceiling is evaluation-equivalent.
/// Canonicalization may clamp only dimensions whose remaining contribution and capability
/// thresholds are already saturated; it must not approximate or rank by item level.
/// </summary>
public interface IEquipmentPartialUtilityCanonicalizationModel
{
    EquipmentSolverUtilityVector CanonicalizePartialUtility(EquipmentSolverUtilityVector utility);
}

public interface IEquipmentSeparablePartialUtilityCanonicalizationModel : IEquipmentPartialUtilityCanonicalizationModel
{
    long CanonicalizePartialUtilityComponent(string componentKey, long units);
}

public sealed record EquipmentExactSolverOffer(
    EquipmentLoadoutOffer Offer,
    string? ObservationId,
    IReadOnlySet<EquipmentLoadoutPosition> Positions,
    uint AvailableQuantity,
    EquipmentSolverUtilityVector Utility,
    ulong AcquisitionCostGil,
    string? WorldVisitKey,
    string? VendorStopKey,
    int PurchaseTransactions,
    EquipmentEvidenceRisk EvidenceRisk,
    IReadOnlyList<string> VariantLabels)
{
    private static readonly ConditionalWeakTable<EquipmentExactSolverOffer, EquipmentOfferAllocationKey> AllocationKeys = new();

    public EquipmentOfferAllocationKey AllocationKey => AllocationKeys.GetValue(
        this,
        value => new(value.Offer.Key, value.ObservationId));
}

public sealed record EquipmentExactFrontierRequest(
    IReadOnlyList<EquipmentExactSolverOffer> Offers,
    IReadOnlySet<EquipmentLoadoutPosition> RequiredPositions,
    IReadOnlyDictionary<EquipmentLoadoutPosition, EquipmentOfferAllocationKey?> Baseline,
    IEquipmentExactSolverUtilityModel UtilityModel,
    int MaxRetainedRepresentatives = 16);

public sealed record EquipmentExactFrontierDiagnostics(
    long ExpandedStateCount,
    long InfeasibleTransitionCount,
    long DominatedStateCount,
    long CompactedEquivalentStateCount,
    int PeakRetainedStateCount,
    int CompleteSolutionCount,
    long RetainedCompletePathCount,
    int RetainedRepresentativeLimit,
    string BaselineSolutionId,
    TimeSpan Elapsed,
    int InputOfferCount = 0,
    int RetainedOfferChoiceCount = 0,
    int RetainedOfferVariantCount = 0);

public sealed record EquipmentExactFrontierProgress(
    int CompletedPositionCount,
    int TotalPositionCount,
    EquipmentLoadoutPosition Position,
    long ExpandedStateCount,
    long DominatedStateCount,
    long CompactedEquivalentStateCount,
    int CandidateStateCount,
    int RetainedStateCount,
    TimeSpan Elapsed,
    string Phase = "PositionComplete",
    bool UsesFastResourceIndex = false,
    int ProcessedCandidateCount = 0,
    int ResourceBucketCount = 0,
    long DominanceStateVisitCount = 0,
    int InputOfferCount = 0,
    int RetainedOfferChoiceCount = 0,
    int RetainedOfferVariantCount = 0);

/// <summary>
/// Diagnostic lineage for complete selection paths retained by the solver's documented canonical
/// position traversal after partial dominance. A retained path is one exact offer/allocation choice
/// per occupied position that survives every prefix-pruning step. RetainedPathCount is the number of
/// such paths compacted into the terminal metric/resource class; it deliberately excludes feasible
/// histories discarded before terminal world/vendor reuse can make them equivalent and therefore is
/// not a count of all feasible terminal variants. Representatives are the ordinally smallest
/// executable solution IDs among those retained paths, bounded by MaxRetainedRepresentatives.
/// </summary>
public sealed record EquipmentRetainedEquivalenceSummary(
    string ClassId,
    long RetainedPathCount,
    IReadOnlyList<string> RetainedRepresentativeSolutionIds)
{
    public bool RetainedRepresentativesTruncated => RetainedPathCount > RetainedRepresentativeSolutionIds.Count;
}

public sealed record EquipmentExactFrontierResult(
    EquipmentParetoResult Pareto,
    EquipmentExactFrontierDiagnostics Diagnostics,
    IReadOnlyList<EquipmentRetainedEquivalenceSummary> RetainedEquivalenceSummaries);

/// <summary>
/// Exact incremental frontier solver. It never materializes a Cartesian product: each position
/// expands only the retained Pareto states, and pruning is allowed only when the utility model
/// guarantees partial dominance and the candidate consumes no additional future resource.
/// </summary>
public sealed class EquipmentExactFrontierSolver
{
    private static readonly EquipmentLoadoutPosition[] CanonicalPositions =
    [
        EquipmentLoadoutPosition.MainHand,
        EquipmentLoadoutPosition.OffHand,
        EquipmentLoadoutPosition.Head,
        EquipmentLoadoutPosition.Body,
        EquipmentLoadoutPosition.Hands,
        EquipmentLoadoutPosition.Legs,
        EquipmentLoadoutPosition.Feet,
        EquipmentLoadoutPosition.Ears,
        EquipmentLoadoutPosition.Neck,
        EquipmentLoadoutPosition.Wrists,
        EquipmentLoadoutPosition.LeftRing,
        EquipmentLoadoutPosition.RightRing,
    ];

    public EquipmentExactFrontierResult Solve(
        EquipmentExactFrontierRequest request,
        CancellationToken cancellationToken = default,
        Action<EquipmentExactFrontierProgress>? reportProgress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.UtilityModel);
        Validate(request);
        var stopwatch = Stopwatch.StartNew();
        var positions = CanonicalPositions.Where(request.RequiredPositions.Contains).ToArray();
        var orderedOffers = request.Offers
            .Select(offer => offer with { Utility = offer.Utility.Normalize() })
            .OrderBy(offer => offer.Offer.Key.ItemId)
            .ThenBy(offer => offer.Offer.Key.Quality)
            .ThenBy(offer => offer.Offer.Key.SourceKind)
            .ThenBy(offer => offer.Offer.Key.SourceCatalogKey, StringComparer.Ordinal)
            .ThenBy(offer => offer.ObservationId, StringComparer.Ordinal)
            .ToArray();
        ValidateBaseline(request, orderedOffers);
        var canonicalizationModel = request.UtilityModel as IEquipmentPartialUtilityCanonicalizationModel;
        var utilities = new RequestUtilityPool(canonicalizationModel, orderedOffers.Select(offer => offer.Utility));
        var solverOffers = orderedOffers.Select(offer => offer with
        {
            Utility = utilities.Intern(offer.Utility),
        }).ToArray();
        var resources = new RequestResourcePool(solverOffers, positions);
        var offersByPosition = positions.ToDictionary(
            position => position,
            position => BuildExactOfferFrontier(
                solverOffers.Where(offer => offer.Positions.Contains(position)).ToArray(),
                position,
                request.Baseline[position],
                request.UtilityModel,
                resources));
        var retainedOfferChoiceCount = offersByPosition.Values.Sum(value => value.Length);
        var retainedOfferVariantCount = offersByPosition.Values.Sum(value => value.Sum(choice => choice.EquivalentOffers.Count));
        reportProgress?.Invoke(new(
            0,
            positions.Length,
            positions[0],
            0,
            0,
            0,
            0,
            0,
            stopwatch.Elapsed,
            "OfferReduction",
            InputOfferCount: orderedOffers.Length,
            RetainedOfferChoiceCount: retainedOfferChoiceCount,
            RetainedOfferVariantCount: retainedOfferVariantCount));
        long expanded = 0;
        long infeasible = 0;
        long dominated = 0;
        long compacted = 0;
        var peak = 1;
        var states = new List<State> { State.Empty(resources.Empty, utilities.Empty) };
        for (var positionIndex = 0; positionIndex < positions.Length; positionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = positions[positionIndex];
            var next = new List<State>();
            foreach (var state in states)
            {
                if (position == EquipmentLoadoutPosition.OffHand && state.Resources.MainHandOccupiesOffHand)
                {
                    expanded++;
                    next.Add(state with
                    {
                        IsBaselineSoFar = state.IsBaselineSoFar && request.Baseline[position] is null,
                    });
                    continue;
                }

                foreach (var choice in offersByPosition[position])
                {
                    expanded++;
                    if (!resources.TryAllocate(state.Resources, position, choice.Primary, out var allocatedResources))
                    {
                        infeasible++;
                        continue;
                    }
                    next.Add(Allocate(state, choice, request.Baseline[position], allocatedResources, utilities));
                }
            }
            if (next.Count == 0)
                throw new InvalidOperationException($"No feasible loadout state can fill {position}.");

            var remainingPositions = positions.Skip(positionIndex + 1).ToArray();
            var futureResources = resources.CreateProjection(remainingPositions);
            for (var stateIndex = 0; stateIndex < next.Count; stateIndex++)
            {
                var state = next[stateIndex];
                var projectedResources = resources.Project(state.Resources, futureResources);
                if (!ReferenceEquals(projectedResources, state.Resources))
                    next[stateIndex] = state with { Resources = projectedResources };
            }
            reportProgress?.Invoke(new(
                positionIndex,
                positions.Length,
                position,
                expanded,
                dominated,
                compacted,
                next.Count,
                states.Count,
                stopwatch.Elapsed,
                "Pruning",
                next.All(state => state.Resources.TryGetFastResourceKey(futureResources, out _))));
            states = cancellationToken.CanBeCanceled
                ? PruneCancellable(
                    next,
                    request.UtilityModel,
                    futureResources,
                    request.MaxRetainedRepresentatives,
                    cancellationToken,
                    (processed, retained, index) => reportProgress?.Invoke(new(
                        positionIndex,
                        positions.Length,
                        position,
                        expanded,
                        dominated,
                        compacted,
                        next.Count,
                        retained,
                        stopwatch.Elapsed,
                        "Pruning",
                        index?.UsesFastResources ?? false,
                        processed,
                        index?.ResourceBucketCount ?? 0,
                        index?.DominanceStateVisitCount ?? 0)),
                    ref dominated,
                    ref compacted)
                : Prune(
                    next,
                    request.UtilityModel,
                    futureResources,
                    request.MaxRetainedRepresentatives,
                    ref dominated,
                    ref compacted);
            peak = Math.Max(peak, states.Count);
            reportProgress?.Invoke(new(
                positionIndex + 1,
                positions.Length,
                position,
                expanded,
                dominated,
                compacted,
                next.Count,
                states.Count,
                stopwatch.Elapsed));
        }

        var feasibilityOffers = orderedOffers.ToDictionary(
            offer => offer.AllocationKey,
            offer => new EquipmentFeasibilityOffer(offer.Offer, offer.AvailableQuantity, offer.ObservationId));
        var feasibilityEvaluator = new EquipmentLoadoutFeasibilityEvaluator();
        var offersByAllocation = orderedOffers.ToDictionary(offer => offer.AllocationKey);
        var primaryDecisions = new List<EquipmentDecisionSolution>(states.Count);
        var statesByPrimaryId = new Dictionary<string, State>(StringComparer.Ordinal);
        var equivalenceSummaries = new List<EquipmentRetainedEquivalenceSummary>();
        var materializations = new List<FinalStateMaterialization>(states.Count);
        foreach (var state in states)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var representatives = state.Paths.GetCanonicalRepresentatives(request.MaxRetainedRepresentatives);
            var representativeIds = representatives
                .Select(selections => state.IsBaselineSoFar ? "baseline" : SolutionId(selections))
                .ToArray();
            materializations.Add(new(state, representatives, representativeIds));
            if (state.Paths.RetainedPathCount > 1)
                equivalenceSummaries.Add(new(
                    $"retained:{CanonicalMetricText(state)}",
                    state.Paths.RetainedPathCount,
                    representativeIds));
        }
        ReportFinalizationProgress("RepresentativesMaterialized", materializations.Count);
        RetainedPathNode.ReleaseIntermediateRepresentativeCaches(materializations.Select(value => value.State.Paths));
        foreach (var materialization in materializations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var primary = Materialize(
                materialization.State,
                materialization.Representatives[0],
                materialization.RepresentativeIds[0],
                request,
                feasibilityEvaluator,
                feasibilityOffers,
                offersByAllocation);
            primaryDecisions.Add(primary);
            statesByPrimaryId[primary.Candidate.SolutionId] = materialization.State;
        }
        ReportFinalizationProgress("PrimaryMaterialized", primaryDecisions.Count);

        if (primaryDecisions.All(solution => !string.Equals(solution.Candidate.SolutionId, "baseline", StringComparison.Ordinal)))
            throw new InvalidOperationException("The no-purchase baseline was not preserved through frontier generation.");
        var corePareto = new EquipmentParetoFrontierBuilder().Build(primaryDecisions);
        ReportFinalizationProgress("ParetoBuilt", corePareto.Frontier.Count);
        var expandedFrontier = new List<EquipmentDecisionSolution>();
        foreach (var core in corePareto.Frontier)
        {
            var state = statesByPrimaryId[core.Candidate.SolutionId];
            foreach (var selections in state.Paths.GetCanonicalRepresentatives(request.MaxRetainedRepresentatives))
            {
                var solutionId = state.IsBaselineSoFar ? "baseline" : SolutionId(selections);
                if (string.Equals(solutionId, core.Candidate.SolutionId, StringComparison.Ordinal))
                {
                    expandedFrontier.Add(core);
                    continue;
                }
                expandedFrontier.Add(Materialize(
                    state,
                    selections,
                    solutionId,
                    request,
                    feasibilityEvaluator,
                    feasibilityOffers,
                    offersByAllocation));
            }
        }
        ReportFinalizationProgress("FrontierMaterialized", expandedFrontier.Count);
        var pareto = new EquipmentParetoResult(
            expandedFrontier,
            corePareto.Dominated,
            BuildExplicitEquivalenceGroups(expandedFrontier),
            corePareto.Adjacencies);
        var normalizedEquivalenceSummaries = equivalenceSummaries
            .GroupBy(summary => summary.ClassId, StringComparer.Ordinal)
            .Select(group => new EquipmentRetainedEquivalenceSummary(
                group.Key,
                group.Aggregate(0L, (sum, summary) => checked(sum + summary.RetainedPathCount)),
                group.SelectMany(summary => summary.RetainedRepresentativeSolutionIds)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .Take(request.MaxRetainedRepresentatives)
                    .ToArray()))
            .ToArray();
        ReportFinalizationProgress("Finalized", expandedFrontier.Count);
        stopwatch.Stop();
        return new(
            pareto,
            new(
                expanded,
                infeasible,
                dominated,
                compacted,
                peak,
                expandedFrontier.Count + corePareto.Dominated.Count,
                states.Sum(state => state.Paths.RetainedPathCount),
                request.MaxRetainedRepresentatives,
                "baseline",
                stopwatch.Elapsed,
                orderedOffers.Length,
                retainedOfferChoiceCount,
                retainedOfferVariantCount),
            normalizedEquivalenceSummaries);

        void ReportFinalizationProgress(string phase, int retainedCount) => reportProgress?.Invoke(new(
            positions.Length,
            positions.Length,
            positions[^1],
            expanded,
            dominated,
            compacted,
            states.Count,
            retainedCount,
            stopwatch.Elapsed,
            phase));
    }

    private static EquipmentDecisionSolution Materialize(
        State state,
        IReadOnlyList<EquipmentLoadoutSelection> selections,
        string solutionId,
        EquipmentExactFrontierRequest request,
        EquipmentLoadoutFeasibilityEvaluator feasibilityEvaluator,
        IReadOnlyDictionary<EquipmentOfferAllocationKey, EquipmentFeasibilityOffer> feasibilityOffers,
        IReadOnlyDictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer> offersByAllocation)
    {
        var candidate = new EquipmentLoadoutCandidate(solutionId, selections);
        var feasibility = feasibilityEvaluator.Evaluate(
            candidate,
            feasibilityOffers,
            request.RequiredPositions);
        if (!feasibility.IsFeasible)
            throw new InvalidOperationException($"Solver emitted infeasible solution '{solutionId}': {string.Join("; ", feasibility.Violations.Select(value => value.Message))}");
        var labels = selections
            .Select(selection => offersByAllocation[selection.AllocationKey])
            .SelectMany(offer => offer.VariantLabels)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var exactUtility = selections
            .Select(selection => offersByAllocation[selection.AllocationKey].Utility)
            .Aggregate(EquipmentSolverUtilityVector.Empty, (sum, utility) => sum.Add(utility));
        var evaluation = request.UtilityModel.Evaluate(exactUtility);
        if (!double.IsFinite(evaluation.UtilityScore))
            throw new InvalidOperationException("Exact solver utility models must emit finite deterministic utility scores.");
        return new(
            candidate,
            evaluation,
            state.Cost,
            new(state.Resources.WorldVisitCount, state.Resources.VendorStopCount, state.PurchaseTransactions),
            state.EvidenceRisk,
            labels);
    }

    private static IReadOnlyList<EquipmentEquivalentSolutions> BuildExplicitEquivalenceGroups(
        IReadOnlyList<EquipmentDecisionSolution> frontier) => frontier
        .GroupBy(solution => new ExplicitEquivalenceKey(
            solution.Utility.Profile.ProfileId,
            solution.Utility.Profile.ProfileVersion,
            solution.Utility.Context.ContextId,
            solution.Utility.Context.ClassJobId,
            solution.Utility.Context.CharacterLevel,
            solution.AcquisitionCostGil,
            checked((long)solution.Utility.UtilityScore)))
        .Select(group => group.OrderBy(solution => solution.Candidate.SolutionId, StringComparer.Ordinal).ToArray())
        .Where(group => group.Length > 1)
        .Select(group => new EquipmentEquivalentSolutions(
            $"equivalent:{string.Join("+", group.Select(solution => solution.Candidate.SolutionId))}",
            group))
        .ToArray();

    private static List<State> Prune(
        IReadOnlyList<State> candidates,
        IEquipmentExactSolverUtilityModel utilityModel,
        ResourceProjection futureResources,
        int maxRetainedRepresentatives,
        ref long dominatedCount,
        ref long compactedCount)
    {
        var coordinateModel = utilityModel as IEquipmentPartialDominanceCoordinateModel;
        var ordered = OrderForDominancePruning(candidates, coordinateModel, futureResources);
        var retained = new List<State>(ordered.Length);
        var dominanceIndex = coordinateModel is null ? null : new ExactDominanceIndex(coordinateModel, futureResources, ordered);
        foreach (var candidate in ordered)
        {
            var isDominated = !candidate.IsBaselineSoFar && (dominanceIndex is not null
                ? dominanceIndex.AnyDominates(candidate, utilityModel)
                : retained.Any(other => DominatesPartial(other, candidate, utilityModel, futureResources)));
            if (isDominated)
            {
                dominatedCount++;
                continue;
            }

            if (dominanceIndex is null)
            {
                for (var retainedIndex = retained.Count - 1; retainedIndex >= 0; retainedIndex--)
                {
                    if (retained[retainedIndex].IsBaselineSoFar)
                        continue;
                    if (DominatesPartial(candidate, retained[retainedIndex], utilityModel, futureResources))
                    {
                        retained.RemoveAt(retainedIndex);
                        dominatedCount++;
                    }
                }
            }
            retained.Add(candidate);
            if (dominanceIndex is not null)
                dominanceIndex.Add(candidate);
        }
        return CompactEquivalent(retained, futureResources, maxRetainedRepresentatives, ref compactedCount);
    }

    private static List<State> PruneCancellable(
        IReadOnlyList<State> candidates,
        IEquipmentExactSolverUtilityModel utilityModel,
        ResourceProjection futureResources,
        int maxRetainedRepresentatives,
        CancellationToken cancellationToken,
        Action<int, int, ExactDominanceIndex?>? reportPruningProgress,
        ref long dominatedCount,
        ref long compactedCount)
    {
        var coordinateModel = utilityModel as IEquipmentPartialDominanceCoordinateModel;
        var ordered = OrderForDominancePruning(candidates, coordinateModel, futureResources);
        var retained = new List<State>(ordered.Length);
        var dominanceIndex = coordinateModel is null ? null : new ExactDominanceIndex(
            coordinateModel,
            futureResources,
            ordered,
            cancellationToken);
        for (var candidateIndex = 0; candidateIndex < ordered.Length; candidateIndex++)
        {
            if ((candidateIndex & 4095) == 0)
                reportPruningProgress?.Invoke(candidateIndex, retained.Count, dominanceIndex);
            if (cancellationToken.CanBeCanceled)
                cancellationToken.ThrowIfCancellationRequested();
            var candidate = ordered[candidateIndex];
            var isDominated = !candidate.IsBaselineSoFar && (dominanceIndex is not null
                ? dominanceIndex.AnyDominates(candidate, utilityModel, cancellationToken)
                : cancellationToken.CanBeCanceled
                    ? AnyDominatesCancellable(
                    retained,
                    candidate,
                    utilityModel,
                    futureResources,
                    cancellationToken)
                    : retained.Any(other => DominatesPartial(other, candidate, utilityModel, futureResources)));
            if (isDominated)
            {
                dominatedCount++;
                continue;
            }

            if (dominanceIndex is not null)
            {
                // Coordinate-sum/resource-burden ordering is topological for DominatesPartial:
                // a later state cannot dominate an earlier state, so eviction is unnecessary.
            }
            else if (!cancellationToken.CanBeCanceled)
            {
                for (var index = retained.Count - 1; index >= 0; index--)
                {
                    if (retained[index].IsBaselineSoFar)
                        continue;
                    if (DominatesPartial(candidate, retained[index], utilityModel, futureResources))
                    {
                        retained.RemoveAt(index);
                        dominatedCount++;
                    }
                }
            }
            else
            {
                for (var index = retained.Count - 1; index >= 0; index--)
                {
                    if ((index & 255) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    if (retained[index].IsBaselineSoFar)
                        continue;
                    if (DominatesPartial(candidate, retained[index], utilityModel, futureResources))
                    {
                        retained.RemoveAt(index);
                        dominatedCount++;
                    }
                }
            }
            retained.Add(candidate);
            if (dominanceIndex is not null)
                dominanceIndex.Add(candidate);
        }
        reportPruningProgress?.Invoke(ordered.Length, retained.Count, dominanceIndex);
        return CompactEquivalentCancellable(
            retained,
            futureResources,
            maxRetainedRepresentatives,
            cancellationToken,
            ref compactedCount);
    }

    private static bool AnyDominatesCancellable(
        IReadOnlyList<State> retained,
        State candidate,
        IEquipmentExactSolverUtilityModel utilityModel,
        ResourceProjection futureResources,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < retained.Count; index++)
        {
            if ((index & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (DominatesPartial(retained[index], candidate, utilityModel, futureResources))
                return true;
        }
        return false;
    }

    private static State[] OrderForDominancePruning(
        IReadOnlyList<State> candidates,
        IEquipmentPartialDominanceCoordinateModel? coordinateModel,
        ResourceProjection futureResources) => candidates
        .OrderByDescending(state => coordinateModel is null
            ? UtilityMagnitude(state)
            : CoordinateMagnitude(coordinateModel.GetPartialDominanceCoordinates(state.Utility)))
        .ThenBy(state => state.Cost)
        .ThenBy(state => state.EvidenceRisk.FreshnessBucket)
        .ThenBy(state => state.EvidenceRisk.IncompleteCoverageCount)
        .ThenBy(state => state.EvidenceRisk.ConfidencePenalty)
        .ThenBy(state => state.PurchaseTransactions)
        .ThenBy(state => state.Resources.WorldVisitCount)
        .ThenBy(state => state.Resources.VendorStopCount)
        .ThenBy(state => state.Resources.ActiveAllocationCount(futureResources.AllocationIndices))
        .ThenBy(state => state.Resources.UniqueItems.CountMasked(futureResources.UniqueMask))
        .ThenBy(state => futureResources.KeepMainHandOccupancy && state.Resources.MainHandOccupiesOffHand)
        .ToArray();

    private static long UtilityMagnitude(State state) => state.Utility.Components.Aggregate(
        0L,
        (sum, component) => checked(sum + component.Units));

    private static long CoordinateMagnitude(IReadOnlyList<long> coordinates) => coordinates.Aggregate(
        0L,
        (sum, coordinate) => checked(sum + coordinate));

    private static List<State> CompactEquivalent(
        IReadOnlyList<State> states,
        ResourceProjection futureResources,
        int maxRetainedRepresentatives,
        ref long compactedCount)
    {
        var compacted = new List<State>();
        foreach (var group in states.GroupBy(BuildStateEquivalenceKey))
        {
            var grouped = group.ToArray();
            compacted.Add(grouped[0].WithPaths(
                RetainedPathNode.Union(grouped.Select(state => state.Paths))));
            compactedCount += grouped.Length - 1;
        }
        return compacted;
    }

    private static List<State> CompactEquivalentCancellable(
        IReadOnlyList<State> states,
        ResourceProjection futureResources,
        int maxRetainedRepresentatives,
        CancellationToken cancellationToken,
        ref long compactedCount)
    {
        var compacted = new List<State>();
        foreach (var group in states.GroupBy(BuildStateEquivalenceKey))
        {
            if (cancellationToken.CanBeCanceled)
                cancellationToken.ThrowIfCancellationRequested();
            var grouped = group.ToArray();
            compacted.Add(grouped[0].WithPaths(
                RetainedPathNode.Union(grouped.Select(state => state.Paths))));
            compactedCount += grouped.Length - 1;
        }
        return compacted;
    }

    private static bool DominatesPartial(
        State candidate,
        State other,
        IEquipmentExactSolverUtilityModel utilityModel,
        ResourceProjection futureResources)
    {
        var utility = utilityModel.ComparePartial(candidate.Utility, other.Utility);
        if (!utility.IsNoWorse || !CouldDominateWithoutUtility(candidate, other, futureResources))
            return false;

        return utility.IsStrictlyBetter ||
            candidate.Cost < other.Cost ||
            candidate.EvidenceRisk.IsStrictlyBetterThan(other.EvidenceRisk) ||
            candidate.PurchaseTransactions < other.PurchaseTransactions ||
            candidate.Resources.WorldVisitCount < other.Resources.WorldVisitCount ||
            candidate.Resources.VendorStopCount < other.Resources.VendorStopCount;
    }

    private static bool CouldDominateWithoutUtility(
        State candidate,
        State other,
        ResourceProjection futureResources)
    {
        if (candidate.Cost > other.Cost ||
            !candidate.EvidenceRisk.IsNoWorseThan(other.EvidenceRisk) ||
            candidate.PurchaseTransactions > other.PurchaseTransactions ||
            candidate.Resources.SunkWorldVisitCount > other.Resources.SunkWorldVisitCount ||
            candidate.Resources.SunkVendorStopCount > other.Resources.SunkVendorStopCount ||
            !candidate.Resources.WorldVisits.IsSubsetOf(other.Resources.WorldVisits) ||
            !candidate.Resources.VendorStops.IsSubsetOf(other.Resources.VendorStops) ||
            futureResources.KeepMainHandOccupancy &&
                candidate.Resources.MainHandOccupiesOffHand &&
                !other.Resources.MainHandOccupiesOffHand)
            return false;
        foreach (var index in futureResources.AllocationIndices)
        {
            if (candidate.Resources.AllocationCounts[index] > other.Resources.AllocationCounts[index])
                return false;
        }
        return candidate.Resources.UniqueItems.IsSubsetOf(other.Resources.UniqueItems, futureResources.UniqueMask);
    }

    private static State Allocate(
        State state,
        ExactOfferChoice choice,
        EquipmentOfferAllocationKey? baseline,
        ResourceSignature allocatedResources,
        RequestUtilityPool utilities)
    {
        var offer = choice.Primary;
        var freshness = Math.Max(state.EvidenceRisk.FreshnessBucket, offer.EvidenceRisk.FreshnessBucket);
        var incompleteCoverage = checked(state.EvidenceRisk.IncompleteCoverageCount + offer.EvidenceRisk.IncompleteCoverageCount);
        var confidencePenalty = Math.Max(state.EvidenceRisk.ConfidencePenalty, offer.EvidenceRisk.ConfidencePenalty);
        var evidenceRisk = freshness == state.EvidenceRisk.FreshnessBucket &&
            incompleteCoverage == state.EvidenceRisk.IncompleteCoverageCount &&
            confidencePenalty == state.EvidenceRisk.ConfidencePenalty
                ? state.EvidenceRisk
                : new EquipmentEvidenceRisk(freshness, incompleteCoverage, confidencePenalty);
        return new(
            state.Paths,
            choice.Selections,
            utilities.Add(state.Utility, offer.Utility),
            checked(state.Cost + offer.AcquisitionCostGil),
            checked(state.PurchaseTransactions + offer.PurchaseTransactions),
            evidenceRisk,
            allocatedResources,
            state.IsBaselineSoFar && baseline == offer.AllocationKey);
    }

    private static ExactOfferChoice[] BuildExactOfferFrontier(
        IReadOnlyList<EquipmentExactSolverOffer> offers,
        EquipmentLoadoutPosition position,
        EquipmentOfferAllocationKey? baseline,
        IEquipmentExactSolverUtilityModel utilityModel,
        RequestResourcePool resources)
    {
        // Stage one is the static exact-quality base-item relation: modeled utility and future
        // slot/occupancy/unique behavior must be no worse. Stage two proves that a concrete
        // owned/vendor/market acquisition envelope is also no worse. Item level only orders
        // witnesses; it is never part of either proof.
        var baseRelations = BuildBaseItemDominanceRelations(offers, position, utilityModel, resources);
        var retained = offers.Where(offer => !offers.Any(other =>
                other != offer &&
                baseRelations.Contains((BaseItemKey(other, position, resources), BaseItemKey(offer, position, resources))) &&
                StrictlyDominatesAcquisition(other, offer, position, baseline, utilityModel, resources)))
            .ToArray();
        return retained
            .GroupBy(offer => OfferEquivalenceKey(offer, position, baseline, resources), StringComparer.Ordinal)
            .Select(group => group.OrderBy(OfferCanonicalText, StringComparer.Ordinal).ToArray())
            .Select(group => new ExactOfferChoice(
                group[0],
                group,
                group.Select(equivalent => new EquipmentLoadoutSelection(
                    position,
                    equivalent.Offer.Key,
                    1,
                    equivalent.ObservationId)).ToArray()))
            .OrderBy(choice => OfferCanonicalText(choice.Primary), StringComparer.Ordinal)
            .ToArray();
    }

    private static HashSet<(string Candidate, string Other)> BuildBaseItemDominanceRelations(
        IReadOnlyList<EquipmentExactSolverOffer> offers,
        EquipmentLoadoutPosition position,
        IEquipmentExactSolverUtilityModel utilityModel,
        RequestResourcePool resources)
    {
        var baseItems = offers
            .GroupBy(offer => BaseItemKey(offer, position, resources), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(value => value.Offer.Definition.ItemLevel)
                .ThenBy(OfferCanonicalText, StringComparer.Ordinal)
                .First())
            .ToArray();
        var relations = new HashSet<(string Candidate, string Other)>();
        foreach (var candidate in baseItems.OrderByDescending(value => value.Offer.Definition.ItemLevel))
        foreach (var other in baseItems)
        {
            if (BaseItemNoWorse(candidate, other, position, utilityModel, resources))
                relations.Add((BaseItemKey(candidate, position, resources), BaseItemKey(other, position, resources)));
        }
        return relations;
    }

    private static bool BaseItemNoWorse(
        EquipmentExactSolverOffer candidate,
        EquipmentExactSolverOffer other,
        EquipmentLoadoutPosition position,
        IEquipmentExactSolverUtilityModel utilityModel,
        RequestResourcePool resources)
    {
        if (!resources.CanSubstituteBaseItem(candidate, other, position))
            return false;
        var utility = utilityModel.ComparePartial(candidate.Utility, other.Utility);
        return utility.IsNoWorse;
    }

    private static bool StrictlyDominatesAcquisition(
        EquipmentExactSolverOffer candidate,
        EquipmentExactSolverOffer other,
        EquipmentLoadoutPosition position,
        EquipmentOfferAllocationKey? baseline,
        IEquipmentExactSolverUtilityModel utilityModel,
        RequestResourcePool resources)
    {
        if (other.AllocationKey == baseline ||
            !resources.CanSubstituteAllocation(candidate, other) ||
            candidate.AcquisitionCostGil > other.AcquisitionCostGil ||
            !candidate.EvidenceRisk.IsNoWorseThan(other.EvidenceRisk) ||
            candidate.PurchaseTransactions > other.PurchaseTransactions ||
            !StopIsSubset(candidate.WorldVisitKey, other.WorldVisitKey) ||
            !StopIsSubset(candidate.VendorStopKey, other.VendorStopKey))
            return false;
        var utility = utilityModel.ComparePartial(candidate.Utility, other.Utility);
        return utility.IsStrictlyBetter ||
            candidate.AcquisitionCostGil < other.AcquisitionCostGil ||
            candidate.EvidenceRisk.IsStrictlyBetterThan(other.EvidenceRisk) ||
            candidate.PurchaseTransactions < other.PurchaseTransactions ||
            StopCount(candidate.WorldVisitKey) < StopCount(other.WorldVisitKey) ||
            StopCount(candidate.VendorStopKey) < StopCount(other.VendorStopKey);
    }

    private static string BaseItemKey(
        EquipmentExactSolverOffer offer,
        EquipmentLoadoutPosition position,
        RequestResourcePool resources) => string.Join("||",
            offer.Offer.Key.ItemId,
            offer.Offer.Key.Quality,
            offer.Utility.CanonicalText,
            offer.Offer.Definition.Slot,
            offer.Offer.Definition.MainHandOccupancy,
            offer.Offer.Definition.OffHandOccupancy,
            resources.BaseItemFutureBehaviorKey(offer, position));

    private static string OfferEquivalenceKey(
        EquipmentExactSolverOffer offer,
        EquipmentLoadoutPosition position,
        EquipmentOfferAllocationKey? baseline,
        RequestResourcePool resources) => string.Join("||",
            offer.Utility.CanonicalText,
            offer.AcquisitionCostGil,
            offer.PurchaseTransactions,
            offer.EvidenceRisk.FreshnessBucket,
            offer.EvidenceRisk.IncompleteCoverageCount,
            offer.EvidenceRisk.ConfidencePenalty,
            offer.WorldVisitKey,
            offer.VendorStopKey,
            offer.AllocationKey == baseline,
            resources.FutureBehaviorKey(offer, position));

    private static string OfferCanonicalText(EquipmentExactSolverOffer offer) =>
        $"{offer.Offer.Key.ItemId}:{offer.Offer.Key.Quality}:{offer.Offer.Key.SourceKind}:{offer.Offer.Key.SourceCatalogKey}:{offer.ObservationId}";

    private static bool StopIsSubset(string? candidate, string? other) =>
        string.IsNullOrWhiteSpace(candidate) || string.Equals(candidate, other, StringComparison.Ordinal);

    private static int StopCount(string? key) => string.IsNullOrWhiteSpace(key) ? 0 : 1;

    private static void Validate(EquipmentExactFrontierRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Offers);
        ArgumentNullException.ThrowIfNull(request.RequiredPositions);
        ArgumentNullException.ThrowIfNull(request.Baseline);
        if (request.RequiredPositions.Count == 0)
            throw new ArgumentException("At least one required equipment position is needed.", nameof(request));
        if (request.RequiredPositions.Any(position => !CanonicalPositions.Contains(position)))
            throw new ArgumentException("Request contains an unsupported equipment position.", nameof(request));
        if (request.MaxRetainedRepresentatives is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(request.MaxRetainedRepresentatives));
        var duplicate = request.Offers.GroupBy(offer => offer.AllocationKey).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Duplicate exact solver offer '{duplicate.Key}'.", nameof(request));
        foreach (var offer in request.Offers)
        {
            if (!Enum.IsDefined(offer.Offer.SourceKind))
                throw new ArgumentException($"Offer '{offer.AllocationKey}' has an unsupported acquisition source.", nameof(request));
            if (offer.AvailableQuantity == 0)
                throw new ArgumentException($"Offer '{offer.AllocationKey}' has zero available quantity.", nameof(request));
            if (offer.Positions.Count == 0)
                throw new ArgumentException($"Offer '{offer.AllocationKey}' has no equipment positions.", nameof(request));
            if (!offer.Offer.Definition.IsEquipment || offer.Offer.Definition.IsSoulCrystal ||
                offer.Positions.Any(position => !DefinitionFitsPosition(offer.Offer.Definition, position)))
            {
                throw new ArgumentException(
                    $"Offer '{offer.AllocationKey}' declares a position its static equipment definition cannot occupy.",
                    nameof(request));
            }
            if (offer.PurchaseTransactions < 0)
                throw new ArgumentException($"Offer '{offer.AllocationKey}' has a negative purchase burden.", nameof(request));
            _ = offer.Utility.Normalize();
        }
    }

    private static bool DefinitionFitsPosition(
        EquipmentItemDefinition definition,
        EquipmentLoadoutPosition position) => position switch
    {
        EquipmentLoadoutPosition.MainHand => definition.Slot == EquipmentSlot.MainHand,
        EquipmentLoadoutPosition.OffHand => definition.Slot == EquipmentSlot.OffHand,
        EquipmentLoadoutPosition.Head => definition.Slot == EquipmentSlot.Head,
        EquipmentLoadoutPosition.Body => definition.Slot == EquipmentSlot.Body,
        EquipmentLoadoutPosition.Hands => definition.Slot == EquipmentSlot.Hands,
        EquipmentLoadoutPosition.Legs => definition.Slot == EquipmentSlot.Legs,
        EquipmentLoadoutPosition.Feet => definition.Slot == EquipmentSlot.Feet,
        EquipmentLoadoutPosition.Ears => definition.Slot == EquipmentSlot.Ears,
        EquipmentLoadoutPosition.Neck => definition.Slot == EquipmentSlot.Neck,
        EquipmentLoadoutPosition.Wrists => definition.Slot == EquipmentSlot.Wrists,
        EquipmentLoadoutPosition.LeftRing => definition.Slot == EquipmentSlot.Ring && definition.FitsLeftRing,
        EquipmentLoadoutPosition.RightRing => definition.Slot == EquipmentSlot.Ring && definition.FitsRightRing,
        _ => false,
    };

    private static void ValidateBaseline(
        EquipmentExactFrontierRequest request,
        IReadOnlyList<EquipmentExactSolverOffer> offers)
    {
        var feasibilityOffers = offers.Select(offer => new EquipmentFeasibilityOffer(offer.Offer, offer.AvailableQuantity, offer.ObservationId)).ToArray();
        var selections = new List<EquipmentLoadoutSelection>();
        foreach (var position in request.RequiredPositions)
        {
            if (!request.Baseline.TryGetValue(position, out var key))
                throw new ArgumentException($"Baseline does not declare {position}.", nameof(request));
            if (key is null)
                continue;
            var offer = offers.FirstOrDefault(candidate => candidate.AllocationKey == key && candidate.Positions.Contains(position))
                ?? throw new ArgumentException($"Baseline offer '{key}' cannot fill {position}.", nameof(request));
            if (offer.AcquisitionCostGil != 0 || offer.PurchaseTransactions != 0)
                throw new ArgumentException($"Baseline offer '{key}' is not a no-purchase choice.", nameof(request));
            selections.Add(new(position, offer.Offer.Key, 1, offer.ObservationId));
        }
        var baseline = new EquipmentLoadoutCandidate("baseline", selections);
        var feasibility = new EquipmentLoadoutFeasibilityEvaluator().Evaluate(new(
            baseline,
            feasibilityOffers,
            request.RequiredPositions));
        if (!feasibility.IsFeasible)
            throw new ArgumentException($"No-purchase baseline is infeasible: {string.Join("; ", feasibility.Violations.Select(value => value.Message))}", nameof(request));
    }

    private static string SolutionId(IReadOnlyList<EquipmentLoadoutSelection> selections)
    {
        var canonical = string.Join('|', selections.OrderBy(selection => selection.Position).Select(selection =>
            $"{selection.Position}:{selection.OfferKey.ItemId}:{selection.OfferKey.Quality}:{selection.OfferKey.SourceKind}:{selection.OfferKey.SourceCatalogKey}:{selection.ObservationId}:{selection.Quantity}"));
        return $"solution:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16]}";
    }

    private static StateEquivalenceKey BuildStateEquivalenceKey(State state) => new(
        state.Utility,
        state.Cost,
        state.PurchaseTransactions,
        state.EvidenceRisk.FreshnessBucket,
        state.EvidenceRisk.IncompleteCoverageCount,
        state.EvidenceRisk.ConfidencePenalty,
        state.Resources,
        state.IsBaselineSoFar);

    private static string CanonicalMetricText(State state) => string.Join("||",
        state.Utility.CanonicalText,
        state.Cost,
        state.PurchaseTransactions,
        state.EvidenceRisk.FreshnessBucket,
        state.EvidenceRisk.IncompleteCoverageCount,
        state.EvidenceRisk.ConfidencePenalty,
        state.Resources.CanonicalWorldText,
        state.Resources.CanonicalVendorText);

    private sealed record ExplicitEquivalenceKey(
        string ProfileId,
        string ProfileVersion,
        string ContextId,
        uint ClassJobId,
        uint CharacterLevel,
        ulong Cost,
        long UtilityUnits);

    private readonly record struct StateEquivalenceKey(
        EquipmentSolverUtilityVector Utility,
        ulong Cost,
        int PurchaseTransactions,
        int Freshness,
        int IncompleteCoverage,
        int Confidence,
        ResourceSignature Resources,
        bool IsBaselineSoFar);

    private abstract class RetainedPathNode
    {
        private IReadOnlyList<RepresentativePath>? cachedRepresentativePaths;
        private int cachedLimit;

        public static RetainedPathNode Empty { get; } = new EmptyRetainedPathNode();

        public abstract long RetainedPathCount { get; }
        protected abstract CanonicalPathKey? CanonicalFirstKey { get; }
        public RetainedPathNode Append(EquipmentLoadoutSelection selection) => new AppendRetainedPathNode(this, selection);

        public RetainedPathNode AppendAlternatives(IReadOnlyList<EquipmentLoadoutSelection> selections) =>
            selections.Count == 1
                ? Append(selections[0])
                : Union(selections.Select(Append));

        public static RetainedPathNode Union(IEnumerable<RetainedPathNode> paths)
        {
            var children = paths.ToArray();
            return children.Length == 1 ? children[0] : new UnionRetainedPathNode(children);
        }

        public static void ReleaseIntermediateRepresentativeCaches(IEnumerable<RetainedPathNode> roots)
        {
            var rootSet = new HashSet<RetainedPathNode>(roots, ReferenceEqualityComparer.Instance);
            var visited = new HashSet<RetainedPathNode>(ReferenceEqualityComparer.Instance);
            foreach (var root in rootSet)
                Release(root);
            return;

            void Release(RetainedPathNode node)
            {
                if (!visited.Add(node))
                    return;
                foreach (var child in node.Children)
                    Release(child);
                if (rootSet.Contains(node))
                    return;
                node.cachedRepresentativePaths = null;
                node.cachedLimit = 0;
            }
        }

        public IReadOnlyList<IReadOnlyList<EquipmentLoadoutSelection>> GetCanonicalRepresentatives(int limit) =>
            GetCanonicalRepresentativePaths(limit)
                .Select(path => (IReadOnlyList<EquipmentLoadoutSelection>)path.Materialize())
                .ToArray();

        private IReadOnlyList<RepresentativePath> GetCanonicalRepresentativePaths(int limit)
        {
            if (limit < 1)
                throw new ArgumentOutOfRangeException(nameof(limit));
            if (cachedRepresentativePaths is not null && cachedLimit >= limit)
                return cachedLimit == limit
                    ? cachedRepresentativePaths
                    : cachedRepresentativePaths.Take(limit).ToArray();
            var representatives = BuildCanonicalRepresentativePaths(limit)
                .Select(value => (Path: value, Text: value.CanonicalText()))
                .DistinctBy(value => value.Text, StringComparer.Ordinal)
                .OrderBy(value => value.Text, StringComparer.Ordinal)
                .Take(limit)
                .Select(value => value.Path)
                .ToArray();
            cachedRepresentativePaths = representatives;
            cachedLimit = limit;
            return representatives;
        }

        protected abstract IEnumerable<RepresentativePath> BuildCanonicalRepresentativePaths(int limit);
        protected abstract IEnumerable<RetainedPathNode> Children { get; }

        private sealed class EmptyRetainedPathNode : RetainedPathNode
        {
            public override long RetainedPathCount => 1;
            protected override CanonicalPathKey? CanonicalFirstKey => null;
            protected override IEnumerable<RetainedPathNode> Children => [];

            protected override IEnumerable<RepresentativePath> BuildCanonicalRepresentativePaths(int limit)
            {
                yield return RepresentativePath.Empty;
            }
        }

        private sealed class AppendRetainedPathNode(RetainedPathNode parent, EquipmentLoadoutSelection selection) : RetainedPathNode
        {
            private readonly CanonicalPathKey canonicalFirstKey = CanonicalPathKey.Append(parent.CanonicalFirstKey, selection);

            public override long RetainedPathCount => parent.RetainedPathCount;
            protected override CanonicalPathKey CanonicalFirstKey => canonicalFirstKey;
            protected override IEnumerable<RetainedPathNode> Children
            {
                get { yield return parent; }
            }

            protected override IEnumerable<RepresentativePath> BuildCanonicalRepresentativePaths(int limit) =>
                parent.GetCanonicalRepresentativePaths(limit)
                    .Select(value => value.Append(selection));
        }

        private sealed class UnionRetainedPathNode : RetainedPathNode
        {
            private readonly IReadOnlyList<RetainedPathNode> children;
            private readonly long retainedPathCount;
            private readonly CanonicalPathKey canonicalFirstKey;

            public UnionRetainedPathNode(IReadOnlyList<RetainedPathNode> children)
            {
                this.children = children;
                retainedPathCount = children.Aggregate(0L, (sum, child) => checked(sum + child.RetainedPathCount));
                canonicalFirstKey = children.Select(child => child.CanonicalFirstKey)
                    .Where(value => value is not null)
                    .Cast<CanonicalPathKey>()
                    .OrderBy(value => value.Text, StringComparer.Ordinal)
                    .First();
            }

            public override long RetainedPathCount => retainedPathCount;
            protected override CanonicalPathKey CanonicalFirstKey => canonicalFirstKey;
            protected override IEnumerable<RetainedPathNode> Children => children;

            protected override IEnumerable<RepresentativePath> BuildCanonicalRepresentativePaths(int limit) =>
                children.SelectMany(child => child.GetCanonicalRepresentativePaths(limit));
        }

        protected sealed class RepresentativePath
        {
            public static RepresentativePath Empty { get; } = new(null, null, 0);

            private RepresentativePath(
                RepresentativePath? previous,
                EquipmentLoadoutSelection? selection,
                int count)
            {
                Previous = previous;
                Selection = selection;
                Count = count;
                SelectionText = selection is null
                    ? string.Empty
                    : CanonicalRepresentativeSelectionTexts.GetValue(selection, CanonicalRepresentativeSelectionText);
                CanonicalLength = previous is null
                    ? SelectionText.Length
                    : checked(previous.CanonicalLength + (previous.Count == 0 ? 0 : 1) + SelectionText.Length);
            }

            private RepresentativePath? Previous { get; }
            private EquipmentLoadoutSelection? Selection { get; }
            private string SelectionText { get; }
            private int Count { get; }
            private int CanonicalLength { get; }

            public RepresentativePath Append(EquipmentLoadoutSelection selection) =>
                new(this, selection, checked(Count + 1));

            public EquipmentLoadoutSelection[] Materialize()
            {
                var selections = new EquipmentLoadoutSelection[Count];
                var current = this;
                for (var index = Count - 1; index >= 0; index--)
                {
                    selections[index] = current.Selection!;
                    current = current.Previous!;
                }
                return selections;
            }

            public string CanonicalText() => string.Create(CanonicalLength, this, static (buffer, path) =>
            {
                var cursor = buffer.Length;
                for (var current = path; current.Selection is not null; current = current.Previous!)
                {
                    cursor -= current.SelectionText.Length;
                    current.SelectionText.AsSpan().CopyTo(buffer[cursor..]);
                    if (current.Previous?.Selection is not null)
                        buffer[--cursor] = '|';
                }
            });
        }

    }

    private static readonly ConditionalWeakTable<EquipmentLoadoutSelection, string> CanonicalRepresentativeSelectionTexts = new();
    private static readonly ConditionalWeakTable<EquipmentLoadoutSelection, string> CanonicalStateSelectionTexts = new();

    private static string CanonicalRepresentativeSelectionText(EquipmentLoadoutSelection selection) =>
        $"{CanonicalStateSelectionText(selection)}:{selection.Quantity}";

    private sealed class CanonicalPathKey
    {
        private CanonicalPathKey(CanonicalPathKey? previous, EquipmentLoadoutSelection selection)
        {
            Previous = previous;
            Selection = selection;
            SelectionText = CanonicalStateSelectionTexts.GetValue(selection, CanonicalStateSelectionText);
            CanonicalLength = checked((previous?.CanonicalLength ?? 0) + (previous is null ? 0 : 1) + SelectionText.Length);
        }

        public CanonicalPathKey? Previous { get; }
        public EquipmentLoadoutSelection Selection { get; }
        private string SelectionText { get; }
        private int CanonicalLength { get; }
        public string Text => string.Create(CanonicalLength, this, static (buffer, path) =>
        {
            var cursor = buffer.Length;
            for (var current = path; current is not null; current = current.Previous)
            {
                cursor -= current.SelectionText.Length;
                current.SelectionText.AsSpan().CopyTo(buffer[cursor..]);
                if (current.Previous is not null)
                    buffer[--cursor] = '|';
            }
        });

        public static CanonicalPathKey Append(CanonicalPathKey? current, EquipmentLoadoutSelection selection) =>
            new(current, selection);

    }

    private static string CanonicalStateSelectionText(EquipmentLoadoutSelection selection) =>
        $"{selection.Position}:{selection.OfferKey.ItemId}:{selection.OfferKey.Quality}:{selection.OfferKey.SourceKind}:{selection.OfferKey.SourceCatalogKey}:{selection.ObservationId}";

    private sealed class ExactDominanceIndex
    {
        private readonly IEquipmentPartialDominanceCoordinateModel coordinateModel;
        private readonly ResourceProjection futureResources;
        private readonly Dictionary<State, ulong[]> scores = new(ReferenceEqualityComparer.Instance);
        private readonly DominanceAxis[] axes;
        private readonly Dictionary<FastResourceKey, FastResourceBucket> fastResourceBuckets = [];
        private readonly bool useFastResources;
        private readonly ulong[] candidateScores;

        public bool UsesFastResources => useFastResources;
        public int ResourceBucketCount => fastResourceBuckets.Count;
        public long DominanceStateVisitCount { get; private set; }

        public ExactDominanceIndex(
            IEquipmentPartialDominanceCoordinateModel coordinateModel,
            ResourceProjection futureResources,
            IReadOnlyList<State> generation,
            CancellationToken cancellationToken = default)
        {
            this.coordinateModel = coordinateModel;
            this.futureResources = futureResources;
            candidateScores = new ulong[checked(coordinateModel.GetPartialDominanceCoordinates(generation[0].Utility).Count + 9)];
            useFastResources = generation.All(state => state.Resources.TryGetFastResourceKey(futureResources, out _));
            if (useFastResources)
            {
                axes = [];
                return;
            }
            for (var index = 0; index < generation.Count; index++)
            {
                if ((index & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var state = generation[index];
                scores.Add(state, BuildScores(state));
            }
            axes = Enumerable.Range(0, scores.Values.First().Length)
                .Select(dimension => new DominanceAxis(
                    scores.Values.Select(value => value[dimension]),
                    cancellationToken))
                .ToArray();
        }

        public void Add(State state)
        {
            var stateScores = BuildScores(state);
            if (useFastResources)
            {
                scores.Add(state, stateScores);
                state.Resources.TryGetFastResourceKey(futureResources, out var key);
                if (!fastResourceBuckets.TryGetValue(key, out var bucket))
                {
                    bucket = new(stateScores.Length);
                    fastResourceBuckets.Add(key, bucket);
                }
                bucket.Add(state, stateScores);
            }
            else
            {
                for (var dimension = 0; dimension < axes.Length; dimension++)
                    axes[dimension].Add(stateScores[dimension], state);
            }
        }

        public bool AnyDominates(
            State candidate,
            IEquipmentExactSolverUtilityModel utilityModel,
            CancellationToken cancellationToken = default)
        {
            if (useFastResources)
                BuildScores(candidate, candidateScores);
            IReadOnlyList<ulong> candidateScoreValues = useFastResources
                ? candidateScores
                : scores[candidate];
            var visited = 0;
            if (useFastResources && candidate.Resources.TryGetFastResourceKey(futureResources, out var resource))
            {
                if (FastResourceCombinationCount(resource) <= 512)
                {
                    foreach (var key in DominatingResourceKeys(resource))
                    {
                        if ((visited++ & 255) == 0)
                            cancellationToken.ThrowIfCancellationRequested();
                        if (!fastResourceBuckets.TryGetValue(key, out var bucket) ||
                            !ScoresNoWorse(bucket.MaximumScores, candidateScoreValues))
                            continue;
                        if (BucketAnyDominates(
                            bucket,
                            candidate,
                            candidateScoreValues,
                            utilityModel,
                            cancellationToken,
                            ref visited))
                            return true;
                    }
                    return false;
                }
                foreach (var bucket in fastResourceBuckets)
                {
                    if (!FastResourcesCouldDominate(bucket.Key, resource))
                        continue;
                    if (!ScoresNoWorse(bucket.Value.MaximumScores, candidateScoreValues))
                        continue;
                    if (BucketAnyDominates(
                        bucket.Value,
                        candidate,
                        candidateScoreValues,
                        utilityModel,
                        cancellationToken,
                        ref visited))
                        return true;
                }
                return false;
            }

            var bestDimension = 0;
            var bestCount = int.MaxValue;
            for (var dimension = 0; dimension < axes.Length; dimension++)
            {
                var count = axes[dimension].QualifyingCount(candidateScoreValues[dimension]);
                if (count < bestCount)
                {
                    bestCount = count;
                    bestDimension = dimension;
                }
            }
            foreach (var other in axes[bestDimension].QualifyingStates(candidateScoreValues[bestDimension]))
            {
                DominanceStateVisitCount++;
                if ((visited++ & 255) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (!ScoresNoWorse(scores[other], candidateScoreValues) ||
                    !CouldDominateWithoutUtility(other, candidate, futureResources))
                    continue;
                if (DominatesPartial(other, candidate, utilityModel, futureResources))
                    return true;
            }
            return false;
        }

        private bool BucketAnyDominates(
            FastResourceBucket bucket,
            State candidate,
            IReadOnlyList<ulong> candidateScores,
            IEquipmentExactSolverUtilityModel utilityModel,
            CancellationToken cancellationToken,
            ref int visited)
        {
            for (var dimension = 0; dimension < bucket.BestStates.Length; dimension++)
            {
                var other = bucket.BestStates[dimension];
                if (other is null)
                    continue;
                var duplicate = false;
                for (var previous = 0; previous < dimension; previous++)
                {
                    if (ReferenceEquals(bucket.BestStates[previous], other))
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate)
                    continue;
                DominanceStateVisitCount++;
                if ((visited++ & 255) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (ScoresNoWorse(scores[other], candidateScores) &&
                    DominatesPartial(other, candidate, utilityModel, futureResources))
                    return true;
            }
            if (bucket.HasThreeUtilityCoordinates)
            {
                var dominated = bucket.AnyThreeCoordinateDominates(
                    candidate,
                    candidateScores,
                    utilityModel,
                    futureResources,
                    cancellationToken,
                    ref visited,
                    out var indexedVisits);
                DominanceStateVisitCount += indexedVisits;
                return dominated;
            }
            var minimumCostIndex = bucket.LowerCostScore(candidateScores[0]);
            for (var costIndex = bucket.CostScoreCount - 1; costIndex >= minimumCostIndex; costIndex--)
            foreach (var other in bucket.StatesAtCostIndex(costIndex))
            {
                DominanceStateVisitCount++;
                if ((visited++ & 255) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (!ScoresNoWorse(scores[other], candidateScores))
                    continue;
                if (DominatesPartial(other, candidate, utilityModel, futureResources))
                    return true;
            }
            return false;
        }

        private static long FastResourceCombinationCount(FastResourceKey key)
        {
            var subsetBits = BitOperations.PopCount(key.Worlds) +
                BitOperations.PopCount(key.Vendors) +
                BitOperations.PopCount(key.UniqueItems);
            return checked((1L << subsetBits) * (key.AllocationCount + 1) *
                (key.SunkWorldVisitCount + 1) *
                (key.SunkVendorStopCount + 1) *
                (key.MainHandOccupiesOffHand ? 2 : 1));
        }

        private IEnumerable<FastResourceKey> DominatingResourceKeys(FastResourceKey candidate)
        {
            for (var worlds = candidate.Worlds; ; worlds = (worlds - 1) & candidate.Worlds)
            {
                for (var vendors = candidate.Vendors; ; vendors = (vendors - 1) & candidate.Vendors)
                {
                    for (var unique = candidate.UniqueItems; ; unique = (unique - 1) & candidate.UniqueItems)
                    {
                        var occupancyCount = candidate.MainHandOccupiesOffHand ? 2 : 1;
                        for (var sunkWorlds = 0; sunkWorlds <= candidate.SunkWorldVisitCount; sunkWorlds++)
                        for (var sunkVendors = 0; sunkVendors <= candidate.SunkVendorStopCount; sunkVendors++)
                        for (var occupancy = 0; occupancy < occupancyCount; occupancy++)
                        {
                            yield return new(
                                worlds,
                                vendors,
                                unique,
                                -1,
                                0,
                                sunkWorlds,
                                sunkVendors,
                                occupancy != 0);
                            if (candidate.AllocationIndex >= 0)
                            {
                                for (uint count = 1; count <= candidate.AllocationCount; count++)
                                    yield return new(
                                        worlds,
                                        vendors,
                                        unique,
                                        candidate.AllocationIndex,
                                        count,
                                        sunkWorlds,
                                        sunkVendors,
                                        occupancy != 0);
                            }
                        }
                        if (unique == 0)
                            break;
                    }
                    if (vendors == 0)
                        break;
                }
                if (worlds == 0)
                    break;
            }
        }

        private static bool FastResourcesCouldDominate(FastResourceKey candidate, FastResourceKey other) =>
            (candidate.Worlds & ~other.Worlds) == 0 &&
            (candidate.Vendors & ~other.Vendors) == 0 &&
            (candidate.UniqueItems & ~other.UniqueItems) == 0 &&
            candidate.SunkWorldVisitCount <= other.SunkWorldVisitCount &&
            candidate.SunkVendorStopCount <= other.SunkVendorStopCount &&
            (!candidate.MainHandOccupiesOffHand || other.MainHandOccupiesOffHand) &&
            (candidate.AllocationIndex < 0 ||
                candidate.AllocationIndex == other.AllocationIndex &&
                candidate.AllocationCount <= other.AllocationCount);

        private ulong[] BuildScores(State state)
        {
            var result = new ulong[candidateScores.Length];
            BuildScores(state, result);
            return result;
        }

        private void BuildScores(State state, Span<ulong> result)
        {
            var utility = coordinateModel.GetPartialDominanceCoordinates(state.Utility);
            if (utility.Any(value => value < 0))
                throw new InvalidOperationException("Partial dominance coordinates must be non-negative monotonic benefits.");
            if (result.Length != utility.Count + 9)
                throw new InvalidOperationException("Partial dominance coordinate count changed within one solver request.");
            result[0] = ulong.MaxValue - state.Cost;
            result[1] = ulong.MaxValue - checked((ulong)state.PurchaseTransactions);
            result[2] = ulong.MaxValue - checked((ulong)state.EvidenceRisk.FreshnessBucket);
            result[3] = ulong.MaxValue - checked((ulong)state.EvidenceRisk.IncompleteCoverageCount);
            result[4] = ulong.MaxValue - checked((ulong)state.EvidenceRisk.ConfidencePenalty);
            for (var index = 0; index < utility.Count; index++)
                result[index + 5] = checked((ulong)utility[index]);
            result[^4] = ulong.MaxValue - checked((ulong)state.Resources.WorldVisitCount);
            result[^3] = ulong.MaxValue - checked((ulong)state.Resources.VendorStopCount);
            result[^2] = ulong.MaxValue - state.Resources.ActiveAllocationCount(futureResources.AllocationIndices);
            result[^1] = ulong.MaxValue - checked((ulong)state.Resources.UniqueItems.CountMasked(futureResources.UniqueMask));
        }

        private static bool ScoresNoWorse(IReadOnlyList<ulong> candidate, IReadOnlyList<ulong> other)
        {
            for (var index = 0; index < candidate.Count; index++)
            {
                if (candidate[index] < other[index])
                    return false;
            }
            return true;
        }

        private sealed class FastResourceBucket(int scoreCount)
        {
            public ulong[] MaximumScores { get; } = new ulong[scoreCount];
            public State?[] BestStates { get; } = new State?[scoreCount];
            private readonly SortedList<ulong, List<State>> statesByCostScore = [];
            private readonly Dictionary<NumericEnvelopeKey, Dictionary<UtilityPairKey, SortedList<ulong, UtilityWitness>>> threeCoordinateIndex = [];

            public bool HasThreeUtilityCoordinates => scoreCount == 12;

            public void Add(State state, IReadOnlyList<ulong> scores)
            {
                if (!statesByCostScore.TryGetValue(scores[0], out var sameCost))
                {
                    sameCost = [];
                    statesByCostScore.Add(scores[0], sameCost);
                }
                sameCost.Add(state);
                if (HasThreeUtilityCoordinates)
                {
                    var envelope = new NumericEnvelopeKey(scores[1], scores[2], scores[3], scores[4]);
                    if (!threeCoordinateIndex.TryGetValue(envelope, out var byPair))
                    {
                        byPair = [];
                        threeCoordinateIndex.Add(envelope, byPair);
                    }
                    var pair = new UtilityPairKey(scores[5], scores[6]);
                    if (!byPair.TryGetValue(pair, out var byThirdCoordinate))
                    {
                        byThirdCoordinate = [];
                        byPair.Add(pair, byThirdCoordinate);
                    }
                    var witness = new UtilityWitness(state, scores[0]);
                    if (!byThirdCoordinate.TryGetValue(scores[7], out var existing) || witness.CostScore > existing.CostScore)
                        byThirdCoordinate[scores[7]] = witness;
                }
                for (var index = 0; index < MaximumScores.Length; index++)
                {
                    if (BestStates[index] is null || scores[index] > MaximumScores[index])
                    {
                        MaximumScores[index] = scores[index];
                        BestStates[index] = state;
                    }
                }
            }

            public int CostScoreCount => statesByCostScore.Count;
            public IReadOnlyList<State> StatesAtCostIndex(int index) => statesByCostScore.Values[index];

            public int LowerCostScore(ulong minimumScore)
            {
                var low = 0;
                var high = statesByCostScore.Count;
                while (low < high)
                {
                    var middle = low + (high - low) / 2;
                    if (statesByCostScore.Keys[middle] < minimumScore)
                        low = middle + 1;
                    else
                        high = middle;
                }
                return low;
            }

            public bool AnyThreeCoordinateDominates(
                State candidate,
                IReadOnlyList<ulong> candidateScores,
                IEquipmentExactSolverUtilityModel utilityModel,
                ResourceProjection futureResources,
                CancellationToken cancellationToken,
                ref int cancellationVisits,
                out long stateVisits)
            {
                stateVisits = 0;
                foreach (var envelope in threeCoordinateIndex)
                {
                    if (!envelope.Key.IsNoWorseThan(candidateScores))
                        continue;
                    foreach (var pair in envelope.Value)
                    {
                        if (pair.Key.First < candidateScores[5] || pair.Key.Second < candidateScores[6])
                            continue;
                        var minimumThirdIndex = LowerThirdCoordinate(pair.Value, candidateScores[7]);
                        for (var thirdIndex = pair.Value.Count - 1; thirdIndex >= minimumThirdIndex; thirdIndex--)
                        {
                            var witness = pair.Value.Values[thirdIndex];
                            if (witness.CostScore < candidateScores[0])
                                continue;
                            stateVisits++;
                            if ((cancellationVisits++ & 255) == 0)
                                cancellationToken.ThrowIfCancellationRequested();
                            if (DominatesPartial(witness.State, candidate, utilityModel, futureResources))
                                return true;
                        }
                    }
                }
                return false;
            }

            private static int LowerThirdCoordinate(
                SortedList<ulong, UtilityWitness> values,
                ulong minimum)
            {
                var low = 0;
                var high = values.Count;
                while (low < high)
                {
                    var middle = low + (high - low) / 2;
                    if (values.Keys[middle] < minimum)
                        low = middle + 1;
                    else
                        high = middle;
                }
                return low;
            }

            private readonly record struct NumericEnvelopeKey(
                ulong Transactions,
                ulong Freshness,
                ulong IncompleteCoverage,
                ulong Confidence)
            {
                public bool IsNoWorseThan(IReadOnlyList<ulong> scores) =>
                    Transactions >= scores[1] &&
                    Freshness >= scores[2] &&
                    IncompleteCoverage >= scores[3] &&
                    Confidence >= scores[4];
            }

            private readonly record struct UtilityPairKey(ulong First, ulong Second);
            private readonly record struct UtilityWitness(State State, ulong CostScore);
        }


        private sealed class DominanceAxis
        {
            private readonly ulong[] values;
            private readonly List<State>[] states;
            private readonly FenwickCounts counts;

            public DominanceAxis(IEnumerable<ulong> generationValues, CancellationToken cancellationToken)
            {
                var allValues = new List<ulong>();
                foreach (var value in generationValues)
                {
                    if ((allValues.Count & 4095) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    allValues.Add(value);
                }
                values = allValues.Distinct().Order().ToArray();
                states = Enumerable.Range(0, values.Length).Select(_ => new List<State>()).ToArray();
                counts = new(values.Length);
            }

            public void Add(ulong value, State state)
            {
                var index = Array.BinarySearch(values, value);
                states[index].Add(state);
                counts.Add(index, 1);
            }

            public int QualifyingCount(ulong minimum) => counts.Range(LowerBound(minimum), values.Length);

            public IEnumerable<State> QualifyingStates(ulong minimum)
            {
                for (var index = LowerBound(minimum); index < values.Length; index++)
                foreach (var state in states[index])
                    yield return state;
            }

            private int LowerBound(ulong value)
            {
                var low = 0;
                var high = values.Length;
                while (low < high)
                {
                    var middle = low + (high - low) / 2;
                    if (values[middle] < value)
                        low = middle + 1;
                    else
                        high = middle;
                }
                return low;
            }
        }

        private sealed class FenwickCounts(int length)
        {
            private readonly int[] tree = new int[length + 1];

            public void Add(int index, int value)
            {
                for (var cursor = index + 1; cursor < tree.Length; cursor += cursor & -cursor)
                    tree[cursor] = checked(tree[cursor] + value);
            }

            public int Range(int startInclusive, int endExclusive) => Prefix(endExclusive) - Prefix(startInclusive);

            private int Prefix(int endExclusive)
            {
                var sum = 0;
                for (var cursor = endExclusive; cursor > 0; cursor -= cursor & -cursor)
                    sum = checked(sum + tree[cursor]);
                return sum;
            }
        }
    }

    private sealed record ExactOfferChoice(
        EquipmentExactSolverOffer Primary,
        IReadOnlyList<EquipmentExactSolverOffer> EquivalentOffers,
        IReadOnlyList<EquipmentLoadoutSelection> Selections);

    private sealed record FinalStateMaterialization(
        State State,
        IReadOnlyList<IReadOnlyList<EquipmentLoadoutSelection>> Representatives,
        IReadOnlyList<string> RepresentativeIds);

    private sealed record ResourceProjection(
        IReadOnlyList<int> AllocationIndices,
        CompactBitSet WorldMask,
        CompactBitSet VendorMask,
        CompactBitSet UniqueMask,
        bool KeepMainHandOccupancy)
    {
        public Dictionary<ResourceSignature, ResourceSignature> Projected { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private readonly record struct FastResourceKey(
        ulong Worlds,
        ulong Vendors,
        ulong UniqueItems,
        int AllocationIndex,
        uint AllocationCount,
        int SunkWorldVisitCount,
        int SunkVendorStopCount,
        bool MainHandOccupiesOffHand);

    private sealed class RequestUtilityPool
    {
        private readonly IEquipmentPartialUtilityCanonicalizationModel? canonicalizationModel;
        private readonly IEquipmentSeparablePartialUtilityCanonicalizationModel? separableCanonicalizationModel;
        private readonly bool useCompact;
        private readonly string[] componentKeys;
        private readonly IReadOnlyDictionary<string, int> componentIndices;
        private readonly Dictionary<CompactUtilityKey, EquipmentSolverUtilityVector> compactInterned = [];
        private readonly Dictionary<EquipmentSolverUtilityVector, CompactUtilityKey> compactKeys = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, EquipmentSolverUtilityVector> fallbackInterned = new(StringComparer.Ordinal);
        private readonly Dictionary<EquipmentSolverUtilityVector, Dictionary<EquipmentSolverUtilityVector, EquipmentSolverUtilityVector>> additions =
            new(ReferenceEqualityComparer.Instance);

        public RequestUtilityPool(
            IEquipmentPartialUtilityCanonicalizationModel? canonicalizationModel,
            IEnumerable<EquipmentSolverUtilityVector> utilities)
        {
            this.canonicalizationModel = canonicalizationModel;
            separableCanonicalizationModel = canonicalizationModel as IEquipmentSeparablePartialUtilityCanonicalizationModel;
            componentKeys = utilities.SelectMany(utility => utility.Components)
                .Select(component => component.Key)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            componentIndices = componentKeys.Select((key, index) => (key, index))
                .ToDictionary(value => value.key, value => value.index, StringComparer.Ordinal);
            useCompact = componentKeys.Length <= 4 && (canonicalizationModel is null || separableCanonicalizationModel is not null);
            Empty = Intern(EquipmentSolverUtilityVector.Empty);
        }

        public EquipmentSolverUtilityVector Empty { get; }

        public EquipmentSolverUtilityVector Add(
            EquipmentSolverUtilityVector left,
            EquipmentSolverUtilityVector right)
        {
            if (useCompact &&
                compactKeys.TryGetValue(left, out var leftKey) &&
                compactKeys.TryGetValue(right, out var rightKey))
            {
                var addedKey = AddCompact(leftKey, rightKey);
                if (compactInterned.TryGetValue(addedKey, out var compactCached))
                    return compactCached;
                return InternCompact(addedKey);
            }
            if (!additions.TryGetValue(left, out var byRight))
            {
                byRight = new(ReferenceEqualityComparer.Instance);
                additions.Add(left, byRight);
            }
            if (byRight.TryGetValue(right, out var cached))
                return cached;
            var added = Intern(left.Add(right));
            byRight.Add(right, added);
            return added;
        }

        public EquipmentSolverUtilityVector Intern(EquipmentSolverUtilityVector utility)
        {
            if (canonicalizationModel is not null)
                utility = canonicalizationModel.CanonicalizePartialUtility(utility).Normalize();
            if (useCompact)
            {
                var compactKey = BuildCompactKey(utility);
                if (compactInterned.TryGetValue(compactKey, out var compactExisting))
                    return compactExisting;
                compactInterned.Add(compactKey, utility);
                compactKeys.Add(utility, compactKey);
                return utility;
            }
            var key = utility.CanonicalText;
            if (fallbackInterned.TryGetValue(key, out var existing))
                return existing;
            fallbackInterned.Add(key, utility);
            return utility;
        }

        private EquipmentSolverUtilityVector InternCompact(CompactUtilityKey key)
        {
            var components = new List<EquipmentSolverUtilityComponent>(componentKeys.Length);
            for (var index = 0; index < componentKeys.Length; index++)
            {
                var units = key.Get(index);
                if (units > 0)
                    components.Add(new(componentKeys[index], units));
            }
            var utility = new EquipmentSolverUtilityVector(components.ToArray());
            compactInterned.Add(key, utility);
            compactKeys.Add(utility, key);
            return utility;
        }

        private CompactUtilityKey BuildCompactKey(EquipmentSolverUtilityVector utility)
        {
            Span<long> values = stackalloc long[4];
            foreach (var component in utility.Components)
                values[componentIndices[component.Key]] = component.Units;
            return new(values[0], values[1], values[2], values[3]);
        }

        private CompactUtilityKey AddCompact(CompactUtilityKey left, CompactUtilityKey right)
        {
            Span<long> values = stackalloc long[4];
            for (var index = 0; index < componentKeys.Length; index++)
            {
                var units = checked(left.Get(index) + right.Get(index));
                values[index] = separableCanonicalizationModel is null
                    ? units
                    : separableCanonicalizationModel.CanonicalizePartialUtilityComponent(componentKeys[index], units);
            }
            return new(values[0], values[1], values[2], values[3]);
        }

        private readonly record struct CompactUtilityKey(long A, long B, long C, long D)
        {
            public long Get(int index) => index switch
            {
                0 => A,
                1 => B,
                2 => C,
                3 => D,
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };
        }
    }

    /// <summary>
    /// Request-local canonical resource storage. Only allocations that can be reused across
    /// positions receive counters; one-position observations can never constrain a later step.
    /// Completed-position counters and unique IDs are projected away as soon as no future offer
    /// can observe them, while world/vendor footprints remain because later choices can reuse a stop.
    /// </summary>
    private sealed class RequestResourcePool
    {
        private readonly IReadOnlyList<EquipmentExactSolverOffer> offers;
        private readonly IReadOnlyDictionary<EquipmentOfferAllocationKey, int> allocationIndices;
        private readonly IReadOnlyDictionary<uint, int> uniqueIndices;
        private readonly IReadOnlyDictionary<string, int> worldIndices;
        private readonly IReadOnlyDictionary<string, int> vendorIndices;
        private readonly IReadOnlySet<EquipmentLoadoutPosition> requiredPositions;
        private readonly string[] worldKeys;
        private readonly string[] vendorKeys;
        private readonly Dictionary<ResourceSignature, ResourceSignature> interned = new(ResourceSignatureComparer.Instance);
        private readonly Dictionary<ResourceSignature, Dictionary<ResourceTransitionKey, ResourceSignature?>> transitions = new(ReferenceEqualityComparer.Instance);

        public RequestResourcePool(
            IReadOnlyList<EquipmentExactSolverOffer> offers,
            IReadOnlyCollection<EquipmentLoadoutPosition> requiredPositions)
        {
            this.offers = offers;
            this.requiredPositions = requiredPositions.ToHashSet();
            allocationIndices = offers
                .Where(offer => offer.Positions.Count(requiredPositions.Contains) > 1)
                .Select(offer => offer.AllocationKey)
                .Distinct()
                .Select((key, index) => (key, index))
                .ToDictionary(value => value.key, value => value.index);
            uniqueIndices = offers
                .Where(offer => offer.Offer.Definition.IsUnique)
                .Select(offer => offer.Offer.Definition.ItemId)
                .Distinct()
                .Order()
                .Select((key, index) => (key, index))
                .ToDictionary(value => value.key, value => value.index);
            worldKeys = offers.Select(offer => offer.WorldVisitKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            vendorKeys = offers.Select(offer => offer.VendorStopKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            worldIndices = worldKeys.Select((key, index) => (key, index))
                .ToDictionary(value => value.key, value => value.index, StringComparer.Ordinal);
            vendorIndices = vendorKeys.Select((key, index) => (key, index))
                .ToDictionary(value => value.key, value => value.index, StringComparer.Ordinal);
            Empty = Intern(new(
                this,
                new uint[allocationIndices.Count],
                CompactBitSet.Empty(worldKeys.Length),
                CompactBitSet.Empty(vendorKeys.Length),
                CompactBitSet.Empty(uniqueIndices.Count),
                0,
                0,
                false));
        }

        public ResourceSignature Empty { get; }

        public bool TryAllocate(
            ResourceSignature state,
            EquipmentLoadoutPosition position,
            EquipmentExactSolverOffer offer,
            out ResourceSignature allocated)
        {
            if (!transitions.TryGetValue(state, out var byTransition))
            {
                byTransition = [];
                transitions.Add(state, byTransition);
            }
            var transition = new ResourceTransitionKey(position, offer.AllocationKey);
            if (byTransition.TryGetValue(transition, out var cached))
            {
                allocated = cached!;
                return cached is not null;
            }
            if (allocationIndices.TryGetValue(offer.AllocationKey, out var allocationIndex) &&
                state.AllocationCounts[allocationIndex] >= offer.AvailableQuantity)
            {
                byTransition.Add(transition, null);
                allocated = null!;
                return false;
            }
            if (offer.Offer.Definition.IsUnique &&
                uniqueIndices.TryGetValue(offer.Offer.Definition.ItemId, out var uniqueIndex) &&
                state.UniqueItems.Contains(uniqueIndex))
            {
                byTransition.Add(transition, null);
                allocated = null!;
                return false;
            }
            allocated = AllocateCore(state, position, offer);
            byTransition.Add(transition, allocated);
            return true;
        }

        public bool CanSubstituteBaseItem(
            EquipmentExactSolverOffer candidate,
            EquipmentExactSolverOffer other,
            EquipmentLoadoutPosition position)
        {
            if (candidate.Offer.Definition.OffHandOccupancy != other.Offer.Definition.OffHandOccupancy)
                return false;
            if (HasFutureUniqueConstraint(candidate, position))
                return HasFutureUniqueConstraint(other, position) &&
                    candidate.Offer.Definition.ItemId == other.Offer.Definition.ItemId;
            return true;
        }

        public bool CanSubstituteAllocation(
            EquipmentExactSolverOffer candidate,
            EquipmentExactSolverOffer other) =>
            !allocationIndices.ContainsKey(candidate.AllocationKey) || candidate.AllocationKey == other.AllocationKey;

        public string BaseItemFutureBehaviorKey(
            EquipmentExactSolverOffer offer,
            EquipmentLoadoutPosition position) => string.Join(':',
            offer.Offer.Definition.OffHandOccupancy,
            HasFutureUniqueConstraint(offer, position) ? uniqueIndices[offer.Offer.Definition.ItemId] : -1);

        public string FutureBehaviorKey(EquipmentExactSolverOffer offer, EquipmentLoadoutPosition position) => string.Join(':',
            offer.Offer.Definition.OffHandOccupancy,
            allocationIndices.GetValueOrDefault(offer.AllocationKey, -1),
            HasFutureUniqueConstraint(offer, position) ? uniqueIndices[offer.Offer.Definition.ItemId] : -1);

        private ResourceSignature AllocateCore(
            ResourceSignature state,
            EquipmentLoadoutPosition position,
            EquipmentExactSolverOffer offer)
        {
            var allocations = state.AllocationCounts;
            if (allocationIndices.TryGetValue(offer.AllocationKey, out var allocationIndex))
            {
                allocations = (uint[])allocations.Clone();
                allocations[allocationIndex] = checked(allocations[allocationIndex] + 1);
            }
            var worlds = !string.IsNullOrWhiteSpace(offer.WorldVisitKey)
                ? state.WorldVisits.Add(worldIndices[offer.WorldVisitKey])
                : state.WorldVisits;
            var vendors = !string.IsNullOrWhiteSpace(offer.VendorStopKey)
                ? state.VendorStops.Add(vendorIndices[offer.VendorStopKey])
                : state.VendorStops;
            var unique = offer.Offer.Definition.IsUnique && uniqueIndices.TryGetValue(offer.Offer.Definition.ItemId, out var uniqueIndex)
                ? state.UniqueItems.Add(uniqueIndex)
                : state.UniqueItems;
            var occupiesOffHand = state.MainHandOccupiesOffHand ||
                position == EquipmentLoadoutPosition.MainHand && offer.Offer.Definition.OffHandOccupancy == -1;
            if (ReferenceEquals(allocations, state.AllocationCounts) &&
                ReferenceEquals(worlds, state.WorldVisits) &&
                ReferenceEquals(vendors, state.VendorStops) &&
                ReferenceEquals(unique, state.UniqueItems) &&
                occupiesOffHand == state.MainHandOccupiesOffHand)
            {
                return state;
            }
            return Intern(new(
                this,
                allocations,
                worlds,
                vendors,
                unique,
                state.SunkWorldVisitCount,
                state.SunkVendorStopCount,
                occupiesOffHand));
        }

        public ResourceProjection CreateProjection(IReadOnlyCollection<EquipmentLoadoutPosition> remainingPositions)
        {
            var futureOffers = offers.Where(offer => offer.Positions.Any(remainingPositions.Contains)).ToArray();
            var allocations = futureOffers
                .Select(offer => allocationIndices.GetValueOrDefault(offer.AllocationKey, -1))
                .Where(index => index >= 0)
                .Distinct()
                .Order()
                .ToArray();
            var unique = CompactBitSet.Empty(uniqueIndices.Count);
            foreach (var itemId in futureOffers.Where(offer => offer.Offer.Definition.IsUnique).Select(offer => offer.Offer.Definition.ItemId).Distinct())
                unique = unique.Add(uniqueIndices[itemId]);
            var worlds = CompactBitSet.Empty(worldKeys.Length);
            foreach (var key in futureOffers.Select(offer => offer.WorldVisitKey).Where(key => !string.IsNullOrWhiteSpace(key)).Cast<string>().Distinct(StringComparer.Ordinal))
                worlds = worlds.Add(worldIndices[key]);
            var vendors = CompactBitSet.Empty(vendorKeys.Length);
            foreach (var key in futureOffers.Select(offer => offer.VendorStopKey).Where(key => !string.IsNullOrWhiteSpace(key)).Cast<string>().Distinct(StringComparer.Ordinal))
                vendors = vendors.Add(vendorIndices[key]);
            return new(
                allocations,
                worlds,
                vendors,
                unique,
                remainingPositions.Contains(EquipmentLoadoutPosition.OffHand));
        }

        public ResourceSignature Project(ResourceSignature state, ResourceProjection projection)
        {
            if (projection.Projected.TryGetValue(state, out var cached))
                return cached;
            var allocations = state.AllocationCounts;
            var allocationChanged = false;
            for (var index = 0; index < allocations.Length; index++)
            {
                if (allocations[index] != 0 && !projection.AllocationIndices.Contains(index))
                {
                    allocationChanged = true;
                    break;
                }
            }
            if (allocationChanged)
            {
                allocations = (uint[])allocations.Clone();
                for (var index = 0; index < allocations.Length; index++)
                {
                    if (!projection.AllocationIndices.Contains(index))
                        allocations[index] = 0;
                }
            }
            var worlds = state.WorldVisits.Intersect(projection.WorldMask);
            var vendors = state.VendorStops.Intersect(projection.VendorMask);
            var unique = state.UniqueItems.Intersect(projection.UniqueMask);
            var sunkWorlds = checked(state.SunkWorldVisitCount + state.WorldVisits.Count - worlds.Count);
            var sunkVendors = checked(state.SunkVendorStopCount + state.VendorStops.Count - vendors.Count);
            var occupiesOffHand = state.MainHandOccupiesOffHand && projection.KeepMainHandOccupancy;
            if (ReferenceEquals(allocations, state.AllocationCounts) &&
                ReferenceEquals(worlds, state.WorldVisits) &&
                ReferenceEquals(vendors, state.VendorStops) &&
                ReferenceEquals(unique, state.UniqueItems) &&
                occupiesOffHand == state.MainHandOccupiesOffHand)
            {
                projection.Projected.Add(state, state);
                return state;
            }
            var projected = Intern(new(
                this,
                allocations,
                worlds,
                vendors,
                unique,
                sunkWorlds,
                sunkVendors,
                occupiesOffHand));
            projection.Projected.Add(state, projected);
            return projected;
        }

        public string CanonicalWorldText(CompactBitSet values) => values.CanonicalValuesText(worldKeys);
        public string CanonicalVendorText(CompactBitSet values) => values.CanonicalValuesText(vendorKeys);

        private bool HasFutureUniqueConstraint(EquipmentExactSolverOffer offer, EquipmentLoadoutPosition position) =>
            offer.Offer.Definition.IsUnique && offers.Any(candidate =>
                candidate.Offer.Definition.IsUnique &&
                candidate.Offer.Definition.ItemId == offer.Offer.Definition.ItemId &&
                candidate.Positions.Any(candidatePosition => candidatePosition != position && requiredPositions.Contains(candidatePosition)));

        private ResourceSignature Intern(ResourceSignature signature)
        {
            if (interned.TryGetValue(signature, out var existing))
                return existing;
            interned.Add(signature, signature);
            return signature;
        }
    }

    private readonly record struct ResourceTransitionKey(
        EquipmentLoadoutPosition Position,
        EquipmentOfferAllocationKey AllocationKey);

    private sealed class ResourceSignature(
        RequestResourcePool owner,
        uint[] allocationCounts,
        CompactBitSet worldVisits,
        CompactBitSet vendorStops,
        CompactBitSet uniqueItems,
        int sunkWorldVisitCount,
        int sunkVendorStopCount,
        bool mainHandOccupiesOffHand)
    {
        public uint[] AllocationCounts { get; } = allocationCounts;
        public CompactBitSet WorldVisits { get; } = worldVisits;
        public CompactBitSet VendorStops { get; } = vendorStops;
        public CompactBitSet UniqueItems { get; } = uniqueItems;
        public int SunkWorldVisitCount { get; } = sunkWorldVisitCount;
        public int SunkVendorStopCount { get; } = sunkVendorStopCount;
        public int WorldVisitCount => checked(SunkWorldVisitCount + WorldVisits.Count);
        public int VendorStopCount => checked(SunkVendorStopCount + VendorStops.Count);
        public bool MainHandOccupiesOffHand { get; } = mainHandOccupiesOffHand;
        public string CanonicalWorldText => $"{SunkWorldVisitCount}:{owner.CanonicalWorldText(WorldVisits)}";
        public string CanonicalVendorText => $"{SunkVendorStopCount}:{owner.CanonicalVendorText(VendorStops)}";

        public string CanonicalAllocationText(IReadOnlyList<int> indices) =>
            string.Join('|', indices.Select(index => $"{index}:{AllocationCounts[index]}"));

        public ulong ActiveAllocationCount(IReadOnlyList<int> indices) => indices.Aggregate(
            0UL,
            (sum, index) => checked(sum + AllocationCounts[index]));

        public bool TryGetFastResourceKey(ResourceProjection projection, out FastResourceKey key)
        {
            key = default;
            if (!WorldVisits.TryGetSingleWord(out var worlds) ||
                !VendorStops.TryGetSingleWord(out var vendors) ||
                !UniqueItems.Intersect(projection.UniqueMask).TryGetSingleWord(out var unique))
                return false;
            var allocationIndex = -1;
            uint allocationCount = 0;
            foreach (var index in projection.AllocationIndices)
            {
                if (AllocationCounts[index] == 0)
                    continue;
                if (allocationIndex >= 0)
                    return false;
                allocationIndex = index;
                allocationCount = AllocationCounts[index];
            }
            key = new(
                worlds,
                vendors,
                unique,
                allocationIndex,
                allocationCount,
                SunkWorldVisitCount,
                SunkVendorStopCount,
                projection.KeepMainHandOccupancy && MainHandOccupiesOffHand);
            return true;
        }
    }

    private sealed class ResourceSignatureComparer : IEqualityComparer<ResourceSignature>
    {
        public static ResourceSignatureComparer Instance { get; } = new();

        public bool Equals(ResourceSignature? x, ResourceSignature? y) =>
            ReferenceEquals(x, y) || x is not null && y is not null &&
            x.MainHandOccupiesOffHand == y.MainHandOccupiesOffHand &&
            x.SunkWorldVisitCount == y.SunkWorldVisitCount &&
            x.SunkVendorStopCount == y.SunkVendorStopCount &&
            x.AllocationCounts.AsSpan().SequenceEqual(y.AllocationCounts) &&
            x.WorldVisits.Equals(y.WorldVisits) &&
            x.VendorStops.Equals(y.VendorStops) &&
            x.UniqueItems.Equals(y.UniqueItems);

        public int GetHashCode(ResourceSignature value)
        {
            var hash = new HashCode();
            hash.Add(value.MainHandOccupiesOffHand);
            hash.Add(value.SunkWorldVisitCount);
            hash.Add(value.SunkVendorStopCount);
            foreach (var count in value.AllocationCounts)
                hash.Add(count);
            hash.Add(value.WorldVisits);
            hash.Add(value.VendorStops);
            hash.Add(value.UniqueItems);
            return hash.ToHashCode();
        }
    }

    private sealed class CompactBitSet : IEquatable<CompactBitSet>
    {
        private readonly ulong[] words;

        private CompactBitSet(ulong[] words) => this.words = words;

        public int Count => words.Sum(BitOperations.PopCount);

        public int CountMasked(CompactBitSet mask)
        {
            var count = 0;
            for (var index = 0; index < words.Length; index++)
                count = checked(count + BitOperations.PopCount(words[index] & mask.words[index]));
            return count;
        }

        public static CompactBitSet Empty(int bitCount) => new(new ulong[(bitCount + 63) / 64]);

        public bool Contains(int index) => (words[index >> 6] & 1UL << (index & 63)) != 0;

        public CompactBitSet Add(int index)
        {
            var wordIndex = index >> 6;
            var mask = 1UL << (index & 63);
            if ((words[wordIndex] & mask) != 0)
                return this;
            var copy = (ulong[])words.Clone();
            copy[wordIndex] |= mask;
            return new(copy);
        }

        public CompactBitSet Intersect(CompactBitSet mask)
        {
            if (IsSubsetOf(mask))
                return this;
            var result = new ulong[words.Length];
            for (var index = 0; index < words.Length; index++)
                result[index] = words[index] & mask.words[index];
            return new(result);
        }

        public bool IsSubsetOf(CompactBitSet other)
        {
            for (var index = 0; index < words.Length; index++)
            {
                if ((words[index] & ~other.words[index]) != 0)
                    return false;
            }
            return true;
        }

        public bool IsSubsetOf(CompactBitSet other, CompactBitSet mask)
        {
            for (var index = 0; index < words.Length; index++)
            {
                if ((words[index] & mask.words[index] & ~other.words[index]) != 0)
                    return false;
            }
            return true;
        }

        public string CanonicalText(CompactBitSet mask) => string.Join(',', EnumerateSetBits(mask));

        public bool TryGetSingleWord(out ulong word)
        {
            word = words.Length == 0 ? 0 : words[0];
            for (var index = 1; index < words.Length; index++)
            {
                if (words[index] != 0)
                    return false;
            }
            return true;
        }

        public string CanonicalValuesText(IReadOnlyList<string> values) =>
            string.Join('|', EnumerateSetBits().Select(index => values[index]));

        public bool Equals(CompactBitSet? other) => other is not null && words.AsSpan().SequenceEqual(other.words);
        public override bool Equals(object? obj) => obj is CompactBitSet other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var word in words)
                hash.Add(word);
            return hash.ToHashCode();
        }

        private IEnumerable<int> EnumerateSetBits(CompactBitSet? mask = null)
        {
            for (var wordIndex = 0; wordIndex < words.Length; wordIndex++)
            {
                var word = words[wordIndex] & (mask?.words[wordIndex] ?? ulong.MaxValue);
                while (word != 0)
                {
                    var bit = BitOperations.TrailingZeroCount(word);
                    yield return wordIndex * 64 + bit;
                    word &= word - 1;
                }
            }
        }
    }

    private sealed record State(
        RetainedPathNode ParentPaths,
        IReadOnlyList<EquipmentLoadoutSelection>? PendingSelections,
        EquipmentSolverUtilityVector Utility,
        ulong Cost,
        int PurchaseTransactions,
        EquipmentEvidenceRisk EvidenceRisk,
        ResourceSignature Resources,
        bool IsBaselineSoFar)
    {
        private RetainedPathNode? materializedPaths;

        public RetainedPathNode Paths => materializedPaths ??= PendingSelections is null
            ? ParentPaths
            : ParentPaths.AppendAlternatives(PendingSelections);

        public State WithPaths(RetainedPathNode paths) => new(
            paths,
            null,
            Utility,
            Cost,
            PurchaseTransactions,
            EvidenceRisk,
            Resources,
            IsBaselineSoFar);

        public static State Empty(
            ResourceSignature resources,
            EquipmentSolverUtilityVector utility) => new(
            RetainedPathNode.Empty,
            null,
            utility,
            0,
            0,
            new(0, 0, 0),
            resources,
            true);
    }
}
