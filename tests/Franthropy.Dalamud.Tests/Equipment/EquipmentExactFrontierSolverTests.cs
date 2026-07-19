using System.Diagnostics;
using Franthropy.Dalamud.Equipment;

namespace Franthropy.Dalamud.Tests.Equipment;

public sealed class EquipmentExactFrontierSolverTests
{
    private static readonly AdditiveUtilityModel UtilityModel = new();

    [Fact]
    public void Solve_PreservesNoPurchaseBaselineEvenWhenItIsDominated()
    {
        var baseline = Offer(EquipmentLoadoutPosition.Head, 100, 10, 0, source: EquipmentAcquisitionSourceKind.Owned);
        var freeUpgrade = Offer(EquipmentLoadoutPosition.Head, 101, 20, 0, source: EquipmentAcquisitionSourceKind.Owned);

        var result = Solve([baseline, freeUpgrade], [EquipmentLoadoutPosition.Head], Baseline((EquipmentLoadoutPosition.Head, baseline)));

        Assert.Equal("baseline", result.Diagnostics.BaselineSolutionId);
        Assert.Contains(result.Pareto.Dominated, value => value.Solution.Candidate.SolutionId == "baseline");
        Assert.Contains(result.Pareto.Frontier, value => value.Utility.UtilityScore == 20);
    }

    [Fact]
    public void Solve_PreservesEquivalentNqAndHqObservationVariants()
    {
        var baseline = Offer(EquipmentLoadoutPosition.Head, 100, 10, 0, source: EquipmentAcquisitionSourceKind.Owned);
        var nq = Offer(EquipmentLoadoutPosition.Head, 101, 20, 1_000, EquipmentQuality.Normal, observationId: "listing-nq");
        var hq = Offer(EquipmentLoadoutPosition.Head, 101, 20, 1_000, EquipmentQuality.High, observationId: "listing-hq");

        var result = Solve([baseline, nq, hq], [EquipmentLoadoutPosition.Head], Baseline((EquipmentLoadoutPosition.Head, baseline)));

        var group = Assert.Single(result.Pareto.EquivalenceGroups);
        Assert.Equal(2, group.Variants.Count);
        Assert.Contains(group.Variants, value => Assert.Single(value.Candidate.Selections).OfferKey.Quality == EquipmentQuality.Normal);
        Assert.Contains(group.Variants, value => Assert.Single(value.Candidate.Selections).OfferKey.Quality == EquipmentQuality.High);
        Assert.Contains(group.Variants, value => Assert.Single(value.Candidate.Selections).ObservationId == "listing-nq");
        Assert.Contains(group.Variants, value => Assert.Single(value.Candidate.Selections).ObservationId == "listing-hq");
    }

    [Fact]
    public void Solve_NeverOverAllocatesRingListingsOrUniqueItems()
    {
        var left = Offer(EquipmentLoadoutPosition.LeftRing, 200, 5, 0, source: EquipmentAcquisitionSourceKind.Owned, observationId: "owned-left");
        var right = Offer(EquipmentLoadoutPosition.RightRing, 201, 5, 0, source: EquipmentAcquisitionSourceKind.Owned, observationId: "owned-right");
        var scarce = Offer(
            EquipmentLoadoutPosition.LeftRing,
            202,
            20,
            500,
            observationId: "one-listing",
            positions: new HashSet<EquipmentLoadoutPosition> { EquipmentLoadoutPosition.LeftRing, EquipmentLoadoutPosition.RightRing },
            available: 1);
        var unique = Offer(
            EquipmentLoadoutPosition.LeftRing,
            203,
            30,
            800,
            observationId: "unique-listing",
            positions: new HashSet<EquipmentLoadoutPosition> { EquipmentLoadoutPosition.LeftRing, EquipmentLoadoutPosition.RightRing },
            available: 2,
            unique: true);

        var result = Solve(
            [left, right, scarce, unique],
            [EquipmentLoadoutPosition.LeftRing, EquipmentLoadoutPosition.RightRing],
            Baseline((EquipmentLoadoutPosition.LeftRing, left), (EquipmentLoadoutPosition.RightRing, right)));

        foreach (var solution in All(result))
        {
            Assert.True(solution.Candidate.Selections.Count(selection => selection.ObservationId == "one-listing") <= 1);
            Assert.True(solution.Candidate.Selections.Count(selection => selection.OfferKey.ItemId == 203) <= 1);
        }
        Assert.True(result.Diagnostics.InfeasibleTransitionCount > 0);
    }

