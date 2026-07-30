using System.Threading;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace Franthropy.Dalamud.Automation.MarketBoard;

/// <summary>
/// Converts native market-board lifecycle and offering events into coalesced,
/// immutable listing observations. No native listing scan occurs from Draw().
/// </summary>
public sealed unsafe class DalamudMarketBoardListingObserver : IDisposable
{
    private const string ItemSearchResultAddon = "ItemSearchResult";

    private readonly IMarketBoard marketBoard;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly Dictionary<uint, Dictionary<ulong, string>> retainerNamesByItem = [];
    private int captureQueued;

    public DalamudMarketBoardListingObserver(
        IMarketBoard marketBoard,
        IAddonLifecycle addonLifecycle,
        IFramework framework,
        IGameGui gameGui)
    {
        this.marketBoard = marketBoard ?? throw new ArgumentNullException(nameof(marketBoard));
        this.addonLifecycle = addonLifecycle ?? throw new ArgumentNullException(nameof(addonLifecycle));
        this.framework = framework ?? throw new ArgumentNullException(nameof(framework));
        this.gameGui = gameGui ?? throw new ArgumentNullException(nameof(gameGui));

        marketBoard.OfferingsReceived += OnOfferingsReceived;
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, ItemSearchResultAddon, OnNativeListingsChanged);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, ItemSearchResultAddon, OnNativeListingsChanged);
        addonLifecycle.RegisterListener(AddonEvent.PostShow, ItemSearchResultAddon, OnNativeListingsChanged);
    }

    public MarketBoardListingObservation Snapshot { get; private set; } =
        MarketBoardListingObservation.Empty;

    public event Action<MarketBoardListingObservation>? Changed;

    public void Refresh() => QueueCapture();

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
    {
        var proxy = GetItemSearchProxy();
        if (proxy == null ||
            proxy->SearchItemId == 0 ||
            proxy->InfoProxyPageInterface.CurrentRequestId != unchecked((byte)offerings.RequestId) ||
            offerings.ItemListings.Any(listing => listing.ItemId != proxy->SearchItemId))
        {
            return;
        }

        if (offerings.ItemListings.Count > 0)
        {
            var itemId = proxy->SearchItemId;
            if (!retainerNamesByItem.TryGetValue(itemId, out var byListing))
            {
                byListing = [];
                retainerNamesByItem[itemId] = byListing;
            }

            foreach (var listing in offerings.ItemListings)
                byListing[listing.ListingId] = listing.RetainerName;
        }

        QueueCapture();
    }

    private void OnNativeListingsChanged(AddonEvent _, AddonArgs __) => QueueCapture();

    private void QueueCapture()
    {
        if (Interlocked.Exchange(ref captureQueued, 1) != 0)
            return;
        framework.RunOnTick(Capture);
    }

    private void Capture()
    {
        Volatile.Write(ref captureQueued, 0);
        var addon = gameGui.GetAddonByName<AddonItemSearchResult>(ItemSearchResultAddon, 1);
        var proxy = GetItemSearchProxy();
        if (addon == null ||
            !addon->AtkUnitBase.IsReady ||
            !addon->AtkUnitBase.IsVisible ||
            proxy == null ||
            proxy->SearchItemId == 0 ||
            proxy->ListingCount > 100)
        {
            return;
        }

        var itemId = proxy->SearchItemId;
        var requestId = proxy->InfoProxyPageInterface.CurrentRequestId;
        var listingCount = (int)proxy->ListingCount;
        var listings = new List<MarketBoardListing>(listingCount);
        for (var index = 0; index < listingCount; index++)
        {
            var listing = proxy->Listings[index];
            if (listing.ItemId != itemId ||
                listing.ListingId == 0 ||
                listing.RetainerId == 0 ||
                listing.UnitPrice == 0 ||
                listing.Quantity == 0)
            {
                // ListingCount advances before continuation pages finish. Publish
                // only the contiguous truthful prefix and extend it on later events.
                break;
            }

            listings.Add(new(
                listing.ListingId,
                listing.ItemId,
                listing.IsHqItem,
                listing.Quantity,
                listing.UnitPrice,
                listing.TotalTax,
                listing.MateriaCount,
                listing.RetainerId,
                retainerNamesByItem.TryGetValue(itemId, out var byListing) &&
                byListing.TryGetValue(listing.ListingId, out var retainerName)
                    ? retainerName
                    : string.Empty));
        }

        if (listingCount > 0 && listings.Count == 0)
            return;

        var candidate = new MarketBoardListingObservation(
            new(itemId, requestId, listingCount),
            listings);
        if (Equals(Snapshot.Source, candidate.Source) &&
            Snapshot.Listings.SequenceEqual(candidate.Listings))
        {
            return;
        }

        Snapshot = candidate;
        Changed?.Invoke(candidate);
    }

    private static InfoProxyItemSearch* GetItemSearchProxy()
    {
        var infoModule = InfoModule.Instance();
        return infoModule == null
            ? null
            : (InfoProxyItemSearch*)infoModule->GetInfoProxyById(InfoProxyId.ItemSearch);
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostShow, ItemSearchResultAddon, OnNativeListingsChanged);
        addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, ItemSearchResultAddon, OnNativeListingsChanged);
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, ItemSearchResultAddon, OnNativeListingsChanged);
        marketBoard.OfferingsReceived -= OnOfferingsReceived;
    }
}
