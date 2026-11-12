using Franthropy.Dalamud.Automation.MarketBoard;

namespace Franthropy.Dalamud.Tests.Automation.MarketBoard;

public sealed class MarketBoardListingSessionTests
{
    private const uint ItemId = 22528;
    private const byte RequestId = 7;
    private const string OperationId = "browse:1";

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