    [Fact]
    public void Solve_TwoHandedMainHandProducesFeasibleLoadoutWithoutOffHand()
    {
        var sword = Offer(EquipmentLoadoutPosition.MainHand, 300, 10, 0, source: EquipmentAcquisitionSourceKind.Owned);
        var shield = Offer(EquipmentLoadoutPosition.OffHand, 301, 5, 0, source: EquipmentAcquisitionSourceKind.Owned);
        var greatsword = Offer(EquipmentLoadoutPosition.MainHand, 302, 30, 2_000, twoHanded: true);

        var result = Solve(
            [sword, shield, greatsword],
            [EquipmentLoadoutPosition.MainHand, EquipmentLoadoutPosition.OffHand],
            Baseline((EquipmentLoadoutPosition.MainHand, sword), (EquipmentLoadoutPosition.OffHand, shield)));

        var twoHanded = Assert.Single(All(result), solution => solution.Candidate.Selections.Any(selection => selection.OfferKey.ItemId == 302));
        Assert.DoesNotContain(twoHanded.Candidate.Selections, selection => selection.Position == EquipmentLoadoutPosition.OffHand);
    }

    [Fact]
    public void Solve_MainHandOccupancyRemainsAFutureResourceDuringPartialDominance()
    {
        var baselineSword = Offer(EquipmentLoadoutPosition.MainHand, 300, 1, 0, source: EquipmentAcquisitionSourceKind.Owned);
        var baselineShield = Offer(EquipmentLoadoutPosition.OffHand, 301, 1, 0, source: EquipmentAcquisitionSourceKind.Owned);
        var oneHanded = Offer(EquipmentLoadoutPosition.MainHand, 302, 10, 100);
        var twoHanded = Offer(EquipmentLoadoutPosition.MainHand, 303, 50, 100, twoHanded: true);
        var powerfulShield = Offer(EquipmentLoadoutPosition.OffHand, 304, 100, 100);

        var result = Solve(
            [baselineSword, baselineShield, oneHanded, twoHanded, powerfulShield],
            [EquipmentLoadoutPosition.MainHand, EquipmentLoadoutPosition.OffHand],
            Baseline(
                (EquipmentLoadoutPosition.MainHand, baselineSword),
                (EquipmentLoadoutPosition.OffHand, baselineShield)));

        Assert.Contains(All(result), solution =>
            solution.Candidate.Selections.Any(selection => selection.OfferKey.ItemId == oneHanded.Offer.Definition.ItemId) &&
            solution.Candidate.Selections.Any(selection => selection.OfferKey.ItemId == powerfulShield.Offer.Definition.ItemId));
    }

    [Fact]
    public void Solve_IsDeterministicAcrossOfferOrdering()
    {
        var baselineHead = Offer(EquipmentLoadoutPosition.Head, 100, 10, 0, source: EquipmentAcquisitionSourceKind.Owned);
        var baselineBody = Offer(EquipmentLoadoutPosition.Body, 200, 10, 0, source: EquipmentAcquisitionSourceKind.Owned);
        var offers = new[]
        {
            baselineHead,
            baselineBody,
            Offer(EquipmentLoadoutPosition.Head, 101, 20, 1_000),
            Offer(EquipmentLoadoutPosition.Body, 201, 30, 2_000),
        };
        var positions = new HashSet<EquipmentLoadoutPosition> { EquipmentLoadoutPosition.Head, EquipmentLoadoutPosition.Body };
        var baseline = Baseline((EquipmentLoadoutPosition.Head, baselineHead), (EquipmentLoadoutPosition.Body, baselineBody));

        var first = Solve(offers, positions, baseline);
        var second = Solve(offers.Reverse().ToArray(), positions, baseline);

        Assert.Equal(Replay(first), Replay(second));
        Assert.Equal(first.Diagnostics.ExpandedStateCount, second.Diagnostics.ExpandedStateCount);
        Assert.Equal(first.Diagnostics.DominatedStateCount, second.Diagnostics.DominatedStateCount);
    }

