using System.Numerics;

namespace Franthropy.Dalamud.UI.Plots;

public sealed record PlotLayout(PlotRect Bounds, PlotRect DataArea)
{
    public static PlotLayout Create(PlotRect bounds)
    {
        var left = Math.Min(64f, Math.Max(42f, bounds.Width * .12f));
        var bottom = Math.Min(54f, Math.Max(38f, bounds.Height * .16f));
        var top = Math.Min(34f, Math.Max(18f, bounds.Height * .10f));
        var right = Math.Min(24f, Math.Max(12f, bounds.Width * .04f));
        var data = new PlotRect(
            bounds.Minimum + new Vector2(left, top),
            bounds.Maximum - new Vector2(right, bottom));
        if (data.Width < 40 || data.Height < 40)
            throw new ArgumentOutOfRangeException(nameof(bounds), "Plot bounds are too small after axis layout.");
        return new(bounds, data);
    }
}

public abstract record PlotDrawCommand(int ZIndex);
public sealed record PlotLineCommand(Vector2 Start, Vector2 End, PlotLineStyle Style, int ZIndex) : PlotDrawCommand(ZIndex);
public sealed record PlotRectCommand(PlotRect Rect, PlotColor Color, int ZIndex) : PlotDrawCommand(ZIndex);
public sealed record PlotPointCommand(string DatumId, Vector2 Position, PlotPointVisual Visual, int ZIndex) : PlotDrawCommand(ZIndex);
public sealed record PlotTextCommand(Vector2 Position, string Text, PlotColor Color, int ZIndex) : PlotDrawCommand(ZIndex);

public sealed record PlotCompiledFrame(
    PlotLayout Layout,
    IReadOnlyList<PlotDrawCommand> Commands,
    IReadOnlyList<PlotHitTarget> HitTargets,
    LinearPlotScale XScale,
    LinearPlotScale YScale);

/// <summary>
/// Compiles semantic plot layers into renderer-neutral draw commands. This is the testable seam:
/// Dalamud only paints commands and never owns domains, encodings, hit testing, or plot policy.
/// </summary>
public sealed class PlotCompiler
{
    private static readonly PlotColor AxisColor = new(.64f, .67f, .72f);
    private static readonly PlotColor GridColor = new(.30f, .32f, .36f, .55f);
    private static readonly PlotColor BackgroundColor = new(.08f, .09f, .11f, .42f);

    public PlotCompiledFrame Compile(
        PlotSpec spec,
        PlotRect bounds,
        PlotInteractionState? interaction = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        interaction ??= PlotInteractionState.Empty;
        var layout = PlotLayout.Create(bounds);
        var xScale = new LinearPlotScale(spec.XDomain, layout.DataArea.Minimum.X, layout.DataArea.Maximum.X);
        var yScale = new LinearPlotScale(spec.YDomain, layout.DataArea.Maximum.Y, layout.DataArea.Minimum.Y);
        var commands = new List<PlotDrawCommand>
        {
            new PlotRectCommand(layout.DataArea, BackgroundColor, -100),
        };
        CompileAxes(spec, layout, xScale, yScale, commands);

        var hitTargets = new List<PlotHitTarget>();
        var semanticPointIds = new HashSet<string>(StringComparer.Ordinal);
        var traversal = 0;
        foreach (var layer in spec.Layers.OrderBy(layer => layer.ZIndex).ThenBy(layer => layer.Id, StringComparer.Ordinal))
        {
            switch (layer)
            {
                case PlotBandLayer band:
                    CompileBand(band, layout.DataArea, xScale, yScale, commands);
                    break;
                case PlotRuleLayer rule:
                    CompileRule(rule, layout.DataArea, xScale, yScale, commands);
                    break;
                case PlotPolylineLayer polyline:
                    CompilePolyline(polyline.Data, polyline.Style, polyline.ZIndex, layout.DataArea, xScale, yScale, commands, false, true);
                    break;
                case PlotStepLayer step:
                    CompilePolyline(step.Data, step.Style, step.ZIndex, layout.DataArea, xScale, yScale, commands, true, step.StepAfter);
                    break;
                case PlotPointLayer points:
                    foreach (var datum in points.Data
                        .OrderBy(datum => datum.X)
                        .ThenBy(datum => datum.Y)
                        .ThenBy(datum => datum.Id, StringComparer.Ordinal))
                    {
                        PlotDatumValidation.Validate(datum);
                        if (!semanticPointIds.Add(datum.Id))
                            throw new ArgumentException($"Semantic point id '{datum.Id}' appears in more than one point layer.", nameof(spec));
                        var position = new Vector2(xScale.Map(datum.X), yScale.Map(datum.Y));
                        if (!layout.DataArea.Contains(position))
                            continue;
                        var visual = PlotVisualResolver.Resolve(datum, points.Style, points.Encodings, interaction.ResolveRole(datum.Id));
                        commands.Add(new PlotPointCommand(datum.Id, position, visual, points.ZIndex));
                        hitTargets.Add(new(datum.Id, position, visual.RadiusPixels, traversal++));
                        if (!string.IsNullOrWhiteSpace(visual.Label))
                            commands.Add(new PlotTextCommand(position + new Vector2(visual.RadiusPixels + 4f, -7f), visual.Label, visual.Color.WithAlpha(visual.Opacity), points.ZIndex + 1));
                    }
                    break;
                case PlotAnnotationLayer annotation:
                    var annotationPosition = new Vector2(xScale.Map(annotation.X), yScale.Map(annotation.Y));
                    if (layout.DataArea.Contains(annotationPosition))
                        commands.Add(new PlotTextCommand(annotationPosition + annotation.PixelOffset, annotation.Text, annotation.Color, annotation.ZIndex));
                    break;
            }
        }

        return new(
            layout,
            commands.OrderBy(command => command.ZIndex).ToArray(),
            hitTargets,
            xScale,
            yScale);
    }

