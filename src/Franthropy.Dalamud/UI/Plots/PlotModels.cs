using System.Numerics;

namespace Franthropy.Dalamud.UI.Plots;

public readonly record struct PlotColor(float Red, float Green, float Blue, float Alpha = 1f)
{
    public PlotColor WithAlpha(float alpha) => this with { Alpha = Math.Clamp(alpha, 0f, 1f) };
}

public readonly record struct PlotRange(double Minimum, double Maximum)
{
    public double Length => Maximum - Minimum;

    public PlotRange Normalize()
    {
        if (!double.IsFinite(Minimum) || !double.IsFinite(Maximum))
            throw new ArgumentOutOfRangeException(nameof(PlotRange), "Plot ranges must be finite.");
        if (Minimum > Maximum)
            return new(Maximum, Minimum);
        if (Minimum < Maximum)
            return this;
        var padding = Math.Max(1d, Math.Abs(Minimum) * .05d);
        return new(Minimum - padding, Maximum + padding);
    }
}

public readonly record struct PlotRect(Vector2 Minimum, Vector2 Maximum)
{
    public float Width => Maximum.X - Minimum.X;
    public float Height => Maximum.Y - Minimum.Y;
    public bool Contains(Vector2 point) =>
        point.X >= Minimum.X && point.X <= Maximum.X &&
        point.Y >= Minimum.Y && point.Y <= Maximum.Y;
}

public sealed record PlotAxis(
    string Label,
    string? Unit = null,
    int DesiredTickCount = 6,
    Func<double, string>? Format = null);

/// <summary>
/// Requests a truthful discontinuity when the leading portion of an axis contains no plotted
/// evidence. The compiler resolves the actual break from the current layers, so overlays and
/// filtered plots cannot retain a stale, misleading discontinuity.
/// </summary>
public sealed record PlotAxisBreakPolicy(
    double MinimumEmptyFraction = .40d,
    double LeadingContextFraction = .04d,
    double DataPaddingFraction = .04d,
    float GapPixels = 14f);

public sealed record PlotAttributeKey(string Value)
{
    public override string ToString() => Value;
}

public abstract record PlotAttributeValue;
public sealed record PlotNumberAttribute(double Value) : PlotAttributeValue;
public sealed record PlotCategoryAttribute(string Value) : PlotAttributeValue;
public sealed record PlotBooleanAttribute(bool Value) : PlotAttributeValue;
public sealed record PlotTextAttribute(string Value) : PlotAttributeValue;

public sealed record PlotAttribute(PlotAttributeKey Key, PlotAttributeValue Value);

public sealed record PlotDatum(
    string Id,
    double X,
    double Y,
    IReadOnlyList<PlotAttribute> Attributes)
{
    public PlotAttributeValue? GetAttribute(PlotAttributeKey key) => Attributes
        .FirstOrDefault(attribute => attribute.Key == key)?.Value;
}

public enum PlotPointShape
{
    Circle,
    Square,
    Diamond,
    Triangle,
}

[Flags]
public enum PlotPointRole
{
    None = 0,
    Selected = 1,
    Nominated = 2,
    Warning = 4,
    Failure = 8,
}

public sealed record PlotCategoryColorRule(string Category, PlotColor Color);
public sealed record PlotCategoryShapeRule(string Category, PlotPointShape Shape);

public sealed record PlotColorEncoding(
    PlotAttributeKey Attribute,
    IReadOnlyList<PlotCategoryColorRule> Rules,
    PlotColor Fallback);

public sealed record PlotShapeEncoding(
    PlotAttributeKey Attribute,
    IReadOnlyList<PlotCategoryShapeRule> Rules,
    PlotPointShape Fallback = PlotPointShape.Circle);

public sealed record PlotSizeEncoding(
    PlotAttributeKey Attribute,
    PlotRange Input,
    float MinimumPixels,
    float MaximumPixels);

