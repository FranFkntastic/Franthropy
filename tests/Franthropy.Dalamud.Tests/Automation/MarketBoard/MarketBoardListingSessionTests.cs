using Franthropy.Dalamud.Automation.MarketBoard;

namespace Franthropy.Dalamud.Tests.Automation.MarketBoard;

public sealed class MarketBoardListingSessionTests
{
    private const uint ItemId = 22528;
    private const byte RequestId = 7;
    private const string OperationId = "browse:1";

    [Fact]
    public void ManualObservationIsPresentableButNotVerifiedForPurchase()
    {
        var session = new MarketBoardListingSession();

        var result = session.Observe(Observation(10, 20), browseEvidence: null);

        Assert.Equal(MarketBoardListingTransition.AdoptedObservation, result.Transition);
        Assert.Equal([10UL, 20UL], session.Revision!.Listings.Select(listing => listing.ListingId));
        Assert.True(session.IsCurrentNativePresentation(true, ItemId, 2, RequestId));
        Assert.False(session.IsVerifiedForPurchase(Evidence(2)));
    }

    [Fact]
    public void ExactBrowseEvidenceVerifiesTheCompleteObservedRevision()
    {
        var session = new MarketBoardListingSession();

        session.Observe(Observation(10, 20), Evidence(2));

        Assert.Equal(OperationId, session.Revision!.VerifiedBrowseOperationId);
        Assert.True(session.IsVerifiedForPurchase(Evidence(2)));
    }

    [Fact]
    public void ConfirmedPurchaseDerivesTheNextRevisionWithoutInventingABrowse()
    {
        var session = VerifiedSession();

        var result = session.ConfirmPurchase(10);

        Assert.Equal(MarketBoardListingTransition.ConfirmedPurchase, result.Transition);
        Assert.Equal([20UL], session.Revision!.Listings.Select(listing => listing.ListingId));
        Assert.Equal(1, session.Revision.Source.ListingCount);
        Assert.Equal(2, session.Revision.VerifiedBrowseListingCount);
        Assert.Equal(OperationId, session.Revision.VerifiedBrowseOperationId);
        Assert.True(session.IsVerifiedForPurchase(Evidence(2)));
        Assert.True(session.IsCurrentNativePresentation(true, ItemId, 1, RequestId));
    }

    [Fact]
    public void PurchaseDerivedRevisionRemainsCurrentWhileNativeCountCatchesUp()
    {
        var session = VerifiedSession();
        session.ConfirmPurchase(10);

        Assert.True(session.IsCurrentNativePresentation(true, ItemId, 2, RequestId));
        Assert.True(session.IsCurrentNativePresentation(true, ItemId, 1, RequestId));
        Assert.False(session.IsCurrentNativePresentation(true, ItemId, 3, RequestId));
        Assert.False(session.IsCurrentNativePresentation(true, ItemId, 2, RequestId + 1));
    }

    [Fact]
    public void LateSameRequestObservationCannotResurrectPurchasedListings()
    {
        var session = VerifiedSession();
        session.ConfirmPurchase(10);

        var result = session.Observe(Observation(10, 20), Evidence(2));

        Assert.Equal(
            MarketBoardListingTransition.PreservedPurchaseDerivedRevision,
            result.Transition);
        Assert.Equal([20UL], session.Revision!.Listings.Select(listing => listing.ListingId));
    }

    [Fact]
    public void NewRequestSupersedesAPurchaseDerivedRevision()
    {
        var session = VerifiedSession();
        session.ConfirmPurchase(10);

        var next = new MarketBoardListingObservation(
            new(ItemId, RequestId + 1, 1),
            [Listing(30)]);
        var result = session.Observe(
            next,
            new("browse:2", ItemId, RequestId + 1, 1, true));

        Assert.Equal(MarketBoardListingTransition.AdoptedObservation, result.Transition);
        Assert.Equal([30UL], session.Revision!.Listings.Select(listing => listing.ListingId));
        Assert.Equal("browse:2", session.Revision.VerifiedBrowseOperationId);
    }

    [Fact]
    public void PartialObservationCannotBeVerifiedOrAdvanced()
    {
        var session = new MarketBoardListingSession();
        var partial = new MarketBoardListingObservation(
            new(ItemId, RequestId, 2),
            [Listing(10)]);
        session.Observe(partial, Evidence(2));

        var result = session.ConfirmPurchase(10);

        Assert.Null(session.Revision!.VerifiedBrowseOperationId);
        Assert.False(session.IsVerifiedForPurchase(Evidence(2)));
        Assert.Equal(MarketBoardListingTransition.RejectedPurchase, result.Transition);
        Assert.Single(session.Revision.Listings);
    }

    [Fact]
    public void MalformedObservationCannotReplaceLastTruthfulRevision()
    {
        var session = VerifiedSession();
        var malformed = new MarketBoardListingObservation(
            new(ItemId, RequestId, 1),
            [Listing(10), Listing(20)]);

        var result = session.Observe(malformed, Evidence(1));

        Assert.Equal(MarketBoardListingTransition.RejectedObservation, result.Transition);
        Assert.Equal([10UL, 20UL], session.Revision!.Listings.Select(listing => listing.ListingId));
    }

    private static MarketBoardListingSession VerifiedSession()
    {
        var session = new MarketBoardListingSession();
        session.Observe(Observation(10, 20), Evidence(2));
        return session;
    }

    private static MarketBoardListingObservation Observation(params ulong[] listingIds) =>
        new(
            new(ItemId, RequestId, listingIds.Length),
            listingIds.Select(Listing).ToArray());

    private static MarketBoardBrowseEvidence Evidence(int listingCount) =>
        new(OperationId, ItemId, RequestId, listingCount, true);

    private static MarketBoardListing Listing(ulong listingId) =>
        new(
            listingId,
            ItemId,
            false,
            1,
            100,
            5,
            0,
            listingId + 1_000,
            $"Retainer {listingId}");
}
