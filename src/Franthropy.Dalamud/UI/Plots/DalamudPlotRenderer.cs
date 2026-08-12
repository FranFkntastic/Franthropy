using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Plots;

public sealed record DalamudPlotRenderResult(
    PlotCompiledFrame Frame,
    string? HoveredDatumId,
    string? ClickedDatumId);

public readonly record struct PlotRenderRevision(
    long DataRevision,
    long ViewRevision = 0);

/// <summary>
/// Thin immediate-mode renderer for compiled plot commands. All semantic decisions remain in
/// the pure compiler, allowing geometry and interaction behavior to be tested without Dalamud.
/// Callers must advance <see cref="PlotRenderRevision.DataRevision"/> whenever plot or interaction
/// data changes; bounds and view revision are tracked independently in the cache key.
/// </summary>
public sealed class DalamudPlotRenderer
{
    private const int MaximumCachedPlots = 64;
    private readonly PlotCompiler compiler = new();
    private readonly Dictionary<string, CachedPlotFrame> compiledFrames =
        new(StringComparer.Ordinal);

    public DalamudPlotRenderResult Draw(
        string id,
        PlotRenderRevision revision,
        PlotSpec spec,
        Vector2 requestedSize,
        PlotInteractionState? interaction = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(spec);
        var available = ImGui.GetContentRegionAvail();
        var size = new Vector2(
            requestedSize.X > 0 ? requestedSize.X : available.X,
            requestedSize.Y > 0 ? requestedSize.Y : Math.Max(220f, available.Y));
        size.X = Math.Max(180f, size.X);
        size.Y = Math.Max(160f, size.Y);

        ImGui.InvisibleButton($"##FranthropyPlot{id}", size);
        var bounds = new PlotRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        var compileRevision = new PlotCompileRevision(revision, bounds);
        if (!compiledFrames.ContainsKey(id) && compiledFrames.Count >= MaximumCachedPlots)
            compiledFrames.Clear();
        if (!compiledFrames.TryGetValue(id, out var cached) || cached.Revision != compileRevision)
        {
            var compiled = compiler.Compile(spec, bounds, interaction);
            compiledFrames[id] = cached = new(compileRevision, compiled);
        }
        var frame = cached.Frame;
        var drawList = ImGui.GetWindowDrawList();
        foreach (var command in frame.Commands)
            DrawCommand(drawList, command);

        PlotHitTarget? hovered = null;
        if (ImGui.IsItemHovered())
            hovered = PlotHitTesting.FindNearest(frame.HitTargets, ImGui.GetMousePos());
        var clicked = hovered is not null && ImGui.IsMouseClicked(ImGuiMouseButton.Left)
            ? hovered.DatumId
            : null;
        return new(frame, hovered?.DatumId, clicked);
    }

    public void Invalidate(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        compiledFrames.Remove(id);
    }

    public void Clear() => compiledFrames.Clear();

    private readonly record struct PlotCompileRevision(
        PlotRenderRevision Revision,
        PlotRect Bounds);

    private sealed record CachedPlotFrame(
        PlotCompileRevision Revision,
        PlotCompiledFrame Frame);

    private static void DrawCommand(ImDrawListPtr drawList, PlotDrawCommand command)
    {
        switch (command)
        {
            case PlotLineCommand line:
                drawList.AddLine(line.Start, line.End, ToU32(line.Style.Color), line.Style.Thickness);
                break;
            case PlotRectCommand rect:
                drawList.AddRectFilled(rect.Rect.Minimum, rect.Rect.Maximum, ToU32(rect.Color));
                break;
            case PlotTextCommand text:
                drawList.AddText(text.Position, ToU32(text.Color), text.Text);
                break;
            case PlotPointCommand point:
                DrawPoint(drawList, point);
                break;
        }
    }

