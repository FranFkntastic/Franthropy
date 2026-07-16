namespace Franthropy.Dalamud.Equipment;

public enum EquipmentFeasibilityViolationKind
{
    MissingOffer,
    DuplicatePosition,
    InvalidQuantity,
    InsufficientQuantity,
    IncompatibleSlot,
    UniqueItemConflict,
    HandOccupancyConflict,
    MissingRequiredPosition,
}

public sealed record EquipmentFeasibilityOffer(
    EquipmentLoadoutOffer Offer,
    uint AvailableQuantity);

public sealed record EquipmentFeasibilityViolation(
    EquipmentFeasibilityViolationKind Kind,
    string Message,
    EquipmentLoadoutPosition? Position = null,
    EquipmentOfferKey? OfferKey = null);

public sealed record EquipmentLoadoutFeasibilityRequest(
    EquipmentLoadoutCandidate Candidate,
    IReadOnlyList<EquipmentFeasibilityOffer> Offers,
    IReadOnlySet<EquipmentLoadoutPosition> RequiredPositions);

public sealed record EquipmentLoadoutFeasibilityResult(
    bool IsFeasible,
    IReadOnlyList<EquipmentFeasibilityViolation> Violations);

public interface IEquipmentLoadoutFeasibilityEvaluator
{
    EquipmentLoadoutFeasibilityResult Evaluate(EquipmentLoadoutFeasibilityRequest request);
}

/// <summary>
/// Pure feasibility validation for a proposed whole loadout. It handles exact offer quantities,
/// unique items, ring allocation, and two-handed occupancy without reading live game state.
/// </summary>
public sealed class EquipmentLoadoutFeasibilityEvaluator : IEquipmentLoadoutFeasibilityEvaluator
{
    public EquipmentLoadoutFeasibilityResult Evaluate(EquipmentLoadoutFeasibilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var violations = new List<EquipmentFeasibilityViolation>();
        var offers = request.Offers
            .GroupBy(value => value.Offer.Key)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var duplicate in request.Candidate.Selections.GroupBy(selection => selection.Position).Where(group => group.Count() > 1))
            violations.Add(new(EquipmentFeasibilityViolationKind.DuplicatePosition, $"Position {duplicate.Key} is allocated more than once.", duplicate.Key));

        foreach (var required in request.RequiredPositions.Except(request.Candidate.Selections.Select(selection => selection.Position)))
            violations.Add(new(EquipmentFeasibilityViolationKind.MissingRequiredPosition, $"Required position {required} is unfilled.", required));

        foreach (var selection in request.Candidate.Selections)
        {
            if (selection.Quantity != 1)
            {
                violations.Add(new(
                    EquipmentFeasibilityViolationKind.InvalidQuantity,
                    $"Equipment position {selection.Position} must consume exactly one item.",
                    selection.Position,
                    selection.OfferKey));
            }
            if (!offers.TryGetValue(selection.OfferKey, out var available))
            {
                violations.Add(new(
                    EquipmentFeasibilityViolationKind.MissingOffer,
                    $"Offer {selection.OfferKey} is not present in the feasibility book.",
                    selection.Position,
                    selection.OfferKey));
                continue;
            }
            if (!Fits(selection.Position, available.Offer.Definition.Slot))
            {
                violations.Add(new(
                    EquipmentFeasibilityViolationKind.IncompatibleSlot,
                    $"{available.Offer.Definition.Name} cannot occupy {selection.Position}.",
                    selection.Position,
                    selection.OfferKey));
            }
        }

        foreach (var allocation in request.Candidate.Selections.GroupBy(selection => selection.OfferKey))
        {
            if (!offers.TryGetValue(allocation.Key, out var available))
                continue;
            var consumed = allocation.Aggregate(0ul, (sum, selection) => sum + selection.Quantity);
            if (consumed > available.AvailableQuantity)
                violations.Add(new(
                    EquipmentFeasibilityViolationKind.InsufficientQuantity,
                    $"Offer {allocation.Key} requires {consumed} items but only {available.AvailableQuantity} are available.",
                    OfferKey: allocation.Key));
        }

        foreach (var unique in request.Candidate.Selections
            .Where(selection => offers.TryGetValue(selection.OfferKey, out var value) && value.Offer.Definition.IsUnique)
            .GroupBy(selection => selection.OfferKey.ItemId)
            .Where(group => group.Sum(selection => selection.Quantity) > 1))
            violations.Add(new(
                EquipmentFeasibilityViolationKind.UniqueItemConflict,
                $"Unique item {unique.Key} is allocated more than once."));

        var mainHand = request.Candidate.Selections.FirstOrDefault(selection => selection.Position == EquipmentLoadoutPosition.MainHand);
        if (mainHand is not null &&
            offers.TryGetValue(mainHand.OfferKey, out var mainHandOffer) &&
            mainHandOffer.Offer.Definition.OffHandOccupancy == -1 &&
            request.Candidate.Selections.Any(selection => selection.Position == EquipmentLoadoutPosition.OffHand))
        {
            violations.Add(new(
                EquipmentFeasibilityViolationKind.HandOccupancyConflict,
                $"{mainHandOffer.Offer.Definition.Name} occupies the off-hand position.",
                EquipmentLoadoutPosition.OffHand,
                mainHand.OfferKey));
        }

        return new(violations.Count == 0, violations);
    }

    private static bool Fits(EquipmentLoadoutPosition position, EquipmentSlot slot) => position switch
    {
        EquipmentLoadoutPosition.MainHand => slot == EquipmentSlot.MainHand,
        EquipmentLoadoutPosition.OffHand => slot == EquipmentSlot.OffHand,
        EquipmentLoadoutPosition.Head => slot == EquipmentSlot.Head,
        EquipmentLoadoutPosition.Body => slot == EquipmentSlot.Body,
        EquipmentLoadoutPosition.Hands => slot == EquipmentSlot.Hands,
        EquipmentLoadoutPosition.Legs => slot == EquipmentSlot.Legs,
        EquipmentLoadoutPosition.Feet => slot == EquipmentSlot.Feet,
        EquipmentLoadoutPosition.Ears => slot == EquipmentSlot.Ears,
        EquipmentLoadoutPosition.Neck => slot == EquipmentSlot.Neck,
        EquipmentLoadoutPosition.Wrists => slot == EquipmentSlot.Wrists,
        EquipmentLoadoutPosition.LeftRing or EquipmentLoadoutPosition.RightRing => slot == EquipmentSlot.Ring,
        _ => false,
    };
}