    private static void CompileAxes(
        PlotSpec spec,
        PlotLayout layout,
        LinearPlotScale xScale,
        LinearPlotScale yScale,
        ICollection<PlotDrawCommand> commands)
    {
        var axisStyle = new PlotLineStyle(AxisColor, 1f);
        var gridStyle = new PlotLineStyle(GridColor, 1f);
        foreach (var tick in PlotTicks.Create(xScale.Domain, spec.XAxis.DesiredTickCount))
        {
            var x = xScale.Map(tick);
            if (spec.ShowGrid)
                commands.Add(new PlotLineCommand(new(x, layout.DataArea.Minimum.Y), new(x, layout.DataArea.Maximum.Y), gridStyle, -80));
            commands.Add(new PlotLineCommand(new(x, layout.DataArea.Maximum.Y), new(x, layout.DataArea.Maximum.Y + 4), axisStyle, -50));
            commands.Add(new PlotTextCommand(new(x - 12, layout.DataArea.Maximum.Y + 7), FormatTick(spec.XAxis, tick), AxisColor, 100));
        }
        foreach (var tick in PlotTicks.Create(yScale.Domain, spec.YAxis.DesiredTickCount))
        {
            var y = yScale.Map(tick);
            if (spec.ShowGrid)
                commands.Add(new PlotLineCommand(new(layout.DataArea.Minimum.X, y), new(layout.DataArea.Maximum.X, y), gridStyle, -80));
            commands.Add(new PlotLineCommand(new(layout.DataArea.Minimum.X - 4, y), new(layout.DataArea.Minimum.X, y), axisStyle, -50));
            commands.Add(new PlotTextCommand(new(layout.Bounds.Minimum.X + 2, y - 7), FormatTick(spec.YAxis, tick), AxisColor, 100));
        }
        commands.Add(new PlotLineCommand(layout.DataArea.Minimum with { Y = layout.DataArea.Maximum.Y }, layout.DataArea.Maximum, axisStyle, -40));
        commands.Add(new PlotLineCommand(layout.DataArea.Minimum, layout.DataArea.Minimum with { Y = layout.DataArea.Maximum.Y }, axisStyle, -40));
        commands.Add(new PlotTextCommand(
            new(layout.DataArea.Minimum.X + layout.DataArea.Width * .42f, layout.Bounds.Maximum.Y - 17),
            AxisLabel(spec.XAxis),
            AxisColor,
            100));
        commands.Add(new PlotTextCommand(
            new(layout.Bounds.Minimum.X + 2, layout.Bounds.Minimum.Y + 2),
            AxisLabel(spec.YAxis),
            AxisColor,
            100));
        if (!string.IsNullOrWhiteSpace(spec.Title))
            commands.Add(new PlotTextCommand(new(layout.DataArea.Minimum.X, layout.Bounds.Minimum.Y + 2), spec.Title, AxisColor, 100));
    }

