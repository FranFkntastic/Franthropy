namespace Franthropy.Dalamud.Worlds;

public sealed class WorldCatalog
{
    private readonly Dictionary<string, WorldInfo> worlds;

    public WorldCatalog(IEnumerable<WorldInfo> worldInfos)
    {
        worlds = worldInfos
            .Where(world => !string.IsNullOrWhiteSpace(world.Name))
            .GroupBy(world => world.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGetWorld(string worldName, out WorldInfo world)
    {
        world = null!;
        if (string.IsNullOrWhiteSpace(worldName))
            return false;

        return worlds.TryGetValue(worldName.Trim(), out world!);
    }
}

public sealed record WorldInfo(string Name, string DataCenter, string Region, uint RowId);