public sealed record PlotOpacityEncoding(
    PlotAttributeKey Attribute,
    PlotRange Input,
    float MinimumOpacity = .25f,
    float MaximumOpacity = 1f);

public sealed record PlotLabelEncoding(
    PlotAttributeKey Attribute,
    Func<PlotAttributeValue, string>? Format = null);

/// <summary>
/// Each visual channel has exactly one optional owner. Interaction roles are resolved later as
/// orthogonal overlays, so selection cannot erase the encoded quality, burden, or confidence.
/// </summary>
public sealed record PlotEncodingSet(
    PlotColorEncoding? Color = null,
    PlotShapeEncoding? Shape = null,
    PlotSizeEncoding? Size = null,
    PlotOpacityEncoding? Opacity = null,
    PlotLabelEncoding? Label = null);

public sealed record PlotPointStyle(
    PlotColor Color,
    PlotPointShape Shape = PlotPointShape.Circle,
    float RadiusPixels = 5f,
    float Opacity = 1f,
    bool ShowLabel = false);

public sealed record PlotPointVisual(
    PlotColor Color,
    PlotPointShape Shape,
    float RadiusPixels,
    float Opacity,
    string? Label,
    PlotPointRole Role);

public sealed record PlotLineStyle(PlotColor Color, float Thickness = 1.5f);
public sealed record PlotBandStyle(PlotColor Color);

public interface IPlotLayer
{
    string Id { get; }
    int ZIndex { get; }
}

public sealed record PlotPointLayer(
    string Id,
    IReadOnlyList<PlotDatum> Data,
    PlotPointStyle Style,
    PlotEncodingSet Encodings,
    int ZIndex = 20) : IPlotLayer;

public sealed record PlotPolylineLayer(
    string Id,
    IReadOnlyList<PlotDatum> Data,
    PlotLineStyle Style,
    int ZIndex = 10) : IPlotLayer;

public sealed record PlotStepLayer(
    string Id,
    IReadOnlyList<PlotDatum> Data,
    PlotLineStyle Style,
    bool StepAfter = true,
    int ZIndex = 10) : IPlotLayer;

public enum PlotRuleOrientation { Horizontal, Vertical }

public sealed record PlotRuleLayer(
    string Id,
    PlotRuleOrientation Orientation,
    double Value,
    PlotLineStyle Style,
    string? Label = null,
    int ZIndex = 5) : IPlotLayer;

public sealed record PlotBandLayer(
    string Id,
    PlotRuleOrientation Orientation,
    double Minimum,
    double Maximum,
    PlotBandStyle Style,
    string? Label = null,
    int ZIndex = 0) : IPlotLayer;

public sealed record PlotAnnotationLayer(
    string Id,
    double X,
    double Y,
    string Text,
    PlotColor Color,
    Vector2 PixelOffset,
    int ZIndex = 30) : IPlotLayer;

public sealed record PlotSpec(
    string Id,
    PlotRange XDomain,
    PlotRange YDomain,
    PlotAxis XAxis,
    PlotAxis YAxis,
    IReadOnlyList<IPlotLayer> Layers,
    string? Title = null,
    bool ShowGrid = true,
    PlotAxisBreakPolicy? XAxisBreak = null);

public sealed record PlotInteractionState(
    IReadOnlySet<string> SelectedDatumIds,
    string? NominatedDatumId,
    IReadOnlySet<string> WarningDatumIds,
    IReadOnlySet<string> FailureDatumIds)
{
    public static PlotInteractionState Empty { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        null,
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));

    public PlotPointRole ResolveRole(string datumId)
    {
        var role = PlotPointRole.None;
        if (SelectedDatumIds.Contains(datumId)) role |= PlotPointRole.Selected;
        if (string.Equals(NominatedDatumId, datumId, StringComparison.Ordinal)) role |= PlotPointRole.Nominated;
        if (WarningDatumIds.Contains(datumId)) role |= PlotPointRole.Warning;
        if (FailureDatumIds.Contains(datumId)) role |= PlotPointRole.Failure;
        return role;
    }
}