    [Fact]
    public void Solve_ReportsBoundedPruningAndRetainedStateProgressAfterEachPosition()
    {
        var head = Offer(EquipmentLoadoutPosition.Head, 100, 10, 0, source: EquipmentAcquisitionSourceKind.Owned);
        var body = Offer(EquipmentLoadoutPosition.Body, 200, 10, 0, source: EquipmentAcquisitionSourceKind.Owned);
        var progress = new List<EquipmentExactFrontierProgress>();

        var result = new EquipmentExactFrontierSolver().Solve(
            new(
                [head, body],
                new HashSet<EquipmentLoadoutPosition> { EquipmentLoadoutPosition.Head, EquipmentLoadoutPosition.Body },
                Baseline((EquipmentLoadoutPosition.Head, head), (EquipmentLoadoutPosition.Body, body)),
                UtilityModel),
            reportProgress: progress.Add);

        Assert.Collection(
            progress,
            reduction => Assert.Equal(("OfferReduction", 2, 2, 2),
                (reduction.Phase, reduction.InputOfferCount, reduction.RetainedOfferChoiceCount, reduction.RetainedOfferVariantCount)),
            headPruning => Assert.Equal((0, 2, EquipmentLoadoutPosition.Head, "Pruning"),
                (headPruning.CompletedPositionCount, headPruning.TotalPositionCount, headPruning.Position, headPruning.Phase)),
            headComplete => Assert.Equal((1, 2, EquipmentLoadoutPosition.Head, "PositionComplete"),
                (headComplete.CompletedPositionCount, headComplete.TotalPositionCount, headComplete.Position, headComplete.Phase)),
            bodyPruning => Assert.Equal((1, 2, EquipmentLoadoutPosition.Body, "Pruning"),
                (bodyPruning.CompletedPositionCount, bodyPruning.TotalPositionCount, bodyPruning.Position, bodyPruning.Phase)),
            bodyComplete => Assert.Equal((2, 2, EquipmentLoadoutPosition.Body, "PositionComplete"),
                (bodyComplete.CompletedPositionCount, bodyComplete.TotalPositionCount, bodyComplete.Position, bodyComplete.Phase)),
            representatives => Assert.Equal("RepresentativesMaterialized", representatives.Phase),
            primary => Assert.Equal("PrimaryMaterialized", primary.Phase),
            pareto => Assert.Equal("ParetoBuilt", pareto.Phase),
            frontier => Assert.Equal("FrontierMaterialized", frontier.Phase),
            finalized => Assert.Equal("Finalized", finalized.Phase));
        Assert.Equal(result.Diagnostics.PeakRetainedStateCount, progress.Max(value => value.RetainedStateCount));
        Assert.Equal(result.Diagnostics.ExpandedStateCount, progress[^1].ExpandedStateCount);
    }

