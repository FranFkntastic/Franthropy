using Franthropy.Dalamud.Equipment;

namespace Franthropy.Dalamud.Performance;

internal static class EquipmentExactFrontierRepresentativeWorkload
{
    public static EquipmentExactFrontierRequest Create(int candidatesPerPosition, int worldCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(candidatesPerPosition, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(worldCount, 1);

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
            var baselineOffer = Offer(
                position,
                itemId++,
                utility: 10,
                cost: 0,
                EquipmentAcquisitionSourceKind.Owned,
                slot);
            offers.Add(baselineOffer);
            baseline[position] = baselineOffer.AllocationKey;
            for (var option = 1; option < candidatesPerPosition; option++)
            {
                offers.Add(Offer(
                    position,
                    itemId++,
                    utility: 10 + option * 5,
                    cost: (ulong)(option * 1_000),
                    EquipmentAcquisitionSourceKind.MarketBoard,
                    slot,
                    world: $"world-{option % worldCount}"));
            }
        }

        return new(
            offers,
            positions.ToHashSet(),
            baseline,
            new RepresentativeEquipmentUtilityModel(),
            MaxRetainedRepresentatives: 4);
    }

    private static EquipmentExactSolverOffer Offer(
        EquipmentLoadoutPosition position,
        uint itemId,
        long utility,
        ulong cost,
        EquipmentAcquisitionSourceKind source,
        EquipmentSlot slot,
        string? world = null)
    {
        var definition = new EquipmentItemDefinition(
            itemId,
            $"Item {itemId}",
            1,
            (uint)Math.Max(1, utility),
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
            IsUnique: false,
            OffHandOccupancy: 0);
        var catalogKey = $"{source}-{itemId}";
        var offer = new EquipmentLoadoutOffer(
            definition,
            source,
            source.ToString(),
            (uint)cost,
            Quality: EquipmentQuality.Normal,
            SourceCatalogKey: catalogKey);
        return new(
            offer,
            ObservationId: null,
            new HashSet<EquipmentLoadoutPosition> { position },
            AvailableQuantity: 1,
            new([new("power", utility)]),
            cost,
            world,
            VendorStopKey: null,
            source == EquipmentAcquisitionSourceKind.Owned ? 0 : 1,
            new(0, 0, 0),
            [EquipmentQuality.Normal.ToString(), source.ToString()]);
    }

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
}

internal sealed class RepresentativeEquipmentUtilityModel : IEquipmentExactSolverUtilityModel
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
