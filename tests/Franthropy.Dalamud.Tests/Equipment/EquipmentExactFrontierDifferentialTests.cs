using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.Tests.Equipment.Reference;

namespace Franthropy.Dalamud.Tests.Equipment;

public sealed class EquipmentExactFrontierDifferentialTests
{
    [Fact]
    public void ProductionSolver_PreservesCanonicalAuthoritativeFrontierAcrossRandomizedRequests()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var request = Request(seed);

            var expected = new EquipmentExactFrontierReferenceSolver().Solve(request);
            var actual = new EquipmentExactFrontierSolver().Solve(request);

            Assert.Equal(Contract(expected), Contract(actual));
        }
    }

    [Fact]
    public void ProductionSolver_PreservesJoinedTextOrderingAcrossSelectionBoundaries()
    {
        var baselineHead = Offer(EquipmentLoadoutPosition.Head, 900, [0, 0, 0], 0, EquipmentAcquisitionSourceKind.Owned, null, null, 0);
        var baselineBody = Offer(EquipmentLoadoutPosition.Body, 901, [0, 0, 0], 0, EquipmentAcquisitionSourceKind.Owned, null, null, 0);
        var prefix = Offer(EquipmentLoadoutPosition.Head, 1_000, [20, 20, 5], 100, EquipmentAcquisitionSourceKind.MarketBoard, "x", null, 0, "shared");
        var extended = Offer(EquipmentLoadoutPosition.Head, 1_000, [20, 20, 5], 100, EquipmentAcquisitionSourceKind.MarketBoard, "x:1!", null, 0, "shared");
        var request = new EquipmentExactFrontierRequest(
            [baselineHead, baselineBody, prefix, extended],
            new HashSet<EquipmentLoadoutPosition> { EquipmentLoadoutPosition.Head, EquipmentLoadoutPosition.Body },
            new Dictionary<EquipmentLoadoutPosition, EquipmentOfferAllocationKey?>
            {
                [EquipmentLoadoutPosition.Head] = baselineHead.AllocationKey,
                [EquipmentLoadoutPosition.Body] = baselineBody.AllocationKey,
            },
            new ComponentwiseUtilityModel(),
            MaxRetainedRepresentatives: 4);

        var expected = new EquipmentExactFrontierReferenceSolver().Solve(request);
        var actual = new EquipmentExactFrontierSolver().Solve(request);

        Assert.Equal(Contract(expected), Contract(actual));
        var observationIds = actual.Pareto.Frontier
            .Where(solution => solution.Candidate.Selections.Any(selection => selection.OfferKey.ItemId == 1_000))
            .Select(solution => solution.Candidate.Selections.Single(selection => selection.OfferKey.ItemId == 1_000).ObservationId)
            .ToArray();
        Assert.Collection(
            observationIds,
            value => Assert.Equal("x:1!", value),
            value => Assert.Equal("x", value));
    }

    private static EquipmentExactFrontierRequest Request(int seed)
    {
        var random = new Random(seed);
        var positions = new[]
        {
            EquipmentLoadoutPosition.Head,
            EquipmentLoadoutPosition.Body,
            EquipmentLoadoutPosition.Hands,
        };
        var offers = new List<EquipmentExactSolverOffer>();
        var baseline = new Dictionary<EquipmentLoadoutPosition, EquipmentOfferAllocationKey?>();
        uint itemId = 1_000;
        foreach (var position in positions)
        {
            var owned = Offer(position, itemId++, [10, 10, 5], 0, EquipmentAcquisitionSourceKind.Owned, null, null, 0);
            offers.Add(owned);
            baseline[position] = owned.AllocationKey;
            var count = random.Next(1, 4);
            for (var index = 0; index < count; index++)
            {
                offers.Add(Offer(
                    position,
                    itemId++,
                    [random.Next(5, 35), random.Next(5, 35), random.Next(0, 15)],
                    checked((ulong)random.Next(100, 5_000)),
                    EquipmentAcquisitionSourceKind.MarketBoard,
                    $"observation-{seed}-{position}-{index}",
                    $"world-{random.Next(0, 3)}",
                    random.Next(0, 3)));
            }
        }
        return new(
            offers.OrderBy(_ => random.Next()).ToArray(),
            positions.ToHashSet(),
            baseline,
            new ComponentwiseUtilityModel(),
            MaxRetainedRepresentatives: 4);
    }

    private static EquipmentExactSolverOffer Offer(
        EquipmentLoadoutPosition position,
        uint itemId,
        int[] utility,
        ulong cost,
        EquipmentAcquisitionSourceKind source,
        string? observation,
        string? world,
        int risk,
        string? sourceCatalogKey = null)
    {
        var slot = position switch
        {
            EquipmentLoadoutPosition.Head => EquipmentSlot.Head,
            EquipmentLoadoutPosition.Body => EquipmentSlot.Body,
            _ => EquipmentSlot.Hands,
        };
        var definition = new EquipmentItemDefinition(
            itemId,
            $"Item {itemId}",
            1,
            1,
            slot,
            new HashSet<uint> { 17 },
            1,
            true,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            false);
        var offer = new EquipmentLoadoutOffer(
            definition,
            source,
            source.ToString(),
            cost > uint.MaxValue ? uint.MaxValue : (uint)cost,
            SourceCatalogKey: sourceCatalogKey ?? $"{source}:{itemId}");
        return new(
            offer,
            observation,
            new HashSet<EquipmentLoadoutPosition> { position },
            1,
            new([
                new("gathering", utility[0]),
                new("perception", utility[1]),
                new("gp", utility[2]),
            ]),
            cost,
            world,
            null,
            source == EquipmentAcquisitionSourceKind.Owned ? 0 : 1,
            new(risk, 0, 0),
            [source.ToString()]);
    }

    private static string[] Contract(EquipmentExactFrontierResult result)
    {
        var lines = new List<string>();
        lines.AddRange(result.Pareto.Frontier.Select(solution => $"frontier|{Solution(solution)}"));
        lines.AddRange(result.Pareto.EquivalenceGroups.Select(value =>
            $"pareto-equivalence|{value.GroupId}|{string.Join(',', value.Variants.Select(variant => variant.Candidate.SolutionId))}"));
        lines.Add($"baseline-contract|{result.Diagnostics.BaselineSolutionId}");
        return lines.ToArray();
    }

    private static string Solution(EquipmentDecisionSolution solution) => string.Join('|',
        solution.Candidate.SolutionId,
        string.Join(',', solution.Candidate.Selections.OrderBy(value => value.Position).Select(value =>
            $"{value.Position}:{value.OfferKey.ItemId}:{value.OfferKey.Quality}:{value.OfferKey.SourceKind}:{value.OfferKey.SourceCatalogKey}:{value.ObservationId}:{value.Quantity}")),
        solution.Utility.UtilityScore,
        solution.AcquisitionCostGil,
        solution.Burden.WorldVisits,
        solution.Burden.VendorStops,
        solution.Burden.PurchaseTransactions,
        solution.EvidenceRisk.FreshnessBucket,
        solution.EvidenceRisk.IncompleteCoverageCount,
        solution.EvidenceRisk.ConfidencePenalty);

    private sealed class ComponentwiseUtilityModel : IEquipmentExactSolverUtilityModel, IEquipmentPartialDominanceCoordinateModel
    {
        public EquipmentPartialUtilityDominance ComparePartial(
            EquipmentSolverUtilityVector candidate,
            EquipmentSolverUtilityVector other)
        {
            var keys = candidate.Components.Select(value => value.Key)
                .Concat(other.Components.Select(value => value.Key))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var noWorse = keys.All(key => candidate.Get(key) >= other.Get(key));
            return new(noWorse, noWorse && keys.Any(key => candidate.Get(key) > other.Get(key)));
        }

        public IReadOnlyList<long> GetPartialDominanceCoordinates(EquipmentSolverUtilityVector utility) =>
            [utility.Get("gathering"), utility.Get("perception"), utility.Get("gp")];

        public EquipmentUtilityEvaluation Evaluate(EquipmentSolverUtilityVector completed)
        {
            var score = completed.Components.Sum(value => value.Units);
            return new(
                new("differential", "1"),
                new("random", 17, 100, "Random differential", []),
                score,
                new(score, score, []),
                UpgradeAssessment.ClearImprovement,
                [],
                [],
                [],
                EquipmentEvaluationConfidence.High,
                []);
        }
    }
}
