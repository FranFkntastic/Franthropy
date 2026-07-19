using System.Diagnostics;
using System.Security.Cryptography;
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
        return new EquipmentSolverUtilityVector(Components.Concat(other.Components).ToArray()).Normalize();
    }

    public long Get(string key) => Components.FirstOrDefault(component => string.Equals(component.Key, key, StringComparison.Ordinal))?.Units ?? 0;

    public string CanonicalText => string.Join('|', Normalize().Components.Select(component => $"{component.Key}:{component.Units}"));
}

public sealed record EquipmentPartialUtilityDominance(
    bool IsNoWorse,
    bool IsStrictlyBetter);

public interface IEquipmentExactSolverUtilityModel
{
    EquipmentPartialUtilityDominance ComparePartial(
        EquipmentSolverUtilityVector candidate,
        EquipmentSolverUtilityVector other);

    EquipmentUtilityEvaluation Evaluate(EquipmentSolverUtilityVector completed);
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
    public EquipmentOfferAllocationKey AllocationKey => new(Offer.Key, ObservationId);
}

public sealed record EquipmentExactFrontierRequest(
    IReadOnlyList<EquipmentExactSolverOffer> Offers,
    IReadOnlySet<EquipmentLoadoutPosition> RequiredPositions,
    IReadOnlyDictionary<EquipmentLoadoutPosition, EquipmentOfferAllocationKey?> Baseline,
    IEquipmentExactSolverUtilityModel UtilityModel,
    int MaxEquivalentRepresentatives = 16);

public sealed record EquipmentExactFrontierDiagnostics(
    long ExpandedStateCount,
    long InfeasibleTransitionCount,
    long DominatedStateCount,
    long CompactedEquivalentStateCount,
    int PeakRetainedStateCount,
    int CompleteSolutionCount,
    long ExactCompleteVariantCount,
    int EquivalentRepresentativeLimit,
    string BaselineSolutionId,
    TimeSpan Elapsed);

public sealed record EquipmentExactFrontierProgress(
    int CompletedPositionCount,
    int TotalPositionCount,
    EquipmentLoadoutPosition Position,
    long ExpandedStateCount,
    long DominatedStateCount,
    long CompactedEquivalentStateCount,
    int CandidateStateCount,
    int RetainedStateCount,
    TimeSpan Elapsed);

public sealed record EquipmentExactEquivalenceSummary(
    string ClassId,
    long ExactVariantCount,
    IReadOnlyList<string> RepresentativeSolutionIds)
{
    public bool RepresentativesTruncated => ExactVariantCount > RepresentativeSolutionIds.Count;
}

