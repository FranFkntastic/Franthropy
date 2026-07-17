using System.Numerics;

namespace Franthropy.Dalamud.UI.Plots;

public class LinearPlotScale
{
    public LinearPlotScale(PlotRange domain, double pixelMinimum, double pixelMaximum)
    {
        Domain = domain.Normalize();
        PixelMinimum = pixelMinimum;
        PixelMaximum = pixelMaximum;
    }

    public PlotRange Domain { get; }
    public double PixelMinimum { get; }
    public double PixelMaximum { get; }

    public virtual IReadOnlyList<PlotRange> VisibleDomainRanges => [Domain];
    public virtual IReadOnlyList<PlotRange> VisiblePixelRanges =>
        [new(Math.Min(PixelMinimum, PixelMaximum), Math.Max(PixelMinimum, PixelMaximum))];

    public virtual bool IsValueVisible(double value) => value >= Domain.Minimum && value <= Domain.Maximum;

    public virtual float Map(double value)
    {
        var ratio = (value - Domain.Minimum) / Domain.Length;
        return checked((float)(PixelMinimum + ratio * (PixelMaximum - PixelMinimum)));
    }

    public virtual double Invert(float pixel)
    {
        var ratio = (pixel - PixelMinimum) / (PixelMaximum - PixelMinimum);
        return Domain.Minimum + ratio * Domain.Length;
    }
}

public sealed class BrokenLinearPlotScale : LinearPlotScale
{
    private readonly double direction;
    private readonly double firstPixels;
    private readonly double secondPixels;
    private readonly double firstPixelEnd;
    private readonly double secondPixelStart;

    public BrokenLinearPlotScale(
        PlotRange domain,
        double pixelMinimum,
        double pixelMaximum,
        PlotRange omittedDomain,
        float gapPixels)
        : base(domain, pixelMinimum, pixelMaximum)
    {
        OmittedDomain = omittedDomain.Normalize();
        if (OmittedDomain.Minimum <= Domain.Minimum || OmittedDomain.Maximum >= Domain.Maximum)
            throw new ArgumentOutOfRangeException(nameof(omittedDomain), "A scale break must be strictly inside the domain.");
        if (gapPixels < 4f || !float.IsFinite(gapPixels))
            throw new ArgumentOutOfRangeException(nameof(gapPixels), "A scale break must reserve at least four finite pixels.");

        var pixelLength = Math.Abs(pixelMaximum - pixelMinimum);
        if (gapPixels >= pixelLength)
            throw new ArgumentOutOfRangeException(nameof(gapPixels), "A scale break cannot consume the whole pixel range.");
        direction = Math.Sign(pixelMaximum - pixelMinimum);
        GapPixels = gapPixels;
        var firstDomainLength = OmittedDomain.Minimum - Domain.Minimum;
        var secondDomainLength = Domain.Maximum - OmittedDomain.Maximum;
        var visibleDomainLength = firstDomainLength + secondDomainLength;
        var availablePixels = pixelLength - gapPixels;
        firstPixels = availablePixels * firstDomainLength / visibleDomainLength;
        secondPixels = availablePixels - firstPixels;
        firstPixelEnd = pixelMinimum + direction * firstPixels;
        secondPixelStart = firstPixelEnd + direction * gapPixels;
    }

    public PlotRange OmittedDomain { get; }
    public float GapPixels { get; }
    public double BreakPixelMinimum => Math.Min(firstPixelEnd, secondPixelStart);
    public double BreakPixelMaximum => Math.Max(firstPixelEnd, secondPixelStart);
    public override IReadOnlyList<PlotRange> VisibleDomainRanges =>
        [new(Domain.Minimum, OmittedDomain.Minimum), new(OmittedDomain.Maximum, Domain.Maximum)];
    public override IReadOnlyList<PlotRange> VisiblePixelRanges =>
        [
            new(Math.Min(PixelMinimum, firstPixelEnd), Math.Max(PixelMinimum, firstPixelEnd)),
            new(Math.Min(secondPixelStart, PixelMaximum), Math.Max(secondPixelStart, PixelMaximum)),
        ];

    public override bool IsValueVisible(double value) =>
        base.IsValueVisible(value) && (value <= OmittedDomain.Minimum || value >= OmittedDomain.Maximum);

