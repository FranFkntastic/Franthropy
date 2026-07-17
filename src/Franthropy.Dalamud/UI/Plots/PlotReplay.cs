using System.Text.Json;

namespace Franthropy.Dalamud.UI.Plots;

public static class PlotDatumValidation
{
    public static void Validate(PlotDatum datum)
    {
        ArgumentNullException.ThrowIfNull(datum);
        ArgumentException.ThrowIfNullOrWhiteSpace(datum.Id);
        if (!double.IsFinite(datum.X) || !double.IsFinite(datum.Y))
            throw new ArgumentOutOfRangeException(nameof(datum), "Plot coordinates must be finite.");
        var duplicate = datum.Attributes
            .GroupBy(attribute => attribute.Key.Value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Datum '{datum.Id}' contains duplicate attribute '{duplicate.Key}'.", nameof(datum));
        foreach (var attribute in datum.Attributes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(attribute.Key.Value);
            if (attribute.Value is PlotNumberAttribute number && !double.IsFinite(number.Value))
                throw new ArgumentOutOfRangeException(nameof(datum), $"Attribute '{attribute.Key}' must be finite.");
        }
    }
}

public sealed record PlotDatumReplay(
    IReadOnlyList<PlotDatum> Data,
    string SchemaVersion = "franthropy-plot-data/v1");

/// <summary>
/// Canonical serialization for semantic plot data. Encodings are deliberately excluded: a
/// tooltip, table, Agent Bridge projection, or future renderer can consume the same attributes.
/// </summary>
public static class PlotDatumReplayJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string Serialize(PlotDatumReplay replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        var wire = new PlotDatumReplayWire(
            replay.SchemaVersion,
            replay.Data
                .Select(ToWire)
                .OrderBy(datum => datum.Id, StringComparer.Ordinal)
                .ToArray());
        return JsonSerializer.Serialize(wire, Options);
    }

    public static PlotDatumReplay Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var wire = JsonSerializer.Deserialize<PlotDatumReplayWire>(json, Options)
            ?? throw new JsonException("Plot datum replay was empty.");
        if (!string.Equals(wire.SchemaVersion, "franthropy-plot-data/v1", StringComparison.Ordinal))
            throw new JsonException($"Unsupported plot datum replay schema '{wire.SchemaVersion}'.");
        return new(
            wire.Data.Select(FromWire).OrderBy(datum => datum.Id, StringComparer.Ordinal).ToArray(),
            wire.SchemaVersion);
    }

    private static PlotDatumWire ToWire(PlotDatum datum)
    {
        PlotDatumValidation.Validate(datum);
        return new(
            datum.Id,
            datum.X,
            datum.Y,
            datum.Attributes
                .OrderBy(attribute => attribute.Key.Value, StringComparer.Ordinal)
                .Select(attribute => attribute.Value switch
                {
                    PlotNumberAttribute number => new PlotAttributeWire(attribute.Key.Value, "number", number.Value, null, null),
                    PlotCategoryAttribute category => new PlotAttributeWire(attribute.Key.Value, "category", null, category.Value, null),
                    PlotBooleanAttribute boolean => new PlotAttributeWire(attribute.Key.Value, "boolean", null, null, boolean.Value),
                    PlotTextAttribute text => new PlotAttributeWire(attribute.Key.Value, "text", null, text.Value, null),
                    _ => throw new JsonException($"Unsupported plot attribute type '{attribute.Value.GetType().Name}'."),
                })
                .ToArray());
    }

    private static PlotDatum FromWire(PlotDatumWire datum)
    {
        var result = new PlotDatum(
            datum.Id,
            datum.X,
            datum.Y,
            datum.Attributes.Select(attribute => new PlotAttribute(
                new(attribute.Key),
                attribute.Kind switch
                {
                    "number" when attribute.Number is { } number => new PlotNumberAttribute(number),
                    "category" when attribute.Text is { } category => new PlotCategoryAttribute(category),
                    "boolean" when attribute.Boolean is { } boolean => new PlotBooleanAttribute(boolean),
                    "text" when attribute.Text is { } text => new PlotTextAttribute(text),
                    _ => throw new JsonException($"Invalid plot attribute '{attribute.Key}' of kind '{attribute.Kind}'."),
                })).ToArray());
        PlotDatumValidation.Validate(result);
        return result;
    }

    private sealed record PlotDatumReplayWire(string SchemaVersion, IReadOnlyList<PlotDatumWire> Data);
    private sealed record PlotDatumWire(string Id, double X, double Y, IReadOnlyList<PlotAttributeWire> Attributes);
    private sealed record PlotAttributeWire(string Key, string Kind, double? Number, string? Text, bool? Boolean);
}
