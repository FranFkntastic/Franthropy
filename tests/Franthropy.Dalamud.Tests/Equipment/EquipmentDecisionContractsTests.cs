using Franthropy.Dalamud.Equipment;

namespace Franthropy.Dalamud.Tests.Equipment;

public sealed class EquipmentDecisionContractsTests
{
    [Fact]
    public void OfferIdentity_PreservesExactQualityAndSeparatesMutableObservation()
    {
        var definition = Definition(100, EquipmentSlot.Head);
        var nq = new EquipmentLoadoutOffer(definition, EquipmentAcquisitionSourceKind.MarketBoard, "market", 1_000, Quality: EquipmentQuality.Normal, SourceCatalogKey: "listing-source");
        var hq = nq with { Quality = EquipmentQuality.High, UnitPriceGil = 3_000 };

        Assert.NotEqual(nq.Key, hq.Key);
        Assert.Equal(EquipmentQuality.Normal, nq.Key.Quality);
        Assert.Equal(EquipmentQuality.High, hq.Key.Quality);
        Assert.Equal(10, nq.ResolveStatProfile()?.PhysicalDefense);
        Assert.Equal(15, hq.ResolveStatProfile()?.PhysicalDefense);
        Assert.DoesNotContain("1000", nq.Identity, StringComparison.Ordinal);
        Assert.DoesNotContain("3000", hq.Identity, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnedOffer_ResolvesQualityFromObservedInstance()
    {
        var definition = Definition(100, EquipmentSlot.Head);
        var instance = new EquipmentInstanceSnapshot(
            new(new(12, "Test", 1), "Armory", 4, 100, true, 1, 30_000, 0, null, [], null, []),
            DateTimeOffset.UtcNow,
            false);
        var offer = new EquipmentLoadoutOffer(definition, EquipmentAcquisitionSourceKind.Owned, "owned", Instance: instance);

        Assert.Equal(EquipmentQuality.High, offer.ResolvedQuality);
        Assert.Equal(EquipmentQuality.High, offer.Key.Quality);
        Assert.Equal(15, offer.ResolveStatProfile()?.PhysicalDefense);
    }

    [Fact]
    public void OfferObservation_RejectsQualityMismatchInsteadOfSilentlyReusingQuote()
    {
        var definition = Definition(100, EquipmentSlot.Head);
        var key = new EquipmentOfferKey(100, EquipmentQuality.High, EquipmentAcquisitionSourceKind.MarketBoard, "market");
        var observation = new EquipmentOfferObservation(
            key,
            Guid.NewGuid(),
            "row-1",
            DateTimeOffset.UtcNow,
            ObservableMarketRow: new("row-1", 100, EquipmentQuality.Normal, 1, 1_000),
            AvailableQuantity: 1,
            UnitPriceGil: 1_000);
        var offer = new EquipmentLoadoutOffer(
            definition,
            EquipmentAcquisitionSourceKind.MarketBoard,
            "market",
            1_000,
            Quality: EquipmentQuality.High,
            SourceCatalogKey: "market",
            Observation: observation);

        Assert.Throws<InvalidOperationException>(() => offer.GetValidatedObservation());
    }

    [Fact]
    public void Dominance_RequiresEveryDimensionAndCompatibleContext()
    {
        var strong = Solution("strong", 10_000, 80, new(0, 1, 1), new(0, 0, 0));
        var weak = Solution("weak", 15_000, 75, new(1, 2, 2), new(1, 1, 1));
        var cheaperButRiskier = Solution("risky", 5_000, 80, new(0, 1, 1), new(2, 0, 0));
        var differentContext = weak with
        {
            Utility = weak.Utility with
            {
                Context = weak.Utility.Context with { ContextId = "different" },
            },
        };

        Assert.True(EquipmentDecisionDominance.Dominates(strong, weak));
        Assert.False(EquipmentDecisionDominance.Dominates(cheaperButRiskier, strong));
        Assert.False(EquipmentDecisionDominance.Dominates(strong, differentContext));
    }

    [Fact]
    public void Frontier_RetainsEquivalentVariantsAndExplainsDominatedSolutions()
    {
        var nq = Solution("nq", 10_000, 80, new(0, 1, 1), new(0, 0, 0), EquipmentQuality.Normal);
        var hq = Solution("hq", 10_000, 80, new(0, 1, 1), new(0, 0, 0), EquipmentQuality.High);
        var expensive = Solution("expensive", 15_000, 78, new(1, 2, 2), new(1, 1, 1));

        var result = new EquipmentParetoFrontierBuilder().Build([expensive, hq, nq]);

        Assert.Equal(["hq", "nq"], result.Frontier.Select(value => value.Candidate.SolutionId).Order().ToArray());
        Assert.Single(result.Dominated);
        Assert.Equal("expensive", result.Dominated[0].Solution.Candidate.SolutionId);
        Assert.Equal(["hq", "nq"], result.Dominated[0].DominatingSolutionIds);
        Assert.Single(result.EquivalenceGroups);
        Assert.Equal(2, result.EquivalenceGroups[0].Variants.Count);
    }

    [Fact]
    public void Frontier_ReportsAdjacentTradeoffAndStructuralDiff()
    {
        var cheap = Solution("cheap", 1_000, 50, new(0, 1, 1), new(0, 0, 0), EquipmentQuality.Normal, itemId: 100);
        var useful = Solution("useful", 10_000, 80, new(0, 1, 1), new(0, 0, 0), EquipmentQuality.High, itemId: 101);

        var result = new EquipmentParetoFrontierBuilder().Build([useful, cheap]);

        var adjacency = Assert.Single(result.Adjacencies);
        Assert.Equal(9_000, adjacency.CostDeltaGil);
        Assert.Equal(30, adjacency.UtilityDelta);
        var change = Assert.Single(adjacency.StructuralDiff.Changes);
        Assert.Equal(EquipmentLoadoutPosition.Head, change.Position);
        Assert.Equal(100u, change.Before?.ItemId);
        Assert.Equal(101u, change.After?.ItemId);
    }

    [Fact]
    public void ReplaySerialization_IsCanonicalAcrossInputOrdering()
    {
        var first = Solution("a", 1_000, 50, new(0, 1, 1), new(0, 0, 0));
        var second = Solution("b", 2_000, 60, new(0, 1, 1), new(0, 0, 0));
        var generation = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var capturedAt = DateTimeOffset.Parse("2026-07-16T12:00:00Z");

        var left = EquipmentDecisionReplayJson.Serialize(new(generation, capturedAt, [second, first]));
        var right = EquipmentDecisionReplayJson.Serialize(new(generation, capturedAt, [first, second]));
        var roundTrip = EquipmentDecisionReplayJson.Deserialize(left);

        Assert.Equal(right, left);
        Assert.Equal(["a", "b"], roundTrip.Solutions.Select(solution => solution.Candidate.SolutionId).ToArray());
    }

    [Fact]
    public void Feasibility_EnforcesExactQuantityAndUniqueRingAllocation()
    {
        var definition = Definition(200, EquipmentSlot.Ring, isUnique: true);
        var offer = new EquipmentLoadoutOffer(
            definition,
            EquipmentAcquisitionSourceKind.MarketBoard,
            "market",
            Quality: EquipmentQuality.Normal,
            SourceCatalogKey: "ring-listing");
        var candidate = new EquipmentLoadoutCandidate("two-rings",
        [
            new(EquipmentLoadoutPosition.LeftRing, offer.Key),
            new(EquipmentLoadoutPosition.RightRing, offer.Key),
        ]);

        var result = new EquipmentLoadoutFeasibilityEvaluator().Evaluate(new(
            candidate,
            [new(offer, 1)],
            new HashSet<EquipmentLoadoutPosition> { EquipmentLoadoutPosition.LeftRing, EquipmentLoadoutPosition.RightRing }));

        Assert.False(result.IsFeasible);
        Assert.Contains(result.Violations, violation => violation.Kind == EquipmentFeasibilityViolationKind.InsufficientQuantity);
        Assert.Contains(result.Violations, violation => violation.Kind == EquipmentFeasibilityViolationKind.UniqueItemConflict);
    }

    [Fact]
    public void Feasibility_EnforcesTwoHandedOccupancyAndRequiredPositions()
    {
        var weapon = new EquipmentLoadoutOffer(
            Definition(300, EquipmentSlot.MainHand, offHandOccupancy: -1),
            EquipmentAcquisitionSourceKind.GilVendor,
            "vendor");
        var shield = new EquipmentLoadoutOffer(
            Definition(301, EquipmentSlot.OffHand),
            EquipmentAcquisitionSourceKind.GilVendor,
            "vendor");
        var candidate = new EquipmentLoadoutCandidate("invalid-hands",
        [
            new(EquipmentLoadoutPosition.MainHand, weapon.Key),
            new(EquipmentLoadoutPosition.OffHand, shield.Key),
        ]);

        var result = new EquipmentLoadoutFeasibilityEvaluator().Evaluate(new(
            candidate,
            [new(weapon, 1), new(shield, 1)],
            new HashSet<EquipmentLoadoutPosition> { EquipmentLoadoutPosition.MainHand, EquipmentLoadoutPosition.OffHand, EquipmentLoadoutPosition.Head }));

        Assert.False(result.IsFeasible);
        Assert.Contains(result.Violations, violation => violation.Kind == EquipmentFeasibilityViolationKind.HandOccupancyConflict);
        Assert.Contains(result.Violations, violation => violation.Kind == EquipmentFeasibilityViolationKind.MissingRequiredPosition && violation.Position == EquipmentLoadoutPosition.Head);
    }

    private static EquipmentDecisionSolution Solution(
        string id,
        ulong cost,
        double utility,
        EquipmentAcquisitionBurden burden,
        EquipmentEvidenceRisk risk,
        EquipmentQuality quality = EquipmentQuality.Normal,
        uint itemId = 100)
    {
        var key = new EquipmentOfferKey(itemId, quality, EquipmentAcquisitionSourceKind.MarketBoard, $"catalog-{itemId}");
        var candidate = new EquipmentLoadoutCandidate(id, [new(EquipmentLoadoutPosition.Head, key)]);
        var evaluation = new EquipmentUtilityEvaluation(
            new("leveling-combat", "1"),
            new("solo-leveling", 19, 50, "Solo leveling", ["leveling", "solo"]),
            utility,
            new(utility - 1, utility + 1, ["secondary-stat approximation"]),
            UpgradeAssessment.ClearImprovement,
            [new(EquipmentStatSemantic.Vitality, 10, "item")],
            [new(EquipmentStatSemantic.Vitality, 10, 1, 10, "survivability")],
            [],
            EquipmentEvaluationConfidence.High,
            []);
        return new(candidate, evaluation, cost, burden, risk, [quality.ToString()]);
    }

    private static EquipmentItemDefinition Definition(
        uint itemId,
        EquipmentSlot slot,
        bool isUnique = false,
        sbyte offHandOccupancy = 0) => new(
        itemId,
        "Test item",
        1,
        10,
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
        StatProfile: new([], 0, 0, 10, 10, true),
        HighQualityStatProfile: new([], 0, 0, 15, 15, true),
        IsUnique: isUnique,
        OffHandOccupancy: offHandOccupancy);
}
