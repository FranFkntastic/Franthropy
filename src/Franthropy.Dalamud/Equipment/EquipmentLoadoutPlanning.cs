using Franthropy.Dalamud.Characters;

namespace Franthropy.Dalamud.Equipment;

public enum EquipmentLoadoutPosition
{
    MainHand,
    OffHand,
    Head,
    Body,
    Hands,
    Legs,
    Feet,
    Ears,
    Neck,
    Wrists,
    LeftRing,
    RightRing,
}

public enum EquipmentAcquisitionSourceKind
{
    Owned = 0,
    GilVendor = 1,
    MarketBoard = 2,
    Craft = 3,
}

public enum EquipmentLoadoutStrategy
{
    BestOwned,
    MinimizeSpend,
    HighestItemLevel,
}

/// <summary>
/// Stable identity for an obtainable equipment choice. Observation-specific fields such as
/// price, quantity, world, and freshness deliberately do not participate in this key.
/// </summary>
public sealed record EquipmentOfferKey(
    uint ItemId,
    EquipmentQuality Quality,
    EquipmentAcquisitionSourceKind SourceKind,
    string SourceCatalogKey);

/// <summary>
/// One market row observed through an explicit evidence boundary. This is a UI/aggregator
/// observation contract, not a game-structure contract.
/// </summary>
public sealed record EquipmentMarketRowObservation(
    string RowId,
    uint ItemId,
    EquipmentQuality Quality,
    uint Quantity,
    uint UnitPriceGil,
    string? SellerLabel = null,
    string? RetainerLabel = null);

/// <summary>
/// Mutable evidence attached to a stable offer key. Consumers must revalidate observations
/// before acquisition rather than treating the stable key as proof that a listing still exists.
/// </summary>
public sealed record EquipmentOfferObservation(
    EquipmentOfferKey Key,
    Guid EvidenceGenerationId,
    string ObservationId,
    DateTimeOffset ReviewedAt,
    EquipmentInstanceSnapshot? OwnedInstance = null,
    EquipmentMarketRowObservation? ObservableMarketRow = null,
    string? AggregatorCorrelationId = null,
    string? World = null,
    uint AvailableQuantity = 1,
    uint? UnitPriceGil = null)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ObservationId);
        if (!Enum.IsDefined(Key.SourceKind))
            throw new InvalidOperationException($"Offer observation has unsupported acquisition source '{Key.SourceKind}'.");
        if (ReviewedAt == default)
            throw new InvalidOperationException("Offer observations require an explicit review time.");
        if (AvailableQuantity == 0)
            throw new InvalidOperationException("Offer observations must expose a positive available quantity.");
        if (OwnedInstance is not null && ObservableMarketRow is not null)
            throw new InvalidOperationException("An offer observation cannot be both an owned instance and a market row.");
        if (OwnedInstance is not null &&
            (Key.SourceKind != EquipmentAcquisitionSourceKind.Owned ||
             OwnedInstance.Fingerprint.ItemId != Key.ItemId ||
             EquipmentInstanceStats.ResolveQuality(OwnedInstance) != Key.Quality))
            throw new InvalidOperationException("Owned-instance evidence does not match the exact offer key.");
        if (ObservableMarketRow is not null &&
            (Key.SourceKind != EquipmentAcquisitionSourceKind.MarketBoard ||
             ObservableMarketRow.ItemId != Key.ItemId ||
             ObservableMarketRow.Quality != Key.Quality ||
             ObservableMarketRow.Quantity != AvailableQuantity ||
             ObservableMarketRow.UnitPriceGil != UnitPriceGil))
            throw new InvalidOperationException("Observable market-row evidence does not match the exact offer key or quote.");
    }
}

public sealed record EquipmentLoadoutOffer(
    EquipmentItemDefinition Definition,
    EquipmentAcquisitionSourceKind SourceKind,
    string SourceLabel,
    uint? UnitPriceGil = null,
    EquipmentInstanceSnapshot? Instance = null,
    bool PriceIsEstimate = false,
    EquipmentQuality Quality = EquipmentQuality.Normal,
    string? SourceCatalogKey = null,
    EquipmentOfferObservation? Observation = null)
{
    public EquipmentQuality ResolvedQuality => Instance is null
        ? Quality
        : EquipmentInstanceStats.ResolveQuality(Instance);

    public EquipmentOfferKey Key => new(
        Definition.ItemId,
        ResolvedQuality,
        SourceKind,
        SourceCatalogKey ?? ResolveDefaultCatalogKey());

    public string Identity => Instance is null
        ? $"{SourceKind}:{Definition.ItemId}:{ResolvedQuality}:{SourceCatalogKey ?? SourceLabel}"
        : $"Owned:{Instance.Fingerprint.Container}:{Instance.Fingerprint.SlotIndex}:{Definition.ItemId}:{ResolvedQuality}";

    public EquipmentStatProfile? ResolveStatProfile() => Definition.ResolveStatProfile(ResolvedQuality);

    public EquipmentOfferObservation? GetValidatedObservation()
    {
        if (Observation is null)
            return null;
        if (Observation.Key != Key)
            throw new InvalidOperationException("Attached observation does not match this offer's stable key.");
        Observation.Validate();
        return Observation;
    }

    private string ResolveDefaultCatalogKey() => SourceKind == EquipmentAcquisitionSourceKind.Owned && Instance is not null
        ? $"{Instance.Fingerprint.Character.LocalContentId}:{Instance.Fingerprint.Container}:{Instance.Fingerprint.SlotIndex}"
        : SourceLabel;
}