    private static string FormatTick(PlotAxis axis, double value) => axis.Format?.Invoke(value) ?? value.ToString("0.##");
    private static string AxisLabel(PlotAxis axis) => string.IsNullOrWhiteSpace(axis.Unit) ? axis.Label : $"{axis.Label} ({axis.Unit})";

    private static void CompileBand(
        PlotBandLayer band,
        PlotRect area,
        LinearPlotScale xScale,
        LinearPlotScale yScale,
        ICollection<PlotDrawCommand> commands)
    {
        PlotRect rect;
        if (band.Orientation == PlotRuleOrientation.Vertical)
        {
            var first = xScale.Map(Math.Min(band.Minimum, band.Maximum));
            var second = xScale.Map(Math.Max(band.Minimum, band.Maximum));
            rect = new(new(Math.Max(first, area.Minimum.X), area.Minimum.Y), new(Math.Min(second, area.Maximum.X), area.Maximum.Y));
        }
        else
        {
            var first = yScale.Map(Math.Max(band.Minimum, band.Maximum));
            var second = yScale.Map(Math.Min(band.Minimum, band.Maximum));
            rect = new(new(area.Minimum.X, Math.Max(first, area.Minimum.Y)), new(area.Maximum.X, Math.Min(second, area.Maximum.Y)));
        }
        if (rect.Width <= 0 || rect.Height <= 0)
            return;
        commands.Add(new PlotRectCommand(rect, band.Style.Color, band.ZIndex));
        if (!string.IsNullOrWhiteSpace(band.Label))
            commands.Add(new PlotTextCommand(rect.Minimum + new Vector2(4, 3), band.Label, band.Style.Color.WithAlpha(1f), band.ZIndex + 1));
    }

    private static void CompileRule(
        PlotRuleLayer rule,
        PlotRect area,
        LinearPlotScale xScale,
        LinearPlotScale yScale,
        ICollection<PlotDrawCommand> commands)
    {
        Vector2 start;
        Vector2 end;
        if (rule.Orientation == PlotRuleOrientation.Vertical)
        {
            var x = xScale.Map(rule.Value);
            start = new(x, area.Minimum.Y);
            end = new(x, area.Maximum.Y);
        }
        else
        {
            var y = yScale.Map(rule.Value);
            start = new(area.Minimum.X, y);
            end = new(area.Maximum.X, y);
        }
        if (!PlotClipping.TryClipLine(area, start, end, out start, out end))
            return;
        commands.Add(new PlotLineCommand(start, end, rule.Style, rule.ZIndex));
        if (!string.IsNullOrWhiteSpace(rule.Label))
            commands.Add(new PlotTextCommand(start + new Vector2(4, 3), rule.Label, rule.Style.Color, rule.ZIndex + 1));
    }

    private static void CompilePolyline(
        IReadOnlyList<PlotDatum> data,
        PlotLineStyle style,
        int zIndex,
        PlotRect area,
        LinearPlotScale xScale,
        LinearPlotScale yScale,
        ICollection<PlotDrawCommand> commands,
        bool stepped,
        bool stepAfter)
    {
        for (var index = 1; index < data.Count; index++)
        {
            var previous = new Vector2(xScale.Map(data[index - 1].X), yScale.Map(data[index - 1].Y));
            var current = new Vector2(xScale.Map(data[index].X), yScale.Map(data[index].Y));
            if (!stepped)
            {
                AddClipped(previous, current);
                continue;
            }
            var corner = stepAfter ? new Vector2(current.X, previous.Y) : new Vector2(previous.X, current.Y);
            AddClipped(previous, corner);
            AddClipped(corner, current);
        }

        void AddClipped(Vector2 start, Vector2 end)
        {
            if (PlotClipping.TryClipLine(area, start, end, out var clippedStart, out var clippedEnd))
                commands.Add(new PlotLineCommand(clippedStart, clippedEnd, style, zIndex));
        }
    }
}
