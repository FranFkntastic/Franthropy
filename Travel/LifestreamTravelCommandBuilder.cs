using Franthropy.Dalamud.Worlds;

namespace Franthropy.Dalamud.Travel;

public sealed class LifestreamTravelCommandBuilder
{
    private readonly WorldCatalog worldCatalog;

    public LifestreamTravelCommandBuilder(WorldCatalog worldCatalog)
    {
        this.worldCatalog = worldCatalog;
    }

    public WorldTravelCommandResult TryBuildMarketBoardTravel(
        string targetWorldName,
        string currentWorldName,
        out WorldTravelRequest? request)
    {
        request = null;

        if (string.IsNullOrWhiteSpace(targetWorldName))
            return WorldTravelCommandResult.Fail("Target world is required.");

        if (!worldCatalog.TryGetWorld(targetWorldName, out var targetWorld))
            return WorldTravelCommandResult.Fail($"Unknown world: {targetWorldName.Trim()}.");

        if (string.IsNullOrWhiteSpace(currentWorldName))
            return WorldTravelCommandResult.Fail("Current world is unavailable.");

        var currentWorld = currentWorldName.Trim();
        var isCurrentWorld = string.Equals(targetWorld.Name, currentWorld, StringComparison.OrdinalIgnoreCase);
        var command = isCurrentWorld
            ? "/li mb"
            : $"/li {targetWorld.Name} mb";

        request = new WorldTravelRequest(
            targetWorld.Name,
            currentWorld,
            command,
            isCurrentWorld);

        return WorldTravelCommandResult.Ok($"Prepared travel command {command}.", command);
    }
}
