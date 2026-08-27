using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Franthropy.Dalamud.Automation.Vendors;

/// <summary>
/// Projects only ordinary GilShop rows with a concrete NPC and Level-sheet location.
/// Consumers own route selection and may retain unresolved catalog evidence separately.
/// </summary>
public static class DalamudGilVendorCatalogBuilder
{
    public static GilVendorCatalog Build(IDataManager dataManager)
    {
        ArgumentNullException.ThrowIfNull(dataManager);

        var gilShops = dataManager.GetExcelSheet<GilShop>()
            .ToDictionary(row => row.RowId);
        var npcNames = dataManager.GetExcelSheet<ENpcResident>()
            .Where(row => row.RowId != 0)
            .ToDictionary(row => row.RowId, row => row.Singular.ToString());
        var shopNpcs = new Dictionary<uint, Dictionary<uint, HashSet<uint>>>();
        foreach (var npc in dataManager.GetExcelSheet<ENpcBase>())
        {
            foreach (var data in npc.ENpcData)
            {
                if (data.Is<GilShop>() && gilShops.TryGetValue(data.RowId, out var directShop))
                {
                    AddShopNpc(shopNpcs, data.RowId, npc.RowId, directShop.Quest.RowId);
                    continue;
                }
                if (data.Is<PreHandler>() &&
                    data.TryGetValue(out PreHandler preHandler) &&
                    preHandler.Target.Is<GilShop>() &&
                    gilShops.TryGetValue(preHandler.Target.RowId, out var handledShop))
                {
                    AddShopNpc(
                        shopNpcs,
                        preHandler.Target.RowId,
                        npc.RowId,
                        handledShop.Quest.RowId,
                        preHandler.UnlockQuest.RowId);
                    continue;
                }
                if (!data.Is<TopicSelect>() || !data.TryGetValue(out TopicSelect topic))
                    continue;
                foreach (var shop in topic.Shop.Where(value => value.Is<GilShop>() && gilShops.ContainsKey(value.RowId)))
                    AddShopNpc(shopNpcs, shop.RowId, npc.RowId, gilShops[shop.RowId].Quest.RowId);
            }
        }

        var locations = DalamudVendorLocationCatalogBuilder.Build(
            dataManager,
            shopNpcs.Values.SelectMany(npcs => npcs.Keys).ToHashSet());
        var aetheryteNodes = dataManager.GetExcelSheet<Aetheryte>()
            .Where(row => row.RowId != 0 && row.Territory.RowId != 0)
            .Select(row => new GilVendorAetheryteNode(
                row.RowId,
                row.Territory.RowId,
                row.AethernetGroup,
                row.IsAetheryte))
            .ToArray();
        var travelRoutes = locations.Values
            .SelectMany(npcLocations => npcLocations)
            .Select(location => location.TerritoryId)
            .Distinct()
            .ToDictionary(
                territoryId => territoryId,
                territoryId => ResolveTravelRoutes(territoryId, aetheryteNodes));
        var routeAetheryteIds = travelRoutes.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<uint>)pair.Value
                .Select(route => route.AetheryteId)
                .Distinct()
                .ToArray());
        var items = dataManager.GetExcelSheet<Item>()
            .Where(row => row.RowId != 0)
            .ToDictionary(row => row.RowId);
        var routeTerritoryIds = locations.Values
            .SelectMany(npcLocations => npcLocations)
            .Select(location => location.TerritoryId)
            .Distinct()
            .ToHashSet();

        var offers = new List<GilVendorOffer>();
        var pendingOffers = new List<GilVendorOffer>();
        foreach (var shop in dataManager.GetSubrowExcelSheet<GilShopItem>())
        {
            if (!shopNpcs.TryGetValue(shop.RowId, out var npcs))
                continue;

            uint shopRowIndex = 0;
            foreach (var shopItem in shop)
            {
                var itemId = shopItem.Item.RowId;
                if (itemId == 0 || !items.TryGetValue(itemId, out var item) || item.PriceMid == 0)
                {
                    shopRowIndex++;
                    continue;
                }

                foreach (var (npcId, requiredQuestIds) in npcs)
                {
                    if (!npcNames.TryGetValue(npcId, out var npcName) ||
                        string.IsNullOrWhiteSpace(npcName))
                    {
                        continue;
                    }

                    if (!locations.TryGetValue(npcId, out var npcLocations))
                    {
                        // Dynamically spawned vendor with no static placement:
                        // remember the offer without a location so a live
                        // observation can promote it later.
                        pendingOffers.Add(new(
                            itemId,
                            item.Name.ToString(),
                            item.Icon,
                            item.PriceMid,
                            shop.RowId,
                            shopRowIndex,
                            npcId,
                            npcName,
                            0,
                            default,
                            [])
                        {
                            RequiredQuestIds = requiredQuestIds.Order().ToArray(),
                        });
                        continue;
                    }

                    foreach (var location in npcLocations)
                    {
                        if (!routeTerritoryIds.Contains(location.TerritoryId))
                            continue;
                        var routes = travelRoutes[location.TerritoryId];
                        offers.Add(new(
                            itemId,
                            item.Name.ToString(),
                            item.Icon,
                            item.PriceMid,
                            shop.RowId,
                            shopRowIndex,
                            npcId,
                            npcName,
                            location.TerritoryId,
                            location.Position,
                            routeAetheryteIds[location.TerritoryId])
                        {
                            TravelRoutes = routes,
                            RequiredQuestIds = requiredQuestIds.Order().ToArray(),
                        });
                    }
                }

                shopRowIndex++;
            }
        }

        return GilVendorCatalog.Create(offers, pendingOffers);
    }

    /// <summary>
    /// Resolves travel routes for a territory observed at runtime, so callers can
    /// promote pending vendors to executable offers with live locations.
    /// </summary>
    public static IReadOnlyList<GilVendorTravelRoute> ResolveTravelRoutesForTerritory(
        IDataManager dataManager,
        uint targetTerritoryId)
    {
        ArgumentNullException.ThrowIfNull(dataManager);
        var nodes = dataManager.GetExcelSheet<Aetheryte>()
            .Where(row => row.RowId != 0 && row.Territory.RowId != 0)
            .Select(row => new GilVendorAetheryteNode(
                row.RowId,
                row.Territory.RowId,
                row.AethernetGroup,
                row.IsAetheryte))
            .ToArray();
        return ResolveTravelRoutes(targetTerritoryId, nodes);
    }

    internal static IReadOnlyList<GilVendorTravelRoute> ResolveTravelRoutes(
        uint targetTerritoryId,
        IReadOnlyList<GilVendorAetheryteNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var direct = nodes
            .Where(node => node.IsAetheryte && node.TerritoryId == targetTerritoryId)
            .Select(node => new GilVendorTravelRoute(
                node.Id,
                AetheryteTerritoryId: node.TerritoryId))
            .Distinct()
            .OrderBy(route => route.AetheryteId)
            .ToArray();
        if (direct.Length != 0)
            return direct;

        var destinationShards = nodes
            .Where(node => !node.IsAetheryte &&
                           node.TerritoryId == targetTerritoryId &&
                           node.AethernetGroup != 0)
            .OrderBy(node => node.Id)
            .ToArray();
        if (destinationShards.Length == 0)
            return [];

        return destinationShards
            .SelectMany(shard => nodes
                .Where(node => node.IsAetheryte &&
                               node.AethernetGroup == shard.AethernetGroup)
                .Select(main => new GilVendorTravelRoute(
                    main.Id,
                    shard.Id,
                    main.TerritoryId)))
            .Distinct()
            .OrderBy(route => route.AetheryteId)
            .ThenBy(route => route.AethernetId)
            .ToArray();
    }

    internal static void AddShopNpc(
        Dictionary<uint, Dictionary<uint, HashSet<uint>>> shopNpcs,
        uint shopId,
        uint npcId,
        params uint[] requiredQuestIds)
    {
        if (!shopNpcs.TryGetValue(shopId, out var npcs))
            shopNpcs[shopId] = npcs = [];
        if (!npcs.TryGetValue(npcId, out var requirements))
            npcs[npcId] = requirements = [];
        foreach (var questId in requiredQuestIds.Where(id => id != 0))
            requirements.Add(questId);
    }

}

internal sealed record GilVendorAetheryteNode(
    uint Id,
    uint TerritoryId,
    uint AethernetGroup,
    bool IsAetheryte);
