using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.Performance;

namespace Franthropy.Dalamud.Tests.Equipment;

public sealed class EquipmentExactFrontierSolverTests
{
    private static readonly RepresentativeEquipmentUtilityModel UtilityModel = new();

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

}
