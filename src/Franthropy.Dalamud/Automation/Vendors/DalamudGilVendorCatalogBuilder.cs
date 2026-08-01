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

        var gilShopIds = dataManager.GetExcelSheet<GilShop>()
            .Select(row => row.RowId)
            .ToHashSet();
        var npcNames = dataManager.GetExcelSheet<ENpcResident>()
            .Where(row => row.RowId != 0)
            .ToDictionary(row => row.RowId, row => row.Singular.ToString());
        var shopNpcs = new Dictionary<uint, List<uint>>();
        foreach (var npc in dataManager.GetExcelSheet<ENpcBase>())
        {
            foreach (var data in npc.ENpcData)
            {
                if (data.Is<GilShop>() && gilShopIds.Contains(data.RowId))
                {
                    AddShopNpc(shopNpcs, data.RowId, npc.RowId);
                    continue;
                }
                if (data.Is<PreHandler>() &&
                    data.TryGetValue(out PreHandler preHandler) &&
                    preHandler.Target.Is<GilShop>() &&
                    gilShopIds.Contains(preHandler.Target.RowId))
                {
                    AddShopNpc(shopNpcs, preHandler.Target.RowId, npc.RowId);
                    continue;
                }
                if (!data.Is<TopicSelect>() || !data.TryGetValue(out TopicSelect topic))
                    continue;
                foreach (var shop in topic.Shop.Where(value => value.Is<GilShop>() && gilShopIds.Contains(value.RowId)))
                    AddShopNpc(shopNpcs, shop.RowId, npc.RowId);
            }
        }

        var locations = DalamudVendorLocationCatalogBuilder.Build(
            dataManager,
            shopNpcs.Values.SelectMany(npcs => npcs).ToHashSet());
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

        var offers = new List<GilVendorOffer>();
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

                foreach (var npcId in npcs)
                {
                    if (!npcNames.TryGetValue(npcId, out var npcName) ||
                        string.IsNullOrWhiteSpace(npcName) ||
                        !locations.TryGetValue(npcId, out var npcLocations))
                    {
                        continue;
                    }

                    foreach (var location in npcLocations)
                    {
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
                        });
                    }
                }

                shopRowIndex++;
            }
        }

        return GilVendorCatalog.Create(offers);
    }

    internal static IReadOnlyList<GilVendorTravelRoute> ResolveTravelRoutes(
        uint targetTerritoryId,
        IReadOnlyList<GilVendorAetheryteNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var direct = nodes
            .Where(node => node.IsAetheryte && node.TerritoryId == targetTerritoryId)
            .Select(node => new GilVendorTravelRoute(node.Id))
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
                .Select(main => new GilVendorTravelRoute(main.Id, shard.Id)))
            .Distinct()
            .OrderBy(route => route.AetheryteId)
            .ThenBy(route => route.AethernetId)
            .ToArray();
    }

    private static void AddShopNpc(Dictionary<uint, List<uint>> shopNpcs, uint shopId, uint npcId)
    {
        if (!shopNpcs.TryGetValue(shopId, out var npcs))
            shopNpcs[shopId] = npcs = [];
        if (!npcs.Contains(npcId))
            npcs.Add(npcId);
    }

}

internal sealed record GilVendorAetheryteNode(
    uint Id,
    uint TerritoryId,
    uint AethernetGroup,
    bool IsAetheryte);