    [Fact]
    public void Solve_CancellationAfterProgressCannotPublishAPartialFrontier()
    {
        var head = Offer(EquipmentLoadoutPosition.Head, 100, 10, 0, source: EquipmentAcquisitionSourceKind.Owned);
        var body = Offer(EquipmentLoadoutPosition.Body, 200, 10, 0, source: EquipmentAcquisitionSourceKind.Owned);
        using var cancellation = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() => new EquipmentExactFrontierSolver().Solve(
            new(
                [head, body],
                new HashSet<EquipmentLoadoutPosition> { EquipmentLoadoutPosition.Head, EquipmentLoadoutPosition.Body },
                Baseline((EquipmentLoadoutPosition.Head, head), (EquipmentLoadoutPosition.Body, body)),
                UtilityModel),
            cancellation.Token,
            progress =>
            {
                if (progress.CompletedPositionCount == 1)
                    cancellation.Cancel();
            }));
    }

    [Fact]
    public void Solve_PreservesDeterministicFractionalUtilityScores()
    {
        var baseline = Offer(EquipmentLoadoutPosition.Head, 100, 1, 0, source: EquipmentAcquisitionSourceKind.Owned);
        var upgrade = Offer(EquipmentLoadoutPosition.Head, 101, 3, 100);
        var request = new EquipmentExactFrontierRequest(
            [baseline, upgrade],
            new HashSet<EquipmentLoadoutPosition> { EquipmentLoadoutPosition.Head },
            Baseline((EquipmentLoadoutPosition.Head, baseline)),
            new HalvedUtilityModel());

        var first = new EquipmentExactFrontierSolver().Solve(request);
        var second = new EquipmentExactFrontierSolver().Solve(request with { Offers = request.Offers.Reverse().ToArray() });

        Assert.Contains(All(first), solution => solution.Utility.UtilityScore == 0.5);
        Assert.Contains(All(first), solution => solution.Utility.UtilityScore == 1.5);
        Assert.Equal(Replay(first), Replay(second));
    }

    [Fact]
    public void Solve_FrontierIsInvariantUnderDominatedOfferInsertion()
    {
        var baseline = Offer(EquipmentLoadoutPosition.Head, 100, 10, 0, source: EquipmentAcquisitionSourceKind.Owned);
        var useful = Offer(EquipmentLoadoutPosition.Head, 101, 20, 1_000);
        var dominated = Offer(EquipmentLoadoutPosition.Head, 102, 15, 2_000);

        var without = Solve([baseline, useful], [EquipmentLoadoutPosition.Head], Baseline((EquipmentLoadoutPosition.Head, baseline)));
        var with = Solve([dominated, useful, baseline], [EquipmentLoadoutPosition.Head], Baseline((EquipmentLoadoutPosition.Head, baseline)));

        Assert.Equal(
            without.Pareto.Frontier.Select(Metric).Order().ToArray(),
            with.Pareto.Frontier.Select(Metric).Order().ToArray());
        Assert.Equal(3, with.Diagnostics.InputOfferCount);
        Assert.Equal(2, with.Diagnostics.RetainedOfferChoiceCount);
    }

    [Fact]
    public void Solve_BaseAndAcquisitionProofPrunesHigherLevelStatDominatedGearOnlyWhenEnvelopeIsNoWorse()
    {
        var baseline = Offer(EquipmentLoadoutPosition.Head, 100, 10, 0, source: EquipmentAcquisitionSourceKind.Owned, itemLevel: 10);
        var lower = Offer(EquipmentLoadoutPosition.Head, 101, 20, 1_000, itemLevel: 30);
        var higher = Offer(EquipmentLoadoutPosition.Head, 102, 30, 1_000, itemLevel: 40);

        var result = Solve([lower, baseline, higher], [EquipmentLoadoutPosition.Head], Baseline((EquipmentLoadoutPosition.Head, baseline)));

        Assert.Equal(2, result.Diagnostics.RetainedOfferChoiceCount);
        Assert.DoesNotContain(All(result), solution => solution.Candidate.Selections.Any(selection => selection.OfferKey.ItemId == lower.Offer.Definition.ItemId));
        Assert.Contains(All(result), solution => solution.Candidate.Selections.Any(selection => selection.OfferKey.ItemId == higher.Offer.Definition.ItemId));
    }

    [Fact]
    public void Solve_CheaperLowerLevelGearRemainsACostUtilityParetoNeighbor()
    {
        var baseline = Offer(EquipmentLoadoutPosition.Head, 100, 10, 0, source: EquipmentAcquisitionSourceKind.Owned, itemLevel: 10);
        var cheaperLower = Offer(EquipmentLoadoutPosition.Head, 101, 20, 500, itemLevel: 30);
        var stronger = Offer(EquipmentLoadoutPosition.Head, 102, 30, 1_000, itemLevel: 40);

        var result = Solve([baseline, stronger, cheaperLower], [EquipmentLoadoutPosition.Head], Baseline((EquipmentLoadoutPosition.Head, baseline)));

        Assert.Equal(3, result.Diagnostics.RetainedOfferChoiceCount);
        Assert.Contains(result.Pareto.Frontier, solution => solution.AcquisitionCostGil == 500 && solution.Utility.UtilityScore == 20);
        Assert.Contains(result.Pareto.Frontier, solution => solution.AcquisitionCostGil == 1_000 && solution.Utility.UtilityScore == 30);
    }

    [Fact]
    public void Solve_HybridStatTradeoffsRemainRegardlessOfItemLevelOrdering()
    {
        var baseline = OfferVector(EquipmentLoadoutPosition.Head, 100, 0, 10, ("gathering", 10), ("perception", 10));
        var highItemLevelGathering = OfferVector(EquipmentLoadoutPosition.Head, 101, 1_000, 80, ("gathering", 30), ("perception", 10));
        var lowerItemLevelHybrid = OfferVector(EquipmentLoadoutPosition.Head, 102, 1_000, 70, ("gathering", 20), ("perception", 20));
        var request = new EquipmentExactFrontierRequest(
            [baseline, highItemLevelGathering, lowerItemLevelHybrid],
            new HashSet<EquipmentLoadoutPosition> { EquipmentLoadoutPosition.Head },
            Baseline((EquipmentLoadoutPosition.Head, baseline)),
            new VectorUtilityModel());

        var result = new EquipmentExactFrontierSolver().Solve(request);

        Assert.Equal(3, result.Diagnostics.RetainedOfferChoiceCount);
        Assert.Contains(result.Pareto.Frontier, solution => solution.Candidate.Selections.Any(selection => selection.OfferKey.ItemId == 101));
        Assert.Contains(result.Pareto.Frontier, solution => solution.Candidate.Selections.Any(selection => selection.OfferKey.ItemId == 102));
    }

    [Fact]
    public void Solve_ZeroCostEquippedBaselineRemovesPaidPerSlotRegression()
    {
        var baseline = OfferVector(EquipmentLoadoutPosition.Head, 100, 0, 20, ("gathering", 20), ("perception", 20));
        var paidRegression = OfferVector(EquipmentLoadoutPosition.Head, 101, 500, 90, ("gathering", 20), ("perception", 15));
        var request = new EquipmentExactFrontierRequest(
            [paidRegression, baseline],
            new HashSet<EquipmentLoadoutPosition> { EquipmentLoadoutPosition.Head },
            Baseline((EquipmentLoadoutPosition.Head, baseline)),
            new VectorUtilityModel());

        var result = new EquipmentExactFrontierSolver().Solve(request);

        Assert.Equal(1, result.Diagnostics.RetainedOfferChoiceCount);
        Assert.Equal("baseline", Assert.Single(result.Pareto.Frontier).Candidate.SolutionId);
        Assert.DoesNotContain(All(result), solution => solution.Candidate.Selections.Any(selection => selection.OfferKey.ItemId == 101));
    }

    [Fact]
    public void Solve_ProvenUtilitySaturationMatchesBruteForceAndRetainsEquivalentExactLineage()
    {
        var baseline = OfferVector(EquipmentLoadoutPosition.Head, 100, 0, 10, ("gathering", 50));
        var firstOvershoot = OfferVector(EquipmentLoadoutPosition.Head, 101, 500, 50, ("gathering", 150));
        var secondOvershoot = OfferVector(EquipmentLoadoutPosition.Head, 102, 500, 60, ("gathering", 250));
        var expensiveOvershoot = OfferVector(EquipmentLoadoutPosition.Head, 103, 800, 70, ("gathering", 300));
        var offers = new[] { expensiveOvershoot, secondOvershoot, baseline, firstOvershoot };
        var model = SaturatingUtilityModel();
        var result = new EquipmentExactFrontierSolver().Solve(new(
            offers,
            new HashSet<EquipmentLoadoutPosition> { EquipmentLoadoutPosition.Head },
            Baseline((EquipmentLoadoutPosition.Head, baseline)),
            model));
        var brute = offers
            .Select(offer => (offer.AcquisitionCostGil, model.Evaluate(offer.Utility).UtilityScore, offer.PurchaseTransactions))
            .Where(candidate => !offers.Select(offer =>
                (offer.AcquisitionCostGil, model.Evaluate(offer.Utility).UtilityScore, offer.PurchaseTransactions))
                .Any(other => other.AcquisitionCostGil <= candidate.AcquisitionCostGil &&
                    other.UtilityScore >= candidate.UtilityScore &&
                    other.PurchaseTransactions <= candidate.PurchaseTransactions &&
                    (other.AcquisitionCostGil < candidate.AcquisitionCostGil ||
                     other.UtilityScore > candidate.UtilityScore ||
                     other.PurchaseTransactions < candidate.PurchaseTransactions)))
            .Distinct()
            .Order()
            .ToArray();

        Assert.Equal(brute, result.Pareto.Frontier.Select(solution =>
            (solution.AcquisitionCostGil, solution.Utility.UtilityScore, solution.Burden.PurchaseTransactions))
            .Distinct()
            .Order()
            .ToArray());
        Assert.Equal(2, result.Diagnostics.RetainedOfferChoiceCount);
        Assert.Equal(3, result.Diagnostics.RetainedOfferVariantCount);
        Assert.Equal(2, Assert.Single(result.Pareto.EquivalenceGroups).Variants.Count);
    }

    [Fact]
    public void Solve_RetainsCostAgainstEvidenceRiskTradeoff()
    {
        var baseline = Offer(EquipmentLoadoutPosition.Head, 100, 10, 0, source: EquipmentAcquisitionSourceKind.Owned);
        var cheapRisky = Offer(EquipmentLoadoutPosition.Head, 101, 20, 1_000, risk: new(2, 0, 1));
        var expensiveFresh = Offer(EquipmentLoadoutPosition.Head, 102, 20, 1_500, risk: new(0, 0, 0));

        var result = Solve([baseline, expensiveFresh, cheapRisky], [EquipmentLoadoutPosition.Head], Baseline((EquipmentLoadoutPosition.Head, baseline)));

        Assert.Contains(result.Pareto.Frontier, solution => solution.AcquisitionCostGil == 1_000 && solution.EvidenceRisk.FreshnessBucket == 2);
        Assert.Contains(result.Pareto.Frontier, solution => solution.AcquisitionCostGil == 1_500 && solution.EvidenceRisk.FreshnessBucket == 0);
    }

    [Fact]
    public void Solve_ReportsRepresentativeTruncationWithoutChangingRetainedPathCount()
    {
        var baseline = Offer(EquipmentLoadoutPosition.Head, 100, 10, 0, source: EquipmentAcquisitionSourceKind.Owned);
        var nq = Offer(EquipmentLoadoutPosition.Head, 101, 20, 1_000, EquipmentQuality.Normal, observationId: "nq");
        var hq = Offer(EquipmentLoadoutPosition.Head, 101, 20, 1_000, EquipmentQuality.High, observationId: "hq");

        var result = Solve(
            [baseline, nq, hq],
            [EquipmentLoadoutPosition.Head],
            Baseline((EquipmentLoadoutPosition.Head, baseline)),
            maxRetainedRepresentatives: 1);

        var summary = Assert.Single(result.RetainedEquivalenceSummaries, value => value.RetainedPathCount == 2);
        Assert.True(summary.RetainedRepresentativesTruncated);
        Assert.Single(summary.RetainedRepresentativeSolutionIds);
        Assert.Equal(1, result.Diagnostics.RetainedRepresentativeLimit);
    }

    [Fact]
    public void Solve_AgreesWithBruteForceMetricFrontierAcrossSyntheticCases()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed);
            var positions = new[] { EquipmentLoadoutPosition.Head, EquipmentLoadoutPosition.Body, EquipmentLoadoutPosition.Hands };
            var offers = new List<EquipmentExactSolverOffer>();
            var baseline = new Dictionary<EquipmentLoadoutPosition, EquipmentOfferAllocationKey?>();
            foreach (var position in positions)
            {
                var baseOffer = Offer(position, (uint)(1000 + offers.Count), random.Next(1, 10), 0, source: EquipmentAcquisitionSourceKind.Owned);
                offers.Add(baseOffer);
                baseline[position] = baseOffer.AllocationKey;
                offers.Add(Offer(position, (uint)(1000 + offers.Count), random.Next(5, 30), (ulong)random.Next(100, 2_000)));
                offers.Add(Offer(position, (uint)(1000 + offers.Count), random.Next(5, 30), (ulong)random.Next(100, 2_000)));
            }

            var exact = Solve(offers, positions, baseline);
            var brute = BruteMetricFrontier(offers, positions);

            Assert.Equal(brute, exact.Pareto.Frontier.Select(Metric).Distinct().Order().ToArray());
        }
    }

    [Theory]
    [InlineData("level-50-local", 4, 1, 750)]
    [InlineData("level-100-data-center", 8, 2, 1500)]
    [InlineData("level-100-region", 12, 3, 3000)]
    public void Solve_RepresentativeScopeStaysWithinInteractiveBudget(
        string scenario,
        int candidatesPerPosition,
        int worldCount,
        int budgetMilliseconds)
    {
        var positions = new[]
        {
            EquipmentLoadoutPosition.MainHand, EquipmentLoadoutPosition.OffHand,
            EquipmentLoadoutPosition.Head, EquipmentLoadoutPosition.Body, EquipmentLoadoutPosition.Hands,
            EquipmentLoadoutPosition.Legs, EquipmentLoadoutPosition.Feet, EquipmentLoadoutPosition.Ears,
            EquipmentLoadoutPosition.Neck, EquipmentLoadoutPosition.Wrists,
            EquipmentLoadoutPosition.LeftRing, EquipmentLoadoutPosition.RightRing,
        };
        var offers = new List<EquipmentExactSolverOffer>();
        var baseline = new Dictionary<EquipmentLoadoutPosition, EquipmentOfferAllocationKey?>();
        uint itemId = 10_000;
        foreach (var position in positions)
        {
            var slot = position is EquipmentLoadoutPosition.LeftRing or EquipmentLoadoutPosition.RightRing
                ? EquipmentSlot.Ring
                : Slot(position);
            var baselineOffer = Offer(position, itemId++, 10, 0, source: EquipmentAcquisitionSourceKind.Owned, definitionSlot: slot);
            offers.Add(baselineOffer);
            baseline[position] = baselineOffer.AllocationKey;
            for (var option = 1; option < candidatesPerPosition; option++)
            {
                offers.Add(Offer(
                    position,
                    itemId++,
                    10 + option * 5,
                    (ulong)(option * 1_000),
                    definitionSlot: slot,
                    world: $"world-{option % worldCount}"));
            }
        }
        var stopwatch = Stopwatch.StartNew();

        var result = Solve(offers, positions, baseline, maxRetainedRepresentatives: 4);

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(budgetMilliseconds),
            $"{scenario} took {stopwatch.Elapsed}; peak={result.Diagnostics.PeakRetainedStateCount}, representatives={result.Diagnostics.CompleteSolutionCount}, retainedPaths={result.Diagnostics.RetainedCompletePathCount}, dominated={result.Diagnostics.DominatedStateCount}, compacted={result.Diagnostics.CompactedEquivalentStateCount}.");
        Assert.True(result.Diagnostics.PeakRetainedStateCount < 50_000);
        Assert.True(result.Diagnostics.DominatedStateCount > 0);
        Assert.True(result.Diagnostics.RetainedCompletePathCount >= result.Diagnostics.CompleteSolutionCount);
        Assert.Contains(result.RetainedEquivalenceSummaries, summary => summary.RetainedRepresentativesTruncated);
    }

    private static EquipmentExactFrontierResult Solve(
        IReadOnlyList<EquipmentExactSolverOffer> offers,
        IEnumerable<EquipmentLoadoutPosition> positions,
        IReadOnlyDictionary<EquipmentLoadoutPosition, EquipmentOfferAllocationKey?> baseline,
        int maxRetainedRepresentatives = 16) =>
        new EquipmentExactFrontierSolver().Solve(new(
            offers,
            positions.ToHashSet(),
            baseline,
            UtilityModel,
            maxRetainedRepresentatives));

    private static EquipmentExactSolverOffer Offer(
        EquipmentLoadoutPosition position,
        uint itemId,
        long utility,
        ulong cost,
        EquipmentQuality quality = EquipmentQuality.Normal,
        EquipmentAcquisitionSourceKind source = EquipmentAcquisitionSourceKind.MarketBoard,
        string? observationId = null,
        IReadOnlySet<EquipmentLoadoutPosition>? positions = null,
        uint available = 1,
        bool unique = false,
        bool twoHanded = false,
        EquipmentSlot? definitionSlot = null,
        string? world = null,
        EquipmentEvidenceRisk? risk = null,
        uint? itemLevel = null)
    {
        var slot = definitionSlot ?? Slot(position);
        var definition = new EquipmentItemDefinition(
            itemId,
            $"Item {itemId}",
            1,
            itemLevel ?? (uint)Math.Max(1, utility),
            slot,
            new HashSet<uint> { 19 },
            1,
            true,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            IsUnique: unique,
            OffHandOccupancy: twoHanded ? (sbyte)-1 : (sbyte)0);
        var catalogKey = $"{source}-{itemId}";
        var offer = new EquipmentLoadoutOffer(
            definition,
            source,
            source.ToString(),
            cost > uint.MaxValue ? uint.MaxValue : (uint)cost,
            Quality: quality,
            SourceCatalogKey: catalogKey);
        return new(
            offer,
            observationId,
            positions ?? new HashSet<EquipmentLoadoutPosition> { position },
            available,
            new([new("power", utility)]),
            cost,
            world,
            source == EquipmentAcquisitionSourceKind.GilVendor ? catalogKey : null,
            source == EquipmentAcquisitionSourceKind.Owned ? 0 : 1,
            risk ?? new(0, 0, 0),
            [quality.ToString(), source.ToString()]);
    }

    private static EquipmentExactSolverOffer OfferVector(
        EquipmentLoadoutPosition position,
        uint itemId,
        ulong cost,
        uint itemLevel,
        params (string Key, long Units)[] utility)
    {
        var offer = Offer(
            position,
            itemId,
            utility.Sum(value => value.Units),
            cost,
            source: cost == 0 ? EquipmentAcquisitionSourceKind.Owned : EquipmentAcquisitionSourceKind.MarketBoard,
            itemLevel: itemLevel);
        return offer with
        {
            Utility = new(utility.Select(value => new EquipmentSolverUtilityComponent(value.Key, value.Units)).ToArray()),
        };
    }

    private static EquipmentThresholdUtilityModel SaturatingUtilityModel() => new(new(
        new(
            new("saturating-test", "1"),
            "Saturating test",
            new HashSet<uint> { 19 },
            new HashSet<string> { "solver" },
            [],
            "Synthetic saturation proof."),
        new("solver", 19, 50, "Synthetic saturation proof", []),
        new([new("gathering", 50)]),
        [new("gathering", EquipmentStatSemantic.Gathering, 1, 100, "Bounded synthetic progress")],
        [new("gathering-100", "Gathering 100", [new("gathering", 100)], 1_000, "Synthetic threshold")],
        0,
        []));

    private static IReadOnlyDictionary<EquipmentLoadoutPosition, EquipmentOfferAllocationKey?> Baseline(
        params (EquipmentLoadoutPosition Position, EquipmentExactSolverOffer Offer)[] values) =>
        values.ToDictionary(value => value.Position, value => (EquipmentOfferAllocationKey?)value.Offer.AllocationKey);

    private static EquipmentSlot Slot(EquipmentLoadoutPosition position) => position switch
    {
        EquipmentLoadoutPosition.MainHand => EquipmentSlot.MainHand,
        EquipmentLoadoutPosition.OffHand => EquipmentSlot.OffHand,
        EquipmentLoadoutPosition.Head => EquipmentSlot.Head,
        EquipmentLoadoutPosition.Body => EquipmentSlot.Body,
        EquipmentLoadoutPosition.Hands => EquipmentSlot.Hands,
        EquipmentLoadoutPosition.Legs => EquipmentSlot.Legs,
        EquipmentLoadoutPosition.Feet => EquipmentSlot.Feet,
        EquipmentLoadoutPosition.Ears => EquipmentSlot.Ears,
        EquipmentLoadoutPosition.Neck => EquipmentSlot.Neck,
        EquipmentLoadoutPosition.Wrists => EquipmentSlot.Wrists,
        EquipmentLoadoutPosition.LeftRing or EquipmentLoadoutPosition.RightRing => EquipmentSlot.Ring,
        _ => EquipmentSlot.Unknown,
    };

    private static IReadOnlyList<EquipmentDecisionSolution> All(EquipmentExactFrontierResult result) =>
        result.Pareto.Frontier.Concat(result.Pareto.Dominated.Select(value => value.Solution)).ToArray();

    private static (ulong Cost, double Utility, int Purchases) Metric(EquipmentDecisionSolution solution) =>
        (solution.AcquisitionCostGil, solution.Utility.UtilityScore, solution.Burden.PurchaseTransactions);

    private static string Replay(EquipmentExactFrontierResult result) => EquipmentDecisionReplayJson.Serialize(new(
        Guid.Empty,
        DateTimeOffset.UnixEpoch,
        All(result)));

    private static (ulong Cost, double Utility, int Purchases)[] BruteMetricFrontier(
        IReadOnlyList<EquipmentExactSolverOffer> offers,
        IReadOnlyList<EquipmentLoadoutPosition> positions)
    {
        var combinations = new List<(ulong Cost, double Utility, int Purchases)> { (0, 0, 0) };
        foreach (var position in positions)
        {
            combinations = combinations.SelectMany(current => offers
                .Where(offer => offer.Positions.Contains(position))
                .Select(offer => (
                    checked(current.Cost + offer.AcquisitionCostGil),
                    current.Utility + offer.Utility.Get("power"),
                    current.Purchases + offer.PurchaseTransactions)))
                .ToList();
        }
        return combinations
            .Where(candidate => !combinations.Any(other =>
                other.Cost <= candidate.Cost &&
                other.Utility >= candidate.Utility &&
                other.Purchases <= candidate.Purchases &&
                (other.Cost < candidate.Cost || other.Utility > candidate.Utility || other.Purchases < candidate.Purchases)))
            .Distinct()
            .Order()
            .ToArray();
    }

    private sealed class AdditiveUtilityModel : IEquipmentExactSolverUtilityModel
    {
        private static readonly EquipmentUtilityProfileKey Profile = new("synthetic-additive", "1");
        private static readonly EquipmentUtilityContext Context = new("solver-test", 19, 50, "Synthetic solver validation", []);

        public EquipmentPartialUtilityDominance ComparePartial(
            EquipmentSolverUtilityVector candidate,
            EquipmentSolverUtilityVector other)
        {
            var candidateScore = candidate.Components.Sum(component => component.Units);
            var otherScore = other.Components.Sum(component => component.Units);
            return new(candidateScore >= otherScore, candidateScore > otherScore);
        }

        public EquipmentUtilityEvaluation Evaluate(EquipmentSolverUtilityVector completed)
        {
            var score = completed.Components.Sum(component => component.Units);
            return new(
                Profile,
                Context,
                score,
                new(score, score, []),
                UpgradeAssessment.ClearImprovement,
                [],
                completed.Components.Select(component => new EquipmentStatContribution(
                    EquipmentStatSemantic.Unknown,
                    checked((int)component.Units),
                    1,
                    component.Units,
                    component.Key)).ToArray(),
                [],
                EquipmentEvaluationConfidence.High,
                []);
        }
    }

    private sealed class HalvedUtilityModel : IEquipmentExactSolverUtilityModel
    {
        private static readonly EquipmentUtilityProfileKey Profile = new("synthetic-halved", "1");
        private static readonly EquipmentUtilityContext Context = new("solver-test", 19, 50, "Synthetic fractional solver validation", []);

        public EquipmentPartialUtilityDominance ComparePartial(
            EquipmentSolverUtilityVector candidate,
            EquipmentSolverUtilityVector other)
        {
            var candidateScore = candidate.Components.Sum(component => component.Units);
            var otherScore = other.Components.Sum(component => component.Units);
            return new(candidateScore >= otherScore, candidateScore > otherScore);
        }

        public EquipmentUtilityEvaluation Evaluate(EquipmentSolverUtilityVector completed)
        {
            var score = completed.Components.Sum(component => component.Units) / 2d;
            return new(
                Profile,
                Context,
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

    private sealed class VectorUtilityModel : IEquipmentExactSolverUtilityModel, IEquipmentPartialDominanceCoordinateModel
    {
        private static readonly EquipmentUtilityProfileKey Profile = new("synthetic-vector", "1");
        private static readonly EquipmentUtilityContext Context = new("solver-vector-test", 19, 50, "Synthetic vector validation", []);

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
            new[] { utility.Get("gathering"), utility.Get("perception") };

        public EquipmentUtilityEvaluation Evaluate(EquipmentSolverUtilityVector completed)
        {
            var score = completed.Components.Sum(value => value.Units);
            return new(
                Profile,
                Context,
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