public sealed record EquipmentExactFrontierResult(
    EquipmentParetoResult Pareto,
    EquipmentExactFrontierDiagnostics Diagnostics,
    IReadOnlyList<EquipmentExactEquivalenceSummary> EquivalenceSummaries);

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
        var offersByPosition = positions.ToDictionary(
            position => position,
            position => orderedOffers.Where(offer => offer.Positions.Contains(position)).ToArray());
        ValidateBaseline(request, orderedOffers);

        var states = new List<State> { State.Empty };
        long expanded = 0;
        long infeasible = 0;
        long dominated = 0;
        long compacted = 0;
        var peak = 1;
        for (var positionIndex = 0; positionIndex < positions.Length; positionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = positions[positionIndex];
            var next = new List<State>();
            foreach (var state in states)
            {
                if (position == EquipmentLoadoutPosition.OffHand && state.MainHandOccupiesOffHand)
                {
                    expanded++;
                    next.Add(state with
                    {
                        IsBaselineSoFar = state.IsBaselineSoFar && request.Baseline[position] is null,
                    });
                    continue;
                }

                foreach (var offer in offersByPosition[position])
                {
                    expanded++;
                    if (!CanAllocate(state, offer))
                    {
                        infeasible++;
                        continue;
                    }
                    next.Add(Allocate(state, position, offer, request.Baseline[position]));
                }
            }
            if (next.Count == 0)
                throw new InvalidOperationException($"No feasible loadout state can fill {position}.");

            var remainingPositions = positions.Skip(positionIndex + 1).ToArray();
            var futureOffers = remainingPositions.SelectMany(remaining => offersByPosition[remaining]).ToArray();
            var futureAllocations = futureOffers.Select(offer => offer.AllocationKey).ToHashSet();
            var futureUniqueItems = futureOffers.Where(offer => offer.Offer.Definition.IsUnique).Select(offer => offer.Offer.Definition.ItemId).ToHashSet();
            states = cancellationToken.CanBeCanceled
                ? PruneCancellable(
                    next,
                    request.UtilityModel,
                    futureAllocations,
                    futureUniqueItems,
                    request.MaxEquivalentRepresentatives,
                    cancellationToken,
                    ref dominated,
                    ref compacted)
                : Prune(
                    next,
                    request.UtilityModel,
                    futureAllocations,
                    futureUniqueItems,
                    request.MaxEquivalentRepresentatives,
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

        var feasibilityOffers = orderedOffers
            .Select(offer => new EquipmentFeasibilityOffer(offer.Offer, offer.AvailableQuantity, offer.ObservationId))
            .ToArray();
        var offersByAllocation = orderedOffers.ToDictionary(offer => offer.AllocationKey);
        var primaryDecisions = new List<EquipmentDecisionSolution>(states.Count);
        var statesByPrimaryId = new Dictionary<string, State>(StringComparer.Ordinal);
        var equivalenceSummaries = new List<EquipmentExactEquivalenceSummary>();
        foreach (var state in states.OrderBy(CanonicalStateText, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var representativeIds = state.RepresentativeSelections
                .Select(selections => state.IsBaselineSoFar ? "baseline" : SolutionId(selections))
                .ToArray();
            var primary = Materialize(
                state,
                state.RepresentativeSelections[0],
                representativeIds[0],
                request,
                feasibilityOffers,
                offersByAllocation);
            primaryDecisions.Add(primary);
            statesByPrimaryId[primary.Candidate.SolutionId] = state;
            if (state.EquivalentPathCount > 1)
                equivalenceSummaries.Add(new(
                    $"exact:{CanonicalMetricText(state)}",
                    state.EquivalentPathCount,
                    representativeIds));
        }

        if (primaryDecisions.All(solution => !string.Equals(solution.Candidate.SolutionId, "baseline", StringComparison.Ordinal)))
            throw new InvalidOperationException("The no-purchase baseline was not preserved through frontier generation.");
        var corePareto = new EquipmentParetoFrontierBuilder().Build(primaryDecisions);
        var expandedFrontier = new List<EquipmentDecisionSolution>();
        foreach (var core in corePareto.Frontier)
        {
            var state = statesByPrimaryId[core.Candidate.SolutionId];
            foreach (var selections in state.RepresentativeSelections)
            {
                var solutionId = state.IsBaselineSoFar ? "baseline" : SolutionId(selections);
                expandedFrontier.Add(Materialize(
                    state,
                    selections,
                    solutionId,
                    request,
                    feasibilityOffers,
                    offersByAllocation));
            }
        }
        var pareto = new EquipmentParetoResult(
            expandedFrontier,
            corePareto.Dominated,
            BuildExplicitEquivalenceGroups(expandedFrontier),
            corePareto.Adjacencies);
        var normalizedEquivalenceSummaries = equivalenceSummaries
            .GroupBy(summary => summary.ClassId, StringComparer.Ordinal)
            .Select(group => new EquipmentExactEquivalenceSummary(
                group.Key,
                group.Aggregate(0L, (sum, summary) => checked(sum + summary.ExactVariantCount)),
                group.SelectMany(summary => summary.RepresentativeSolutionIds)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .Take(request.MaxEquivalentRepresentatives)
                    .ToArray()))
            .ToArray();
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
                states.Sum(state => state.EquivalentPathCount),
                request.MaxEquivalentRepresentatives,
                "baseline",
                stopwatch.Elapsed),
            normalizedEquivalenceSummaries);
    }

    private static EquipmentDecisionSolution Materialize(
        State state,
        IReadOnlyList<EquipmentLoadoutSelection> selections,
        string solutionId,
        EquipmentExactFrontierRequest request,
        IReadOnlyList<EquipmentFeasibilityOffer> feasibilityOffers,
        IReadOnlyDictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer> offersByAllocation)
    {
        var candidate = new EquipmentLoadoutCandidate(solutionId, selections);
        var feasibility = new EquipmentLoadoutFeasibilityEvaluator().Evaluate(new(
            candidate,
            feasibilityOffers,
            request.RequiredPositions));
        if (!feasibility.IsFeasible)
            throw new InvalidOperationException($"Solver emitted infeasible solution '{solutionId}': {string.Join("; ", feasibility.Violations.Select(value => value.Message))}");
        var labels = selections
            .Select(selection => offersByAllocation[selection.AllocationKey])
            .SelectMany(offer => offer.VariantLabels)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var evaluation = request.UtilityModel.Evaluate(state.Utility);
        if (!double.IsFinite(evaluation.UtilityScore))
            throw new InvalidOperationException("Exact solver utility models must emit finite deterministic utility scores.");
        return new(
            candidate,
            evaluation,
            state.Cost,
            new(state.WorldVisits.Count, state.VendorStops.Count, state.PurchaseTransactions),
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
        IReadOnlySet<EquipmentOfferAllocationKey> futureAllocations,
        IReadOnlySet<uint> futureUniqueItems,
        int maxEquivalentRepresentatives,
        ref long dominatedCount,
        ref long compactedCount)
    {
        var ordered = OrderForDominancePruning(candidates);
        var retained = new List<State>(ordered.Length);
        foreach (var candidate in ordered)
        {
            if (!candidate.IsBaselineSoFar && retained.Any(other => DominatesPartial(
                other,
                candidate,
                utilityModel,
                futureAllocations,
                futureUniqueItems)))
            {
                dominatedCount++;
                continue;
            }

            for (var index = retained.Count - 1; index >= 0; index--)
            {
                if (retained[index].IsBaselineSoFar)
                    continue;
                if (DominatesPartial(candidate, retained[index], utilityModel, futureAllocations, futureUniqueItems))
                {
                    retained.RemoveAt(index);
                    dominatedCount++;
                }
            }
            retained.Add(candidate);
        }
        return CompactEquivalent(retained, futureAllocations, futureUniqueItems, maxEquivalentRepresentatives, ref compactedCount);
    }

    private static List<State> PruneCancellable(
        IReadOnlyList<State> candidates,
        IEquipmentExactSolverUtilityModel utilityModel,
        IReadOnlySet<EquipmentOfferAllocationKey> futureAllocations,
        IReadOnlySet<uint> futureUniqueItems,
        int maxEquivalentRepresentatives,
        CancellationToken cancellationToken,
        ref long dominatedCount,
        ref long compactedCount)
    {
        var ordered = OrderForDominancePruning(candidates);
        var retained = new List<State>(ordered.Length);
        foreach (var candidate in ordered)
        {
            if (cancellationToken.CanBeCanceled)
                cancellationToken.ThrowIfCancellationRequested();
            var isDominated = !candidate.IsBaselineSoFar && (cancellationToken.CanBeCanceled
                ? AnyDominatesCancellable(
                    retained,
                    candidate,
                    utilityModel,
                    futureAllocations,
                    futureUniqueItems,
                    cancellationToken)
                : retained.Any(other => DominatesPartial(
                    other,
                    candidate,
                    utilityModel,
                    futureAllocations,
                    futureUniqueItems)));
            if (isDominated)
            {
                dominatedCount++;
                continue;
            }

            if (!cancellationToken.CanBeCanceled)
            {
                for (var index = retained.Count - 1; index >= 0; index--)
                {
                    if (retained[index].IsBaselineSoFar)
                        continue;
                    if (DominatesPartial(candidate, retained[index], utilityModel, futureAllocations, futureUniqueItems))
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
                    if (DominatesPartial(candidate, retained[index], utilityModel, futureAllocations, futureUniqueItems))
                    {
                        retained.RemoveAt(index);
                        dominatedCount++;
                    }
                }
            }
            retained.Add(candidate);
        }
        return CompactEquivalentCancellable(
            retained,
            futureAllocations,
            futureUniqueItems,
            maxEquivalentRepresentatives,
            cancellationToken,
            ref compactedCount);
    }

    private static bool AnyDominatesCancellable(
        IReadOnlyList<State> retained,
        State candidate,
        IEquipmentExactSolverUtilityModel utilityModel,
        IReadOnlySet<EquipmentOfferAllocationKey> futureAllocations,
        IReadOnlySet<uint> futureUniqueItems,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < retained.Count; index++)
        {
            if ((index & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (DominatesPartial(retained[index], candidate, utilityModel, futureAllocations, futureUniqueItems))
                return true;
        }
        return false;
    }

    private static State[] OrderForDominancePruning(IReadOnlyList<State> candidates) => candidates
        .OrderByDescending(UtilityMagnitude)
        .ThenBy(state => state.Cost)
        .ThenBy(state => state.EvidenceRisk.FreshnessBucket)
        .ThenBy(state => state.EvidenceRisk.IncompleteCoverageCount)
        .ThenBy(state => state.EvidenceRisk.ConfidencePenalty)
        .ThenBy(state => state.PurchaseTransactions)
        .ThenBy(state => state.WorldVisits.Count)
        .ThenBy(state => state.VendorStops.Count)
        .ThenBy(CanonicalStateText, StringComparer.Ordinal)
        .ToArray();

    private static long UtilityMagnitude(State state) => state.Utility.Components.Aggregate(
        0L,
        (sum, component) => checked(sum + component.Units));

    private static List<State> CompactEquivalent(
        IReadOnlyList<State> states,
        IReadOnlySet<EquipmentOfferAllocationKey> futureAllocations,
        IReadOnlySet<uint> futureUniqueItems,
        int maxEquivalentRepresentatives,
        ref long compactedCount)
    {
        var compacted = new List<State>();
        foreach (var group in states.GroupBy(state => EquivalenceKey(state, futureAllocations, futureUniqueItems), StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(CanonicalStateText, StringComparer.Ordinal).ToArray();
            var representativeSelections = ordered
                .SelectMany(state => state.RepresentativeSelections)
                .DistinctBy(CanonicalSelectionsText, StringComparer.Ordinal)
                .OrderBy(CanonicalSelectionsText, StringComparer.Ordinal)
                .Take(maxEquivalentRepresentatives)
                .ToArray();
            var exactCount = ordered.Aggregate(0L, (sum, state) => checked(sum + state.EquivalentPathCount));
            compacted.Add(ordered[0] with
            {
                Selections = representativeSelections[0],
                RepresentativeSelections = representativeSelections,
                EquivalentPathCount = exactCount,
            });
            compactedCount += ordered.Length - 1;
        }
        return compacted.OrderBy(CanonicalStateText, StringComparer.Ordinal).ToList();
    }

    private static List<State> CompactEquivalentCancellable(
        IReadOnlyList<State> states,
        IReadOnlySet<EquipmentOfferAllocationKey> futureAllocations,
        IReadOnlySet<uint> futureUniqueItems,
        int maxEquivalentRepresentatives,
        CancellationToken cancellationToken,
        ref long compactedCount)
    {
        var compacted = new List<State>();
        foreach (var group in states.GroupBy(state => EquivalenceKey(state, futureAllocations, futureUniqueItems), StringComparer.Ordinal))
        {
            if (cancellationToken.CanBeCanceled)
                cancellationToken.ThrowIfCancellationRequested();
            var ordered = group.OrderBy(CanonicalStateText, StringComparer.Ordinal).ToArray();
            var representativeSelections = ordered
                .SelectMany(state => state.RepresentativeSelections)
                .DistinctBy(CanonicalSelectionsText, StringComparer.Ordinal)
                .OrderBy(CanonicalSelectionsText, StringComparer.Ordinal)
                .Take(maxEquivalentRepresentatives)
                .ToArray();
            var exactCount = ordered.Aggregate(0L, (sum, state) => checked(sum + state.EquivalentPathCount));
            compacted.Add(ordered[0] with
            {
                Selections = representativeSelections[0],
                RepresentativeSelections = representativeSelections,
                EquivalentPathCount = exactCount,
            });
            compactedCount += ordered.Length - 1;
        }
        return compacted.OrderBy(CanonicalStateText, StringComparer.Ordinal).ToList();
    }

    private static bool DominatesPartial(
        State candidate,
        State other,
        IEquipmentExactSolverUtilityModel utilityModel,
        IReadOnlySet<EquipmentOfferAllocationKey> futureAllocations,
        IReadOnlySet<uint> futureUniqueItems)
    {
        var utility = utilityModel.ComparePartial(candidate.Utility, other.Utility);
        if (!utility.IsNoWorse ||
            candidate.Cost > other.Cost ||
            !candidate.EvidenceRisk.IsNoWorseThan(other.EvidenceRisk) ||
            candidate.PurchaseTransactions > other.PurchaseTransactions ||
            !candidate.WorldVisits.IsSubsetOf(other.WorldVisits) ||
            !candidate.VendorStops.IsSubsetOf(other.VendorStops))
            return false;

        foreach (var key in futureAllocations)
        {
            if (candidate.Allocations.GetValueOrDefault(key) > other.Allocations.GetValueOrDefault(key))
                return false;
        }
        if (!candidate.UniqueItemIds.Where(futureUniqueItems.Contains).ToHashSet().IsSubsetOf(
            other.UniqueItemIds.Where(futureUniqueItems.Contains)))
            return false;

        return utility.IsStrictlyBetter ||
            candidate.Cost < other.Cost ||
            candidate.EvidenceRisk.IsStrictlyBetterThan(other.EvidenceRisk) ||
            candidate.PurchaseTransactions < other.PurchaseTransactions ||
            candidate.WorldVisits.Count < other.WorldVisits.Count ||
            candidate.VendorStops.Count < other.VendorStops.Count;
    }

    private static bool CanAllocate(State state, EquipmentExactSolverOffer offer)
    {
        var used = state.Allocations.GetValueOrDefault(offer.AllocationKey);
        if (used >= offer.AvailableQuantity)
            return false;
        return !offer.Offer.Definition.IsUnique || !state.UniqueItemIds.Contains(offer.Offer.Definition.ItemId);
    }

    private static State Allocate(
        State state,
        EquipmentLoadoutPosition position,
        EquipmentExactSolverOffer offer,
        EquipmentOfferAllocationKey? baseline)
    {
        var allocations = new Dictionary<EquipmentOfferAllocationKey, uint>(state.Allocations)
        {
            [offer.AllocationKey] = checked(state.Allocations.GetValueOrDefault(offer.AllocationKey) + 1),
        };
        var unique = new HashSet<uint>(state.UniqueItemIds);
        if (offer.Offer.Definition.IsUnique)
            unique.Add(offer.Offer.Definition.ItemId);
        var worlds = new HashSet<string>(state.WorldVisits, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(offer.WorldVisitKey))
            worlds.Add(offer.WorldVisitKey);
        var vendors = new HashSet<string>(state.VendorStops, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(offer.VendorStopKey))
            vendors.Add(offer.VendorStopKey);
        var labels = new HashSet<string>(state.VariantLabels, StringComparer.Ordinal);
        labels.UnionWith(offer.VariantLabels);
        var representativeSelections = state.RepresentativeSelections
            .Select(selections => (IReadOnlyList<EquipmentLoadoutSelection>)[.. selections, new(position, offer.Offer.Key, 1, offer.ObservationId)])
            .ToArray();
        return new(
            representativeSelections[0],
            representativeSelections,
            state.EquivalentPathCount,
            state.Utility.Add(offer.Utility),
            checked(state.Cost + offer.AcquisitionCostGil),
            worlds,
            vendors,
            checked(state.PurchaseTransactions + offer.PurchaseTransactions),
            new(
                Math.Max(state.EvidenceRisk.FreshnessBucket, offer.EvidenceRisk.FreshnessBucket),
                checked(state.EvidenceRisk.IncompleteCoverageCount + offer.EvidenceRisk.IncompleteCoverageCount),
                Math.Max(state.EvidenceRisk.ConfidencePenalty, offer.EvidenceRisk.ConfidencePenalty)),
            allocations,
            unique,
            state.MainHandOccupiesOffHand ||
                position == EquipmentLoadoutPosition.MainHand && offer.Offer.Definition.OffHandOccupancy == -1,
            state.IsBaselineSoFar && baseline == offer.AllocationKey,
            labels);
    }

    private static void Validate(EquipmentExactFrontierRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Offers);
        ArgumentNullException.ThrowIfNull(request.RequiredPositions);
        ArgumentNullException.ThrowIfNull(request.Baseline);
        if (request.RequiredPositions.Count == 0)
            throw new ArgumentException("At least one required equipment position is needed.", nameof(request));
        if (request.RequiredPositions.Any(position => !CanonicalPositions.Contains(position)))
            throw new ArgumentException("Request contains an unsupported equipment position.", nameof(request));
        if (request.MaxEquivalentRepresentatives is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(request.MaxEquivalentRepresentatives));
        var duplicate = request.Offers.GroupBy(offer => offer.AllocationKey).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Duplicate exact solver offer '{duplicate.Key}'.", nameof(request));
        foreach (var offer in request.Offers)
        {
            if (offer.AvailableQuantity == 0)
                throw new ArgumentException($"Offer '{offer.AllocationKey}' has zero available quantity.", nameof(request));
            if (offer.Positions.Count == 0)
                throw new ArgumentException($"Offer '{offer.AllocationKey}' has no equipment positions.", nameof(request));
            if (offer.PurchaseTransactions < 0)
                throw new ArgumentException($"Offer '{offer.AllocationKey}' has a negative purchase burden.", nameof(request));
            _ = offer.Utility.Normalize();
        }
    }

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

    private static string CanonicalStateText(State state) => string.Join('|', state.Selections.OrderBy(selection => selection.Position).Select(selection =>
        $"{selection.Position}:{selection.OfferKey.ItemId}:{selection.OfferKey.Quality}:{selection.OfferKey.SourceKind}:{selection.OfferKey.SourceCatalogKey}:{selection.ObservationId}"));

    private static string CanonicalSelectionsText(IReadOnlyList<EquipmentLoadoutSelection> selections) => string.Join('|', selections.OrderBy(selection => selection.Position).Select(selection =>
        $"{selection.Position}:{selection.OfferKey.ItemId}:{selection.OfferKey.Quality}:{selection.OfferKey.SourceKind}:{selection.OfferKey.SourceCatalogKey}:{selection.ObservationId}:{selection.Quantity}"));

    private static string EquivalenceKey(
        State state,
        IReadOnlySet<EquipmentOfferAllocationKey> futureAllocations,
        IReadOnlySet<uint> futureUniqueItems) => string.Join("||",
            CanonicalMetricText(state),
            state.MainHandOccupiesOffHand,
            state.IsBaselineSoFar,
            string.Join('|', futureAllocations.OrderBy(AllocationText, StringComparer.Ordinal).Select(key => $"{AllocationText(key)}:{state.Allocations.GetValueOrDefault(key)}")),
            string.Join('|', state.UniqueItemIds.Where(futureUniqueItems.Contains).Order()));

    private static string CanonicalMetricText(State state) => string.Join("||",
        state.Utility.CanonicalText,
        state.Cost,
        state.PurchaseTransactions,
        state.EvidenceRisk.FreshnessBucket,
        state.EvidenceRisk.IncompleteCoverageCount,
        state.EvidenceRisk.ConfidencePenalty,
        string.Join('|', state.WorldVisits.Order(StringComparer.Ordinal)),
        string.Join('|', state.VendorStops.Order(StringComparer.Ordinal)));

    private static string AllocationText(EquipmentOfferAllocationKey key) =>
        $"{key.OfferKey.ItemId}:{key.OfferKey.Quality}:{key.OfferKey.SourceKind}:{key.OfferKey.SourceCatalogKey}:{key.ObservationId}";

    private sealed record ExplicitEquivalenceKey(
        string ProfileId,
        string ProfileVersion,
        string ContextId,
        uint ClassJobId,
        uint CharacterLevel,
        ulong Cost,
        long UtilityUnits);

    private sealed record State(
        IReadOnlyList<EquipmentLoadoutSelection> Selections,
        IReadOnlyList<IReadOnlyList<EquipmentLoadoutSelection>> RepresentativeSelections,
        long EquivalentPathCount,
        EquipmentSolverUtilityVector Utility,
        ulong Cost,
        IReadOnlySet<string> WorldVisits,
        IReadOnlySet<string> VendorStops,
        int PurchaseTransactions,
        EquipmentEvidenceRisk EvidenceRisk,
        IReadOnlyDictionary<EquipmentOfferAllocationKey, uint> Allocations,
        IReadOnlySet<uint> UniqueItemIds,
        bool MainHandOccupiesOffHand,
        bool IsBaselineSoFar,
        IReadOnlySet<string> VariantLabels)
    {
        public static State Empty { get; } = new(
            [],
            [[]],
            1,
            EquipmentSolverUtilityVector.Empty,
            0,
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            0,
            new(0, 0, 0),
            new Dictionary<EquipmentOfferAllocationKey, uint>(),
            new HashSet<uint>(),
            false,
            true,
            new HashSet<string>(StringComparer.Ordinal));
    }
}
