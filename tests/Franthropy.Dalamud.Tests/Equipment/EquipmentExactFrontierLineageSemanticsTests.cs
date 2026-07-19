using Franthropy.Dalamud.Equipment;

namespace Franthropy.Dalamud.Tests.Equipment;

public sealed class EquipmentExactFrontierLineageSemanticsTests
{
    [Fact]
    public void RetainedCanonicalTraversalPathCount_IsNotAllFeasibleTerminalVariants()
    {
        var choices = new Dictionary<int, MiniChoice[]>
        {
            [0] = [new(0, "head-a", "a"), new(0, "head-b", "b")],
            [1] = [new(1, "body-a", "a")],
            [2] = [new(2, "hands-b", "b")],
        };

        var brute = EnumerateAll(choices);
        var retainedCounts = new[]
        {
            SolveInOrder(choices, [0, 1, 2]),
            SolveInOrder(choices, [0, 2, 1]),
            SolveInOrder(choices, [1, 2, 0]),
        };

        Assert.Equal(2, brute.Length);
        Assert.All(brute, state => Assert.Equal("a,b", state.WorldKey));
        Assert.Equal([1, 1, 2], retainedCounts);
    }

    [Fact]
    public void BruteForceTerminalEquivalenceAndCanonicalRepresentative_AreTraversalInvariant()
    {
        var choices = new Dictionary<int, MiniChoice[]>
        {
            [0] = [new(0, "head-a", "a"), new(0, "head-b", "b")],
            [1] = [new(1, "body-a", "a")],
            [2] = [new(2, "hands-b", "b")],
        };
        int[][] traversals =
        [
            [0, 1, 2], [0, 2, 1], [1, 0, 2],
            [1, 2, 0], [2, 0, 1], [2, 1, 0],
        ];

        var terminalContracts = traversals.Select(traversal =>
        {
            var terminalClass = Assert.Single(EnumerateAll(choices, traversal)
                .GroupBy(state => (state.Utility, state.Cost, state.WorldKey)));
            return (
                Count: terminalClass.Count(),
                CanonicalRepresentative: terminalClass
                    .Select(state => state.CanonicalSelectionKey)
                    .Order(StringComparer.Ordinal)
                    .First());
        }).ToArray();

        Assert.All(terminalContracts, contract => Assert.Equal(2, contract.Count));
        Assert.All(terminalContracts, contract => Assert.Equal(
            "0:head-a|1:body-a|2:hands-b",
            contract.CanonicalRepresentative));
    }

    [Fact]
    public void ProductionCanonicalTraversal_RetainsOneOfTwoFeasibleTerminalEquivalentPaths()
    {
        var headA = Offer(EquipmentLoadoutPosition.Head, 101, "a");
        var headB = Offer(EquipmentLoadoutPosition.Head, 102, "b");
        var bodyA = Offer(EquipmentLoadoutPosition.Body, 201, "a");
        var handsB = Offer(EquipmentLoadoutPosition.Hands, 301, "b");
        var headBaseline = Offer(EquipmentLoadoutPosition.Head, 100, null, utility: 0, cost: 0, owned: true);
        var bodyBaseline = Offer(EquipmentLoadoutPosition.Body, 200, null, utility: 0, cost: 0, owned: true);
        var handsBaseline = Offer(EquipmentLoadoutPosition.Hands, 300, null, utility: 0, cost: 0, owned: true);
        var request = new EquipmentExactFrontierRequest(
            [headA, headB, bodyA, handsB, headBaseline, bodyBaseline, handsBaseline],
            new HashSet<EquipmentLoadoutPosition>
            {
                EquipmentLoadoutPosition.Head,
                EquipmentLoadoutPosition.Body,
                EquipmentLoadoutPosition.Hands,
            },
            new Dictionary<EquipmentLoadoutPosition, EquipmentOfferAllocationKey?>
            {
                [EquipmentLoadoutPosition.Head] = headBaseline.AllocationKey,
                [EquipmentLoadoutPosition.Body] = bodyBaseline.AllocationKey,
                [EquipmentLoadoutPosition.Hands] = handsBaseline.AllocationKey,
            },
            new AdditiveUtilityModel());

        var result = new EquipmentExactFrontierSolver().Solve(request);
        var reordered = new EquipmentExactFrontierSolver().Solve(request with
        {
            Offers = request.Offers.Reverse().ToArray(),
        });
        var represented = result.Pareto.Frontier
            .Concat(result.Pareto.Dominated.Select(value => value.Solution))
            .Where(solution => solution.AcquisitionCostGil == 3 && solution.Utility.UtilityScore == 3)
            .Where(solution => solution.Candidate.Selections.Any(selection => selection.OfferKey.ItemId is 101 or 102))
            .ToArray();

        Assert.Single(represented);
        // The full fixture has twelve feasible paths after including baseline choices; canonical
        // prefix dominance retains eight, including only one of the two target terminal variants.
        Assert.Equal(8, result.Diagnostics.RetainedCompletePathCount);
        Assert.Contains(represented[0].Candidate.Selections, selection => selection.OfferKey.ItemId == 101);
        Assert.DoesNotContain(represented[0].Candidate.Selections, selection => selection.OfferKey.ItemId == 102);
        Assert.Contains(reordered.Pareto.Frontier.Concat(reordered.Pareto.Dominated.Select(value => value.Solution)),
            solution => solution.Candidate.SolutionId == represented[0].Candidate.SolutionId);
        var feasibility = new EquipmentLoadoutFeasibilityEvaluator().Evaluate(new(
            represented[0].Candidate,
            request.Offers.Select(offer => new EquipmentFeasibilityOffer(
                offer.Offer,
                offer.AvailableQuantity,
                offer.ObservationId)).ToArray(),
            request.RequiredPositions));
        Assert.True(feasibility.IsFeasible);
    }

