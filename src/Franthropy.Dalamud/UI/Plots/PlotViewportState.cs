namespace Franthropy.Dalamud.UI.Plots;

/// <summary>
/// Product-neutral viewport state for plots. It owns only the visible domains; renderers remain
/// free to provide mouse, touch, keyboard, or explicit-control affordances over the same model.
/// </summary>
public sealed class PlotViewportState
{
    private PlotRange? xDomain;
    private PlotRange? yDomain;

    public bool IsFit => xDomain is null && yDomain is null;

    public PlotSpec Apply(PlotSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec with
        {
            XDomain = xDomain is { } x ? Clamp(x, spec.XDomain) : spec.XDomain,
            YDomain = yDomain is { } y ? Clamp(y, spec.YDomain) : spec.YDomain,
        };
    }

    public void Fit()
    {
        xDomain = null;
        yDomain = null;
    }

    public void Zoom(
        PlotSpec spec,
        double centerX,
        double centerY,
        double factor,
        bool zoomX = true,
        bool zoomY = true)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!double.IsFinite(factor) || factor <= 0)
            throw new ArgumentOutOfRangeException(nameof(factor));
        var current = Apply(spec);
        if (zoomX)
            xDomain = ZoomRange(current.XDomain, spec.XDomain, centerX, factor);
        if (zoomY)
            yDomain = ZoomRange(current.YDomain, spec.YDomain, centerY, factor);
        CollapseFitDomains(spec);
    }

    public void Pan(PlotSpec spec, double xDelta, double yDelta)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var current = Apply(spec);
        if (double.IsFinite(xDelta) && xDelta != 0)
            xDomain = ShiftRange(current.XDomain, spec.XDomain, xDelta);
        if (double.IsFinite(yDelta) && yDelta != 0)
            yDomain = ShiftRange(current.YDomain, spec.YDomain, yDelta);
        CollapseFitDomains(spec);
    }

    private void CollapseFitDomains(PlotSpec spec)
    {
        if (xDomain is { } x && Covers(x, spec.XDomain)) xDomain = null;
        if (yDomain is { } y && Covers(y, spec.YDomain)) yDomain = null;
    }

    private static PlotRange ZoomRange(PlotRange current, PlotRange extent, double center, double factor)
    {
        current = current.Normalize();
        extent = extent.Normalize();
        center = Math.Clamp(center, current.Minimum, current.Maximum);
        var minimumLength = extent.Length * .01d;
        var targetLength = Math.Clamp(current.Length * factor, minimumLength, extent.Length);
        var leftRatio = (center - current.Minimum) / current.Length;
        var candidate = new PlotRange(center - targetLength * leftRatio, center + targetLength * (1d - leftRatio));
        return Clamp(candidate, extent);
    }

    private static PlotRange ShiftRange(PlotRange current, PlotRange extent, double delta) =>
        Clamp(new(current.Minimum + delta, current.Maximum + delta), extent);

    private static PlotRange Clamp(PlotRange candidate, PlotRange extent)
    {
        candidate = candidate.Normalize();
        extent = extent.Normalize();
        if (candidate.Length >= extent.Length)
            return extent;
        if (candidate.Minimum < extent.Minimum)
            return new(extent.Minimum, extent.Minimum + candidate.Length);
        if (candidate.Maximum > extent.Maximum)
            return new(extent.Maximum - candidate.Length, extent.Maximum);
        return candidate;
    }

    private static bool Covers(PlotRange candidate, PlotRange extent)
    {
        candidate = candidate.Normalize();
        extent = extent.Normalize();
        var tolerance = Math.Max(1e-9d, extent.Length * 1e-9d);
        return candidate.Minimum <= extent.Minimum + tolerance && candidate.Maximum >= extent.Maximum - tolerance;
    }
}
