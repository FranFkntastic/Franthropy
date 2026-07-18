using System.Text.Json;
using Dalamud.Plugin;

namespace Franthropy.Dalamud.Travel;

public sealed record LifestreamObjectInteractionResult(
    bool Success,
    string Code,
    string Message,
    string? AliasJson = null);

/// <summary>
/// Submits a semantic object interaction through Lifestream. The caller owns object identity,
/// lifecycle policy, and rendered-UI confirmation of the interaction's effect.
/// </summary>
public sealed class DalamudLifestreamObjectInteractor
{
    public const string EnqueueChannel = "Lifestream.EnqueueCustomAliasFromString";

    private readonly IDalamudPluginInterface pluginInterface;

    public DalamudLifestreamObjectInteractor(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
    }

    public LifestreamObjectInteractionResult TryEnqueue(
        uint dataId,
        float? approachDistance = null,
        string exportedName = "Franthropy object interaction")
    {
        string aliasJson;
        try
        {
            aliasJson = BuildAliasJson(dataId, approachDistance, exportedName);
        }
        catch (ArgumentException ex)
        {
            return new(false, "InvalidRequest", ex.Message);
        }

        try
        {
            pluginInterface
                .GetIpcSubscriber<string, bool, int?, int?, object>(EnqueueChannel)
                .InvokeAction(aliasJson, true, null, null);
            return new(true, "Submitted", "Lifestream accepted the semantic object interaction.", aliasJson);
        }
        catch (Exception ex)
        {
            return new(false, "IpcFailure", ex.Message, aliasJson);
        }
    }

    public static string BuildAliasJson(
        uint dataId,
        float? approachDistance = null,
        string exportedName = "Franthropy object interaction")
    {
        if (dataId == 0)
            throw new ArgumentOutOfRangeException(nameof(dataId), "A non-zero game-data row ID is required.");
        if (approachDistance is <= 0 || float.IsNaN(approachDistance ?? 0) || float.IsInfinity(approachDistance ?? 0))
            throw new ArgumentOutOfRangeException(nameof(approachDistance), "Approach distance must be a finite positive value when supplied.");
        if (string.IsNullOrWhiteSpace(exportedName))
            throw new ArgumentException("An exported action name is required.", nameof(exportedName));

        var command = new Dictionary<string, object>
        {
            ["Kind"] = 6,
            ["DataID"] = dataId,
        };
        if (approachDistance is { } distance)
            command["InteractDistance"] = distance;

        return JsonSerializer.Serialize(new
        {
            ExportedName = exportedName.Trim(),
            Commands = new[] { command },
        });
    }
}