public sealed record EquipmentLoadoutRequest(
    CharacterJobSnapshot Job,
    uint TargetLevel,
    EquipmentLoadoutStrategy Strategy,
    IReadOnlyList<EquipmentLoadoutOffer> Offers,
    IReadOnlyDictionary<EquipmentLoadoutPosition, EquipmentLoadoutOffer> CurrentItems,
    bool IncludeOwned = true,
    bool IncludeGilVendors = true,
    bool IncludeMarketBoard = true,
    bool IncludeCraft = true);

public sealed record EquipmentLoadoutPlanEntry(
    EquipmentLoadoutPosition Position,
    EquipmentLoadoutOffer? Current,
    EquipmentLoadoutOffer? Recommended,
    int ItemLevelDelta,
    IReadOnlyList<EquipmentLoadoutOffer> Alternatives,
    string? Diagnostic = null,
    bool IsRequired = true)
{
    public bool IsMissing => IsRequired && Recommended is null;
    public bool RequiresAcquisition => Recommended is { SourceKind: not EquipmentAcquisitionSourceKind.Owned };
    public bool IsUpgrade => Recommended is not null &&
        (Current is null || Recommended.Definition.ItemLevel > Current.Definition.ItemLevel);
}

public sealed record EquipmentLoadoutPlan(
    CharacterJobSnapshot Job,
    uint TargetLevel,
    EquipmentLoadoutStrategy Strategy,
    IReadOnlyList<EquipmentLoadoutPlanEntry> Entries,
    uint CurrentAverageItemLevel,
    uint RecommendedAverageItemLevel,
    ulong EstimatedAcquisitionCost,
    int MissingSlotCount,
    int UpgradeCount,
    int AcquisitionCount);

public sealed class EquipmentLoadoutSolver
{
    private static readonly EquipmentLoadoutPosition[] Positions =
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

    public EquipmentLoadoutPlan Plan(EquipmentLoadoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Offers.Any(offer => !Enum.IsDefined(offer.SourceKind)) ||
            request.CurrentItems.Values.Any(offer => !Enum.IsDefined(offer.SourceKind)))
            throw new ArgumentException("Request contains an unsupported equipment acquisition source.", nameof(request));
        var targetLevel = Math.Clamp(request.TargetLevel, 1u, Math.Max(1u, request.Job.Level));
        var compatible = request.Offers
            .Where(offer => IsCompatible(offer, request, targetLevel))
            .ToArray();
        var entries = new List<EquipmentLoadoutPlanEntry>(Positions.Length);
        var allocatedOwned = new HashSet<string>(StringComparer.Ordinal);
        var allocatedUnique = new HashSet<uint>();

        foreach (var position in Positions)
        {
            request.CurrentItems.TryGetValue(position, out var current);
            if (position == EquipmentLoadoutPosition.OffHand &&
                entries.FirstOrDefault(entry => entry.Position == EquipmentLoadoutPosition.MainHand)?.Recommended?.Definition.OffHandOccupancy == -1)
            {
                entries.Add(new(
                    position,
                    current,
                    null,
                    0,
                    [],
                    "Occupied by the planned two-handed main-hand weapon.",
                    IsRequired: false));
                continue;
            }
            var candidates = OrderCandidates(compatible
                .Where(offer => Fits(position, offer.Definition.Slot))
                .Where(offer => offer.SourceKind != EquipmentAcquisitionSourceKind.Owned || !allocatedOwned.Contains(offer.Identity))
                .Where(offer => !offer.Definition.IsUnique || !allocatedUnique.Contains(offer.Definition.ItemId))
                .ToArray(), current, request.Strategy);
            var recommended = candidates.FirstOrDefault();
            if (recommended is not null)
            {
                if (recommended.SourceKind == EquipmentAcquisitionSourceKind.Owned)
                    allocatedOwned.Add(recommended.Identity);
                if (recommended.Definition.IsUnique)
                    allocatedUnique.Add(recommended.Definition.ItemId);
            }

            var delta = checked((int)(recommended?.Definition.ItemLevel ?? 0) - (int)(current?.Definition.ItemLevel ?? 0));
            entries.Add(new(
                position,
                current,
                recommended,
                delta,
                candidates.Skip(1).Take(3).ToArray(),
                recommended is null ? "No compatible accessible item was found for this slot." : null));
        }