    private static void DrawPoint(ImDrawListPtr drawList, PlotPointCommand point)
    {
        var visual = point.Visual;
        var color = ToU32(visual.Color.WithAlpha(visual.Color.Alpha * visual.Opacity));
        var radius = visual.RadiusPixels;
        switch (visual.Shape)
        {
            case PlotPointShape.Square:
                drawList.AddRectFilled(point.Position - new Vector2(radius), point.Position + new Vector2(radius), color);
                break;
            case PlotPointShape.Diamond:
                drawList.AddTriangleFilled(
                    point.Position + new Vector2(0, -radius),
                    point.Position + new Vector2(radius, 0),
                    point.Position + new Vector2(0, radius),
                    color);
                drawList.AddTriangleFilled(
                    point.Position + new Vector2(0, -radius),
                    point.Position + new Vector2(0, radius),
                    point.Position + new Vector2(-radius, 0),
                    color);
                break;
            case PlotPointShape.Triangle:
                drawList.AddTriangleFilled(
                    point.Position + new Vector2(0, -radius),
                    point.Position + new Vector2(radius, radius),
                    point.Position + new Vector2(-radius, radius),
                    color);
                break;
            default:
                drawList.AddCircleFilled(point.Position, radius, color);
                break;
        }

        var ring = radius + 2f;
        DrawRoleRing(drawList, point.Position, visual.Role, PlotPointRole.Selected, ref ring);
        DrawRoleRing(drawList, point.Position, visual.Role, PlotPointRole.Nominated, ref ring);
        DrawRoleRing(drawList, point.Position, visual.Role, PlotPointRole.Warning, ref ring);
        DrawRoleRing(drawList, point.Position, visual.Role, PlotPointRole.Failure, ref ring);
    }

    private static void DrawRoleRing(
        ImDrawListPtr drawList,
        Vector2 position,
        PlotPointRole roles,
        PlotPointRole role,
        ref float ring)
    {
        if ((roles & role) == 0)
            return;
        drawList.AddCircle(position, ring, ToU32(RoleColor(role)), 20, 1.5f);
        ring += 2.5f;
    }

    private static PlotColor RoleColor(PlotPointRole role) => role switch
    {
        PlotPointRole.Nominated => new(.38f, .88f, .53f),
        PlotPointRole.Warning => new(.98f, .72f, .22f),
        PlotPointRole.Failure => new(.96f, .31f, .31f),
        _ => new(.94f, .95f, .98f),
    };

    private static uint ToU32(PlotColor color)
    {
        var red = (uint)Math.Round(Math.Clamp(color.Red, 0f, 1f) * 255f);
        var green = (uint)Math.Round(Math.Clamp(color.Green, 0f, 1f) * 255f);
        var blue = (uint)Math.Round(Math.Clamp(color.Blue, 0f, 1f) * 255f);
        var alpha = (uint)Math.Round(Math.Clamp(color.Alpha, 0f, 1f) * 255f);
        return red | green << 8 | blue << 16 | alpha << 24;
    }
}

/// <summary>
/// Reusable direct-manipulation container for any Dalamud plot. The container owns viewport and
/// height state while the plot renderer remains a thin painter of compiled geometry.
/// </summary>
public sealed record DalamudPlotContainerControl(
    string Id,
    string Label,
    PlotRect Bounds,
    bool Enabled,
    bool Selected,
    string Value,
    Action Invoke);

public sealed record DalamudPlotContainerResult(
    DalamudPlotRenderResult Plot,
    IReadOnlyList<DalamudPlotContainerControl> Controls)
{
    public PlotCompiledFrame Frame => Plot.Frame;
    public string? HoveredDatumId => Plot.HoveredDatumId;
    public string? ClickedDatumId => Plot.ClickedDatumId;
}

public sealed class DalamudPlotContainer
{
    private sealed class ContainerState(float height)
    {
        public PlotViewportState Viewport { get; } = new();
        public float Height { get; set; } = height;
        public PlotCompiledFrame? LastFrame { get; set; }
        public long ViewportRevision { get; set; }
    }

    private readonly DalamudPlotRenderer renderer = new();
    private readonly Dictionary<string, ContainerState> states = new(StringComparer.Ordinal);

