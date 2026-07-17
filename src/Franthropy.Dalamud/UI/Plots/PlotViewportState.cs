using System.Numerics;

namespace Franthropy.Dalamud.UI.Plots;

/// <summary>
/// Product-neutral viewport state for plots. It owns only the visible domains; renderers remain
/// free to provide mouse, touch, keyboard, or explicit-control affordances over the same model.
/// </summary>
public sealed class PlotViewportState
{
    private PlotRange? xDomain;
    private PlotRange? yDomain;
    private PlotRange? xNavigationExtent;

    public bool IsFit => xDomain is null && yDomain is null;
    public bool HasHorizontalViewport => xDomain is not null;

    public PlotSpec Apply(PlotSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec with
        {
            XDomain = xDomain is { } x ? Clamp(x, EffectiveXExtent(spec)) : spec.XDomain,
            YDomain = yDomain is { } y ? Clamp(y, spec.YDomain) : spec.YDomain,
        };
    }

    public PlotSpec ApplyForRendering(PlotSpec spec)
    {
        var applied = Apply(spec);
        return HasHorizontalViewport ? applied with { XAxisBreak = null } : applied;
    }

    public void Fit()
    {
        xDomain = null;
        yDomain = null;
        xNavigationExtent = null;
    }

    public void Zoom(
        PlotSpec spec,
        double centerX,
        double centerY,
        double factor,
        bool zoomX = true,
        bool zoomY = true,
        PlotRange? xExtent = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!double.IsFinite(factor) || factor <= 0)
            throw new ArgumentOutOfRangeException(nameof(factor));
        var current = Apply(spec);
        if (zoomX)
        {
            if (xExtent is { } requestedExtent)
                xNavigationExtent = Intersect(requestedExtent, spec.XDomain);
            var extent = EffectiveXExtent(spec);
            xDomain = ZoomRange(Clamp(current.XDomain, extent), extent, centerX, factor);
        }
        if (zoomY)
            yDomain = ZoomRange(current.YDomain, spec.YDomain, centerY, factor);
        CollapseFitDomains(spec);
    }

    public void Pan(PlotSpec spec, double xDelta, double yDelta)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var current = Apply(spec);
        if (double.IsFinite(xDelta) && xDelta != 0)
            xDomain = ShiftRange(current.XDomain, EffectiveXExtent(spec), xDelta);
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

    private static PlotRange Intersect(PlotRange first, PlotRange second)
    {
        first = first.Normalize();
        second = second.Normalize();
        var intersection = new PlotRange(Math.Max(first.Minimum, second.Minimum), Math.Min(first.Maximum, second.Maximum));
        return intersection.Minimum < intersection.Maximum ? intersection : second;
    }

    private PlotRange EffectiveXExtent(PlotSpec spec) =>
        xNavigationExtent is { } navigation ? Intersect(navigation, spec.XDomain) : spec.XDomain;

    private static bool Covers(PlotRange candidate, PlotRange extent)
    {
        candidate = candidate.Normalize();
        extent = extent.Normalize();
        var tolerance = Math.Max(1e-9d, extent.Length * 1e-9d);
        return candidate.Minimum <= extent.Minimum + tolerance && candidate.Maximum >= extent.Maximum - tolerance;
    }
}

public readonly record struct PlotViewportInput(
    float WheelDelta,
    bool ControlHeld,
    bool RightButtonDragging,
    Vector2 Pointer,
    Vector2 DragDelta);

/// <summary>
/// Maps guarded pointer gestures onto viewport operations. Unmodified wheel input is deliberately
/// ignored so the containing Dalamud window retains ordinary scrolling.
/// </summary>
public static class PlotViewportInputController
{
    public static bool Apply(
        PlotViewportState viewport,
        PlotSpec spec,
        PlotCompiledFrame frame,
        PlotViewportInput input)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(frame);
        var changed = false;
        if (input.ControlHeld && input.WheelDelta != 0)
        {
            var centerX = frame.XScale.Invert(input.Pointer.X);
            viewport.Zoom(
                spec,
                centerX,
                frame.YScale.Invert(input.Pointer.Y),
                Math.Pow(.82d, input.WheelDelta),
                xExtent: ResolveNavigationExtent(frame.XScale, centerX));
            changed = true;
        }
        if (input.RightButtonDragging && input.DragDelta != Vector2.Zero)
        {
            var visible = viewport.Apply(spec);
            viewport.Pan(
                spec,
                -input.DragDelta.X / frame.Layout.DataArea.Width * visible.XDomain.Length,
                input.DragDelta.Y / frame.Layout.DataArea.Height * visible.YDomain.Length);
            changed = true;
        }
        return changed;
    }

    private static PlotRange? ResolveNavigationExtent(LinearPlotScale scale, double value) =>
        scale is BrokenLinearPlotScale
            ? scale.VisibleDomainRanges
                .Where(range => value >= range.Minimum && value <= range.Maximum)
                .Select(range => (PlotRange?)range)
                .FirstOrDefault()
            : null;
}
