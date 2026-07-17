namespace Franthropy.Dalamud.UI.Plots;

public sealed record PlotOverlayStyle(
    PlotColor? LineColor = null,
    PlotPointShape? PointShape = null);

public sealed record PlotOverlaySeries(
    string Id,
    PlotSpec Spec,
    PlotOverlayStyle? Style = null);

public sealed record PlotOverlayDatumIdentity(
    string SeriesId,
    string SourceDatumId);

public sealed record PlotOverlayResult(
    PlotSpec Spec,
    IReadOnlyDictionary<string, PlotOverlayDatumIdentity> DatumIdentities);

/// <summary>
/// Places independently-built plots into one shared coordinate system. Series keep their own
/// layers and encodings; the composer only owns axis compatibility, domain union, semantic-id
/// namespacing, and optional series-level line/shape identity.
/// </summary>
public static class PlotOverlayComposer
{
    public static PlotAttributeKey SeriesAttribute { get; } = new("plot.overlaySeries");

    public static PlotOverlayResult Compose(
        string plotId,
        IReadOnlyList<PlotOverlaySeries> series,
        string? title = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plotId);
        ArgumentNullException.ThrowIfNull(series);
        if (series.Count == 0)
            throw new ArgumentException("An overlay requires at least one plot series.", nameof(series));

        var first = series[0];
        ValidateSeries(first, nameof(series));
        var seriesIds = new HashSet<string>(StringComparer.Ordinal);
        var layers = new List<IPlotLayer>();
        var identities = new Dictionary<string, PlotOverlayDatumIdentity>(StringComparer.Ordinal);
        var xMinimum = double.PositiveInfinity;
        var xMaximum = double.NegativeInfinity;
        var yMinimum = double.PositiveInfinity;
        var yMaximum = double.NegativeInfinity;

        foreach (var value in series)
        {
            ValidateSeries(value, nameof(series));
            if (!seriesIds.Add(value.Id))
                throw new ArgumentException($"Overlay series id '{value.Id}' is duplicated.", nameof(series));
            if (!AxesMatch(first.Spec.XAxis, value.Spec.XAxis) || !AxesMatch(first.Spec.YAxis, value.Spec.YAxis))
                throw new ArgumentException($"Overlay series '{value.Id}' does not share the same X and Y axes.", nameof(series));

            var xDomain = value.Spec.XDomain.Normalize();
            var yDomain = value.Spec.YDomain.Normalize();
            xMinimum = Math.Min(xMinimum, xDomain.Minimum);
            xMaximum = Math.Max(xMaximum, xDomain.Maximum);
            yMinimum = Math.Min(yMinimum, yDomain.Minimum);
            yMaximum = Math.Max(yMaximum, yDomain.Maximum);
            foreach (var layer in value.Spec.Layers)
                layers.Add(RewriteLayer(value, layer, identities));
        }

        var spec = new PlotSpec(
            plotId,
            new PlotRange(xMinimum, xMaximum).Normalize(),
            new PlotRange(yMinimum, yMaximum).Normalize(),
            first.Spec.XAxis,
            first.Spec.YAxis,
            layers,
            title ?? first.Spec.Title,
            series.Any(value => value.Spec.ShowGrid),
            series.Select(value => value.Spec.XAxisBreak).FirstOrDefault(value => value is not null));
        return new(spec, identities);
    }

    public static string DatumId(string seriesId, string sourceDatumId) =>
        $"{seriesId}/{sourceDatumId}";

    private static IPlotLayer RewriteLayer(
        PlotOverlaySeries series,
        IPlotLayer layer,
        IDictionary<string, PlotOverlayDatumIdentity> identities)
    {
        var layerId = $"{series.Id}/{layer.Id}";
        var style = series.Style;
        return layer switch
        {
            PlotPointLayer points => points with
            {
                Id = layerId,
                Data = RewriteData(series.Id, points.Data, identities),
                Style = style?.PointShape is { } shape ? points.Style with { Shape = shape } : points.Style,
                Encodings = style?.PointShape is not null ? points.Encodings with { Shape = null } : points.Encodings,
            },
            PlotPolylineLayer polyline => polyline with
            {
                Id = layerId,
                Data = RewriteData(series.Id, polyline.Data, identities),
                Style = style?.LineColor is { } color ? polyline.Style with { Color = color } : polyline.Style,
            },
            PlotStepLayer step => step with
            {
                Id = layerId,
                Data = RewriteData(series.Id, step.Data, identities),
                Style = style?.LineColor is { } color ? step.Style with { Color = color } : step.Style,
            },
            PlotRuleLayer rule => rule with
            {
                Id = layerId,
                Style = style?.LineColor is { } color ? rule.Style with { Color = color } : rule.Style,
            },
            PlotBandLayer band => band with { Id = layerId },
            PlotAnnotationLayer annotation => annotation with { Id = layerId },
            _ => throw new ArgumentOutOfRangeException(nameof(layer), layer.GetType().Name, "Unknown plot layer type."),
        };
    }

    private static IReadOnlyList<PlotDatum> RewriteData(
        string seriesId,
        IReadOnlyList<PlotDatum> data,
        IDictionary<string, PlotOverlayDatumIdentity> identities)
    {
        return data.Select(datum =>
        {
            var id = DatumId(seriesId, datum.Id);
            identities.TryAdd(id, new(seriesId, datum.Id));
            return datum with
            {
                Id = id,
                Attributes = datum.Attributes
                    .Where(attribute => attribute.Key != SeriesAttribute)
                    .Append(new PlotAttribute(SeriesAttribute, new PlotCategoryAttribute(seriesId)))
                    .ToArray(),
            };
        }).ToArray();
    }

    private static bool AxesMatch(PlotAxis left, PlotAxis right) =>
        string.Equals(left.Label, right.Label, StringComparison.Ordinal) &&
        string.Equals(left.Unit, right.Unit, StringComparison.Ordinal);

    private static void ValidateSeries(PlotOverlaySeries series, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentException.ThrowIfNullOrWhiteSpace(series.Id);
        if (series.Id.Contains('/'))
            throw new ArgumentException("Overlay series ids cannot contain '/'.", parameterName);
        ArgumentNullException.ThrowIfNull(series.Spec);
    }
}