    public DalamudPlotContainerResult Draw(
        string id,
        long revision,
        PlotSpec spec,
        Vector2 requestedSize,
        PlotInteractionState? interaction = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(spec);
        var initialHeight = requestedSize.Y > 0 ? requestedSize.Y : 285f;
        if (!states.TryGetValue(id, out var state))
            states[id] = state = new(Math.Clamp(initialHeight, 180f, 900f));

        ImGui.PushID(id);
        var controls = new List<DalamudPlotContainerControl>(3);
        var fitEnabled = !state.Viewport.IsFit;
        if (!fitEnabled)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Fit"))
        {
            state.Viewport.Fit();
            state.ViewportRevision++;
        }
        if (!fitEnabled)
            ImGui.EndDisabled();
        controls.Add(Control("fit", "Fit plot to all evidence", fitEnabled, state.Viewport.IsFit, state, spec, state.Viewport.Fit));
        ImGui.SameLine();
        if (!fitEnabled)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Zoom -"))
        {
            ZoomFromCenter(state, spec, 1.25d);
            state.ViewportRevision++;
        }
        if (!fitEnabled)
            ImGui.EndDisabled();
        controls.Add(Control("zoom-out", "Zoom plot out", fitEnabled, false, state, spec, () => ZoomFromCenter(state, spec, 1.25d)));
        ImGui.SameLine();
        if (ImGui.SmallButton("Zoom +"))
        {
            ZoomFromCenter(state, spec, .80d);
            state.ViewportRevision++;
        }
        controls.Add(Control("zoom-in", "Zoom plot in", true, false, state, spec, () => ZoomFromCenter(state, spec, .80d)));
        ImGui.SameLine();
        ImGui.TextDisabled(state.Viewport.IsFit
            ? "Fit view · Ctrl+wheel zoom · right-drag pan"
            : "Zoomed view · Ctrl+wheel zoom · right-drag pan");

        var visibleSpec = state.Viewport.ApplyForRendering(spec);
        var result = renderer.Draw(
            "Viewport",
            new PlotRenderRevision(revision, state.ViewportRevision),
            visibleSpec,
            new(requestedSize.X, state.Height),
            interaction);
        if (HandleViewportInput(state.Viewport, spec, result.Frame))
        {
            state.ViewportRevision++;
            result = result with { HoveredDatumId = null };
        }
        state.LastFrame = result.Frame;
        DrawResizeHandle(state);
        ImGui.PopID();
        return new(result, controls);
    }

    private static bool HandleViewportInput(PlotViewportState viewport, PlotSpec spec, PlotCompiledFrame frame)
    {
        if (!ImGui.IsItemHovered())
            return false;
        var io = ImGui.GetIO();
        var rightDragging = ImGui.IsMouseDragging(ImGuiMouseButton.Right, 0f);
        PlotViewportInputController.Apply(
            viewport,
            spec,
            frame,
            new(io.MouseWheel, io.KeyCtrl, rightDragging, ImGui.GetMousePos(), io.MouseDelta));
        return rightDragging;
    }

    private static DalamudPlotContainerControl Control(
        string id,
        string label,
        bool enabled,
        bool selected,
        ContainerState state,
        PlotSpec spec,
        Action invoke) => new(
            id,
            label,
            new(ImGui.GetItemRectMin(), ImGui.GetItemRectMax()),
            enabled,
            selected,
            ViewSummary(state.Viewport.Apply(spec)),
            invoke);

    private static void DrawResizeHandle(ContainerState state)
    {
        var width = Math.Max(180f, ImGui.GetContentRegionAvail().X);
        ImGui.InvisibleButton("##Resize", new(width, 9f));
        var hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
        {
            state.Height = Math.Clamp(state.Height + ImGui.GetIO().MouseDelta.Y, 180f, 900f);
            state.ViewportRevision++;
        }
        var minimum = ImGui.GetItemRectMin();
        var maximum = ImGui.GetItemRectMax();
        var y = (minimum.Y + maximum.Y) * .5f;
        var center = (minimum.X + maximum.X) * .5f;
        var half = hovered ? 28f : 18f;
        ImGui.GetWindowDrawList().AddLine(
            new(center - half, y),
            new(center + half, y),
            ImGui.GetColorU32(ImGuiCol.Separator),
            hovered ? 2f : 1f);
    }

    private static void ZoomFromCenter(ContainerState state, PlotSpec spec, double factor)
    {
        var visible = state.Viewport.Apply(spec);
        PlotRange? xExtent = null;
        var centerX = (visible.XDomain.Minimum + visible.XDomain.Maximum) * .5d;
        if (!state.Viewport.HasHorizontalViewport && state.LastFrame?.XScale is BrokenLinearPlotScale broken)
        {
            xExtent = broken.VisibleDomainRanges[^1];
            centerX = (xExtent.Value.Minimum + xExtent.Value.Maximum) * .5d;
        }
        state.Viewport.Zoom(
            spec,
            centerX,
            (visible.YDomain.Minimum + visible.YDomain.Maximum) * .5d,
            factor,
            xExtent: xExtent);
    }

    private static string ViewSummary(PlotSpec spec) =>
        $"x {spec.XDomain.Minimum:0.##}..{spec.XDomain.Maximum:0.##}; y {spec.YDomain.Minimum:0.##}..{spec.YDomain.Maximum:0.##}";
}
