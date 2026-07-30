namespace Franthropy.Dalamud.Automation.MarketBoard;

public sealed record MarketBoardListing(
    ulong ListingId,
    uint ItemId,
    bool IsHighQuality,
    uint Quantity,
    uint UnitPrice,
    uint TotalTax,
    byte MateriaCount,
    ulong RetainerId,
    string RetainerName)
{
    public ulong TotalGil => ((ulong)UnitPrice * Quantity) + TotalTax;
}

public sealed record MarketBoardListingSource(
    uint ItemId,
    byte RequestId,
    int ListingCount);

public sealed record MarketBoardListingObservation(
    MarketBoardListingSource? Source,
    IReadOnlyList<MarketBoardListing> Listings)
{
    public bool IsComplete =>
        Source is { } source &&
        Listings.Count == source.ListingCount;

    public static MarketBoardListingObservation Empty { get; } =
        new(null, Array.Empty<MarketBoardListing>());
}

public sealed record MarketBoardBrowseEvidence(
    string OperationId,
    uint ItemId,
    byte RequestId,
    int ListingCount,
    bool IsComplete);

public sealed record MarketBoardListingRevision(
    MarketBoardListingSource Source,
    IReadOnlyList<MarketBoardListing> Listings,
    string? VerifiedBrowseOperationId,
    int VerifiedBrowseListingCount)
{
    public bool IsComplete => Listings.Count == Source.ListingCount;
    public bool IsPurchaseDerived => VerifiedBrowseListingCount > Source.ListingCount;
}

public enum MarketBoardListingTransition
{
    AdoptedObservation,
    PreservedPurchaseDerivedRevision,
    ConfirmedPurchase,
    RejectedObservation,
    RejectedPurchase,
}

public sealed record MarketBoardListingTransitionResult(
    MarketBoardListingTransition Transition,
    MarketBoardListingRevision? Revision,
    ulong? ListingId = null)
{
    public bool Changed =>
        Transition is MarketBoardListingTransition.AdoptedObservation
            or MarketBoardListingTransition.ConfirmedPurchase;
}

/// <summary>
/// Owns one truthful revision of the native market-board listing result.
/// Browse evidence verifies a revision; exact confirmed purchases derive later
/// revisions without pretending that the original browse happened again.
/// </summary>
public sealed class MarketBoardListingSession
{
    public MarketBoardListingRevision? Revision { get; private set; }

    public MarketBoardListingTransitionResult Observe(
        MarketBoardListingObservation observation,
        MarketBoardBrowseEvidence? browseEvidence)
    {
        if (observation.Source is not { } source ||
            source.ItemId == 0 ||
            source.ListingCount is < 0 or > 100 ||
            observation.Listings.Count > source.ListingCount ||
            observation.Listings.Any(listing =>
                listing.ListingId == 0 ||
                listing.ItemId != source.ItemId ||
                listing.Quantity == 0 ||
                listing.UnitPrice == 0))
        {
            return Result(MarketBoardListingTransition.RejectedObservation);
        }

        if (ShouldPreservePurchaseDerivedRevision(source))
            return Result(MarketBoardListingTransition.PreservedPurchaseDerivedRevision);

        var verifiedOperationId = IsExactEvidenceForObservation(
            browseEvidence,
            source,
            observation.Listings.Count)
                ? browseEvidence!.OperationId
                : null;
        Revision = new(
            source,
            observation.Listings.ToArray(),
            verifiedOperationId,
            source.ListingCount);
        return Result(MarketBoardListingTransition.AdoptedObservation);
    }

    public MarketBoardListingTransitionResult ConfirmPurchase(ulong listingId)
    {
        if (Revision is not { } revision ||
            string.IsNullOrWhiteSpace(revision.VerifiedBrowseOperationId) ||
            !revision.IsComplete ||
            revision.Source.ListingCount <= 0)
        {
            return Result(MarketBoardListingTransition.RejectedPurchase, listingId);
        }

        var matches = revision.Listings
            .Where(listing => listing.ListingId == listingId)
            .ToArray();
        if (matches.Length != 1)
            return Result(MarketBoardListingTransition.RejectedPurchase, listingId);

        Revision = revision with
        {
            Source = revision.Source with
            {
                ListingCount = revision.Source.ListingCount - 1,
            },
            Listings = revision.Listings
                .Where(listing => listing.ListingId != listingId)
                .ToArray(),
        };
        return Result(MarketBoardListingTransition.ConfirmedPurchase, listingId);
    }

    public bool IsCurrentNativePresentation(
        bool resultVisible,
        uint itemId,
        uint listingCount,
        byte? requestId) =>
        resultVisible &&
        Revision is { } revision &&
        itemId == revision.Source.ItemId &&
        requestId == revision.Source.RequestId &&
        listingCount >= (uint)revision.Source.ListingCount &&
        listingCount <= (uint)revision.VerifiedBrowseListingCount;

    public bool IsVerifiedForPurchase(MarketBoardBrowseEvidence? browseEvidence) =>
        Revision is { } revision &&
        revision.IsComplete &&
        !string.IsNullOrWhiteSpace(revision.VerifiedBrowseOperationId) &&
        browseEvidence is { IsComplete: true } &&
        string.Equals(
            revision.VerifiedBrowseOperationId,
            browseEvidence.OperationId,
            StringComparison.Ordinal) &&
        browseEvidence.ItemId == revision.Source.ItemId &&
        browseEvidence.RequestId == revision.Source.RequestId &&
        browseEvidence.ListingCount == revision.VerifiedBrowseListingCount;

    public void Clear() => Revision = null;

    private bool ShouldPreservePurchaseDerivedRevision(MarketBoardListingSource source) =>
        Revision is { IsPurchaseDerived: true } revision &&
        !string.IsNullOrWhiteSpace(revision.VerifiedBrowseOperationId) &&
        revision.Source.ItemId == source.ItemId &&
        revision.Source.RequestId == source.RequestId;

    private static bool IsExactEvidenceForObservation(
        MarketBoardBrowseEvidence? evidence,
        MarketBoardListingSource source,
        int capturedListingCount) =>
        evidence is { IsComplete: true } &&
        !string.IsNullOrWhiteSpace(evidence.OperationId) &&
        evidence.ItemId == source.ItemId &&
        evidence.RequestId == source.RequestId &&
        evidence.ListingCount == source.ListingCount &&
        capturedListingCount == source.ListingCount;

    private MarketBoardListingTransitionResult Result(
        MarketBoardListingTransition transition,
        ulong? listingId = null) =>
        new(transition, Revision, listingId);
}