    private static MiniState[] EnumerateAll(
        IReadOnlyDictionary<int, MiniChoice[]> choices,
        IReadOnlyList<int>? traversal = null)
    {
        var states = new[] { MiniState.Empty };
        foreach (var position in traversal ?? choices.Keys.Order().ToArray())
            states = states.SelectMany(state => choices[position].Select(state.Add)).ToArray();
        return states;
    }

    private static int SolveInOrder(IReadOnlyDictionary<int, MiniChoice[]> choices, IReadOnlyList<int> positions)
    {
        var states = new[] { MiniState.Empty };
        foreach (var position in positions)
        {
            var expanded = states.SelectMany(state => choices[position].Select(state.Add)).ToArray();
            var retained = expanded.Where(candidate => !expanded.Any(other => other != candidate && Dominates(other, candidate))).ToArray();
            states = retained
                .GroupBy(state => (state.Utility, state.Cost, state.WorldKey))
                .Select(group => group.First() with { RetainedPathCount = group.Sum(state => state.RetainedPathCount) })
                .ToArray();
        }
        return states.Sum(state => state.RetainedPathCount);
    }

    private static bool Dominates(MiniState candidate, MiniState other) =>
        candidate.Utility >= other.Utility &&
        candidate.Cost <= other.Cost &&
        candidate.Worlds.IsSubsetOf(other.Worlds) &&
        (candidate.Utility > other.Utility || candidate.Cost < other.Cost || candidate.Worlds.Count < other.Worlds.Count);

    private static EquipmentExactSolverOffer Offer(
        EquipmentLoadoutPosition position,
        uint itemId,
        string? world,
        long utility = 1,
        ulong cost = 1,
        bool owned = false)
    {
        var slot = position switch
        {
            EquipmentLoadoutPosition.Head => EquipmentSlot.Head,
            EquipmentLoadoutPosition.Body => EquipmentSlot.Body,
            _ => EquipmentSlot.Hands,
        };
        var source = owned ? EquipmentAcquisitionSourceKind.Owned : EquipmentAcquisitionSourceKind.MarketBoard;
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
        var offer = new EquipmentLoadoutOffer(definition, source, source.ToString(), (uint)cost, SourceCatalogKey: $"{source}:{itemId}");
        return new(
            offer,
            owned ? null : $"observation:{itemId}",
            new HashSet<EquipmentLoadoutPosition> { position },
            1,
            new([new("power", utility)]),
            cost,
            world,
            null,
            owned ? 0 : 1,
            new(0, 0, 0),
            [source.ToString()]);
    }

    private sealed record MiniChoice(int Position, string Id, string World);

    private sealed record MiniState(
        IReadOnlyList<MiniChoice> Choices,
        int Utility,
        int Cost,
        IReadOnlySet<string> Worlds,
        int RetainedPathCount)
    {
        public static MiniState Empty { get; } = new([], 0, 0, new HashSet<string>(StringComparer.Ordinal), 1);
        public string WorldKey => string.Join(',', Worlds.Order(StringComparer.Ordinal));
        public string CanonicalSelectionKey => string.Join('|', Choices
            .OrderBy(choice => choice.Position)
            .Select(choice => $"{choice.Position}:{choice.Id}"));

        public MiniState Add(MiniChoice choice)
        {
            var worlds = new HashSet<string>(Worlds, StringComparer.Ordinal) { choice.World };
            return new([.. Choices, choice], Utility + 1, Cost + 1, worlds, RetainedPathCount);
        }
    }

    private sealed class AdditiveUtilityModel : IEquipmentExactSolverUtilityModel
    {
        public EquipmentPartialUtilityDominance ComparePartial(
            EquipmentSolverUtilityVector candidate,
            EquipmentSolverUtilityVector other)
        {
            var candidateValue = candidate.Get("power");
            var otherValue = other.Get("power");
            return new(candidateValue >= otherValue, candidateValue > otherValue);
        }

        public EquipmentUtilityEvaluation Evaluate(EquipmentSolverUtilityVector completed)
        {
            var score = completed.Get("power");
            return new(
                new("lineage-counterexample", "1"),
                new("lineage", 17, 100, "Lineage counterexample", []),
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