    public override float Map(double value)
    {
        if (value <= OmittedDomain.Minimum)
        {
            var ratio = (value - Domain.Minimum) / (OmittedDomain.Minimum - Domain.Minimum);
            return checked((float)(PixelMinimum + direction * ratio * firstPixels));
        }
        if (value >= OmittedDomain.Maximum)
        {
            var ratio = (value - OmittedDomain.Maximum) / (Domain.Maximum - OmittedDomain.Maximum);
            return checked((float)(secondPixelStart + direction * ratio * secondPixels));
        }

        var omittedRatio = (value - OmittedDomain.Minimum) / OmittedDomain.Length;
        return checked((float)(firstPixelEnd + direction * omittedRatio * GapPixels));
    }

    public override double Invert(float pixel)
    {
        var directedDistance = (pixel - PixelMinimum) * direction;
        if (directedDistance <= firstPixels)
            return Domain.Minimum + directedDistance / firstPixels * (OmittedDomain.Minimum - Domain.Minimum);
        if (directedDistance >= firstPixels + GapPixels)
            return OmittedDomain.Maximum + (directedDistance - firstPixels - GapPixels) / secondPixels * (Domain.Maximum - OmittedDomain.Maximum);
        return directedDistance - firstPixels < GapPixels / 2d ? OmittedDomain.Minimum : OmittedDomain.Maximum;
    }
}

public static class PlotTicks
{
    public static IReadOnlyList<double> Create(PlotRange range, int desiredCount)
    {
        range = range.Normalize();
        desiredCount = Math.Clamp(desiredCount, 2, 20);
        var roughStep = range.Length / (desiredCount - 1);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(roughStep)));
        var normalized = roughStep / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        var step = nice * magnitude;
        var first = Math.Ceiling(range.Minimum / step) * step;
        var values = new List<double>();
        for (var value = first; value <= range.Maximum + step * 1e-9 && values.Count < 100; value += step)
            values.Add(Math.Abs(value) < step * 1e-12 ? 0 : value);
        return values;
    }
}

public static class PlotVisualResolver
{
    public static PlotPointVisual Resolve(
        PlotDatum datum,
        PlotPointStyle fallback,
        PlotEncodingSet encodings,
        PlotPointRole role)
    {
        var color = ResolveColor(datum, encodings.Color) ?? fallback.Color;
        var shape = ResolveShape(datum, encodings.Shape) ?? fallback.Shape;
        var radius = ResolveNumber(datum, encodings.Size?.Attribute) is { } sizeValue && encodings.Size is { } size
            ? Interpolate(sizeValue, size.Input, size.MinimumPixels, size.MaximumPixels)
            : fallback.RadiusPixels;
        var resolvedOpacity = ResolveNumber(datum, encodings.Opacity?.Attribute) is { } opacityValue && encodings.Opacity is { } opacityEncoding
            ? Interpolate(opacityValue, opacityEncoding.Input, opacityEncoding.MinimumOpacity, opacityEncoding.MaximumOpacity)
            : fallback.Opacity;
        var label = encodings.Label is { } labelEncoding && datum.GetAttribute(labelEncoding.Attribute) is { } value
            ? labelEncoding.Format?.Invoke(value) ?? Format(value)
            : fallback.ShowLabel ? datum.Id : null;
        return new(color, shape, Math.Max(1f, radius), Math.Clamp(resolvedOpacity, 0f, 1f), label, role);
    }

    private static PlotColor? ResolveColor(PlotDatum datum, PlotColorEncoding? encoding)
    {
        if (encoding is null || datum.GetAttribute(encoding.Attribute) is not PlotCategoryAttribute category)
            return null;
        return encoding.Rules.FirstOrDefault(rule => string.Equals(rule.Category, category.Value, StringComparison.Ordinal))?.Color
            ?? encoding.Fallback;
    }

    private static PlotPointShape? ResolveShape(PlotDatum datum, PlotShapeEncoding? encoding)
    {
        if (encoding is null || datum.GetAttribute(encoding.Attribute) is not PlotCategoryAttribute category)
            return null;
        return encoding.Rules.FirstOrDefault(rule => string.Equals(rule.Category, category.Value, StringComparison.Ordinal))?.Shape
            ?? encoding.Fallback;
    }

