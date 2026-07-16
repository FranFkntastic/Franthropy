using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Plots;

public sealed record DalamudPlotRenderResult(
    PlotCompiledFrame Frame,
    string? HoveredDatumId,
    string? ClickedDatumId);

/// <summary>
/// Thin immediate-mode renderer for compiled plot commands. All semantic decisions remain in
/// the pure compiler, allowing geometry and interaction behavior to be tested without Dalamud.
/// </summary>
public sealed class DalamudPlotRenderer
{
    private readonly PlotCompiler compiler = new();

    public DalamudPlotRenderResult Draw(
        string id,
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
        var frame = compiler.Compile(spec, bounds, interaction);
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
        foreach (var role in EnumerateRoles(visual.Role))
        {
            drawList.AddCircle(point.Position, ring, ToU32(RoleColor(role)), 20, 1.5f);
            ring += 2.5f;
        }
    }

    private static IEnumerable<PlotPointRole> EnumerateRoles(PlotPointRole roles)
    {
        if (roles.HasFlag(PlotPointRole.Selected)) yield return PlotPointRole.Selected;
        if (roles.HasFlag(PlotPointRole.Nominated)) yield return PlotPointRole.Nominated;
        if (roles.HasFlag(PlotPointRole.Warning)) yield return PlotPointRole.Warning;
        if (roles.HasFlag(PlotPointRole.Failure)) yield return PlotPointRole.Failure;
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