        var currentAverage = AverageItemLevel(entries.Select(entry => entry.Current));
        var recommendedAverage = AverageItemLevel(entries.Select(entry => entry.Recommended));
        var estimatedCost = entries
            .Where(entry => entry.RequiresAcquisition)
            .Aggregate(0ul, (total, entry) => total + (entry.Recommended?.UnitPriceGil ?? 0));
        return new(
            request.Job,
            targetLevel,
            request.Strategy,
            entries,
            currentAverage,
            recommendedAverage,
            estimatedCost,
            entries.Count(entry => entry.IsMissing),
            entries.Count(entry => entry.IsUpgrade),
            entries.Count(entry => entry.RequiresAcquisition));
    }

    private static bool IsCompatible(
        EquipmentLoadoutOffer offer,
        EquipmentLoadoutRequest request,
        uint targetLevel)
    {
        var definition = offer.Definition;
        if (!definition.IsEquipment || definition.IsSoulCrystal || definition.IsSpecialPurpose ||
            definition.Slot == EquipmentSlot.Unknown || definition.EquipLevel > targetLevel ||
            !definition.EligibleClassJobIds.Contains(request.Job.ClassJobId))
            return false;

        return offer.SourceKind switch
        {
            EquipmentAcquisitionSourceKind.Owned => request.IncludeOwned,
            EquipmentAcquisitionSourceKind.GilVendor => request.IncludeGilVendors && request.Strategy != EquipmentLoadoutStrategy.BestOwned,
            EquipmentAcquisitionSourceKind.MarketBoard => request.IncludeMarketBoard && request.Strategy != EquipmentLoadoutStrategy.BestOwned,
            EquipmentAcquisitionSourceKind.Craft => request.IncludeCraft && request.Strategy != EquipmentLoadoutStrategy.BestOwned,
            _ => false,
        };
    }

    private static EquipmentLoadoutOffer[] OrderCandidates(
        IReadOnlyList<EquipmentLoadoutOffer> candidates,
        EquipmentLoadoutOffer? current,
        EquipmentLoadoutStrategy strategy)
    {
        if (strategy != EquipmentLoadoutStrategy.MinimizeSpend)
        {
            return candidates
                .OrderByDescending(offer => offer.Definition.ItemLevel)
                .ThenBy(offer => ScoreSource(offer, strategy))
                .ThenBy(offer => offer.UnitPriceGil ?? uint.MaxValue)
                .ThenBy(offer => offer.Definition.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var currentItemLevel = current?.Definition.ItemLevel ?? 0;
        var upgrades = candidates.Where(offer => offer.Definition.ItemLevel > currentItemLevel).ToArray();
        var pool = upgrades.Length > 0 ? upgrades : candidates;
        return pool
            .OrderBy(EffectiveAcquisitionCost)
            .ThenBy(offer => ScoreSource(offer, strategy))
            .ThenByDescending(offer => offer.Definition.ItemLevel)
            .ThenBy(offer => offer.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ulong EffectiveAcquisitionCost(EquipmentLoadoutOffer offer) => offer.SourceKind switch
    {
        EquipmentAcquisitionSourceKind.Owned => 0,
        _ when offer.UnitPriceGil is { } price => price,
        _ => ulong.MaxValue,
    };

    private static int ScoreSource(EquipmentLoadoutOffer offer, EquipmentLoadoutStrategy strategy) => strategy switch
    {
        EquipmentLoadoutStrategy.BestOwned => offer.SourceKind == EquipmentAcquisitionSourceKind.Owned ? 0 : 10,
        EquipmentLoadoutStrategy.MinimizeSpend => offer.SourceKind switch
        {
            EquipmentAcquisitionSourceKind.Owned => 0,
            EquipmentAcquisitionSourceKind.GilVendor => 1,
            EquipmentAcquisitionSourceKind.MarketBoard => 2,
            EquipmentAcquisitionSourceKind.Craft => 3,
            _ => 10,
        },
        _ => offer.SourceKind switch
        {
            EquipmentAcquisitionSourceKind.Owned => 0,
            EquipmentAcquisitionSourceKind.GilVendor => 1,
            EquipmentAcquisitionSourceKind.MarketBoard => 2,
            EquipmentAcquisitionSourceKind.Craft => 3,
            _ => 10,
        },
    };

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

    private static uint AverageItemLevel(IEnumerable<EquipmentLoadoutOffer?> offers)
    {
        var values = offers.Where(offer => offer is not null).Select(offer => offer!.Definition.ItemLevel).ToArray();
        return values.Length == 0 ? 0 : checked((uint)Math.Round(values.Average(value => (double)value)));
    }
}