    private static double? ResolveNumber(PlotDatum datum, PlotAttributeKey? key) => key is not null &&
        datum.GetAttribute(key) is PlotNumberAttribute number ? number.Value : null;

    private static float Interpolate(double value, PlotRange input, float outputMinimum, float outputMaximum)
    {
        input = input.Normalize();
        var ratio = Math.Clamp((value - input.Minimum) / input.Length, 0, 1);
        return checked((float)(outputMinimum + ratio * (outputMaximum - outputMinimum)));
    }

    private static string Format(PlotAttributeValue value) => value switch
    {
        PlotNumberAttribute number => number.Value.ToString("0.##"),
        PlotCategoryAttribute category => category.Value,
        PlotBooleanAttribute boolean => boolean.Value ? "Yes" : "No",
        PlotTextAttribute text => text.Value,
        _ => string.Empty,
    };
}

public sealed record PlotHitTarget(string DatumId, Vector2 Position, float RadiusPixels, int TraversalIndex);

public static class PlotHitTesting
{
    public static PlotHitTarget? FindNearest(
        IReadOnlyList<PlotHitTarget> targets,
        Vector2 pointer,
        float additionalTolerance = 3f)
    {
        return targets
            .Select(target => new
            {
                Target = target,
                Distance = Vector2.Distance(target.Position, pointer),
            })
            .Where(candidate => candidate.Distance <= candidate.Target.RadiusPixels + additionalTolerance)
            .OrderBy(candidate => candidate.Distance)
            .ThenByDescending(candidate => candidate.Target.TraversalIndex)
            .Select(candidate => candidate.Target)
            .FirstOrDefault();
    }

    public static string? Traverse(IReadOnlyList<PlotHitTarget> targets, string? currentId, int direction)
    {
        if (targets.Count == 0 || direction == 0)
            return currentId;
        var ordered = targets.OrderBy(target => target.TraversalIndex).ToArray();
        var current = Array.FindIndex(ordered, target => string.Equals(target.DatumId, currentId, StringComparison.Ordinal));
        var next = current < 0
            ? direction > 0 ? 0 : ordered.Length - 1
            : (current + Math.Sign(direction) + ordered.Length) % ordered.Length;
        return ordered[next].DatumId;
    }
}

public static class PlotClipping
{
    public static bool TryClipLine(PlotRect rect, Vector2 start, Vector2 end, out Vector2 clippedStart, out Vector2 clippedEnd)
    {
        const int left = 1;
        const int right = 2;
        const int top = 4;
        const int bottom = 8;
        clippedStart = start;
        clippedEnd = end;

        int Code(Vector2 point)
        {
            var code = 0;
            if (point.X < rect.Minimum.X) code |= left;
            else if (point.X > rect.Maximum.X) code |= right;
            if (point.Y < rect.Minimum.Y) code |= top;
            else if (point.Y > rect.Maximum.Y) code |= bottom;
            return code;
        }

        while (true)
        {
            var startCode = Code(clippedStart);
            var endCode = Code(clippedEnd);
            if ((startCode | endCode) == 0)
                return true;
            if ((startCode & endCode) != 0)
                return false;
            var outside = startCode != 0 ? startCode : endCode;
            float x;
            float y;
            if ((outside & top) != 0)
            {
                x = clippedStart.X + (clippedEnd.X - clippedStart.X) * (rect.Minimum.Y - clippedStart.Y) / (clippedEnd.Y - clippedStart.Y);
                y = rect.Minimum.Y;
            }
            else if ((outside & bottom) != 0)
            {
                x = clippedStart.X + (clippedEnd.X - clippedStart.X) * (rect.Maximum.Y - clippedStart.Y) / (clippedEnd.Y - clippedStart.Y);
                y = rect.Maximum.Y;
            }
            else if ((outside & right) != 0)
            {
                y = clippedStart.Y + (clippedEnd.Y - clippedStart.Y) * (rect.Maximum.X - clippedStart.X) / (clippedEnd.X - clippedStart.X);
                x = rect.Maximum.X;
            }
            else
            {
                y = clippedStart.Y + (clippedEnd.Y - clippedStart.Y) * (rect.Minimum.X - clippedStart.X) / (clippedEnd.X - clippedStart.X);
                x = rect.Minimum.X;
            }

            if (outside == startCode) clippedStart = new(x, y);
            else clippedEnd = new(x, y);
        }
    }
}
