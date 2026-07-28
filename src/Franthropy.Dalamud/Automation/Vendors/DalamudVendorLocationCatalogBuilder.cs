using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;
using System.Numerics;

namespace Franthropy.Dalamud.Automation.Vendors;

public sealed record DalamudVendorLocation(
    uint NpcId,
    uint TerritoryId,
    Vector3 Position,
    DalamudVendorLocationSource Source);

public enum DalamudVendorLocationSource
{
    Level,
    PlaneventLgb,
}

/// <summary>
/// Resolves static and dynamically placed event-NPC locations from game data.
/// </summary>
public static class DalamudVendorLocationCatalogBuilder
{
    public static IReadOnlyDictionary<uint, IReadOnlyList<DalamudVendorLocation>> Build(
        IDataManager dataManager,
        IReadOnlySet<uint> npcIds)
    {
        ArgumentNullException.ThrowIfNull(dataManager);
        ArgumentNullException.ThrowIfNull(npcIds);

        var locations = new Dictionary<uint, List<DalamudVendorLocation>>();
        foreach (var level in dataManager.GetExcelSheet<Level>())
        {
            var npcId = level.Object.RowId;
            if (npcId == 0 || level.Territory.RowId == 0 || !npcIds.Contains(npcId))
                continue;
            AddLocation(
                locations,
                new(
                    npcId,
                    level.Territory.RowId,
                    new((float)level.X, (float)level.Y, (float)level.Z),
                    DalamudVendorLocationSource.Level));
        }

        var unresolvedNpcIds = FindUnresolvedNpcIds(npcIds, locations.Keys);
        if (unresolvedNpcIds.Count > 0)
            AddPlaneventLocations(dataManager, unresolvedNpcIds, locations);

        return locations.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<DalamudVendorLocation>)pair.Value
                .OrderBy(location => location.Source)
                .ThenBy(location => location.TerritoryId)
                .ThenBy(location => location.Position.X)
                .ThenBy(location => location.Position.Y)
                .ThenBy(location => location.Position.Z)
                .ToArray());
    }

    internal static IReadOnlySet<uint> FindUnresolvedNpcIds(
        IReadOnlySet<uint> requestedNpcIds,
        IEnumerable<uint> resolvedNpcIds)
    {
        ArgumentNullException.ThrowIfNull(requestedNpcIds);
        ArgumentNullException.ThrowIfNull(resolvedNpcIds);
        var resolved = resolvedNpcIds.ToHashSet();
        return requestedNpcIds.Where(npcId => !resolved.Contains(npcId)).ToHashSet();
    }

    internal static bool TryBuildPlaneventPath(string backgroundPath, out string planeventPath)
    {
        var levelIndex = backgroundPath.IndexOf("/level/", StringComparison.Ordinal);
        if (levelIndex < 0)
        {
            planeventPath = string.Empty;
            return false;
        }

        planeventPath = $"bg/{backgroundPath[..(levelIndex + 1)]}level/planevent.lgb";
        return true;
    }

    private static void AddPlaneventLocations(
        IDataManager dataManager,
        IReadOnlySet<uint> unresolvedNpcIds,
        Dictionary<uint, List<DalamudVendorLocation>> locations)
    {
        foreach (var territory in dataManager.GetExcelSheet<TerritoryType>())
        {
            if (!TryBuildPlaneventPath(territory.Bg.ToString(), out var path))
                continue;

            LgbFile? file;
            try
            {
                file = dataManager.GetFile<LgbFile>(path);
            }
            catch
            {
                continue;
            }
            if (file is null)
                continue;

            foreach (var layer in file.Layers)
            {
                foreach (var instance in layer.InstanceObjects)
                {
                    if (instance.AssetType != LayerEntryType.EventNPC)
                        continue;

                    var npcId = ((LayerCommon.ENPCInstanceObject)instance.Object)
                        .ParentData
                        .ParentData
                        .BaseId;
                    if (!unresolvedNpcIds.Contains(npcId))
                        continue;

                    AddLocation(
                        locations,
                        new(
                            npcId,
                            territory.RowId,
                            new(
                                instance.Transform.Translation.X,
                                instance.Transform.Translation.Y,
                                instance.Transform.Translation.Z),
                            DalamudVendorLocationSource.PlaneventLgb));
                }
            }
        }
    }

    private static void AddLocation(
        Dictionary<uint, List<DalamudVendorLocation>> locations,
        DalamudVendorLocation location)
    {
        if (!locations.TryGetValue(location.NpcId, out var npcLocations))
            locations[location.NpcId] = npcLocations = [];
        if (!npcLocations.Contains(location))
            npcLocations.Add(location);
    }
}
