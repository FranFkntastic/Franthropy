using System.Numerics;

namespace Franthropy.Dalamud.AgentBridge;

/// <summary>Describes an ImGui window within a viewport and converts its rendered bounds to capture UV coordinates.</summary>
public sealed record AgentBridgeViewportRegion(
    Vector2 WindowPosition,
    Vector2 WindowSize,
    Vector2 ViewportPosition,
    Vector2 ViewportSize,
    DateTimeOffset RenderedAtUtc)
{
    /// <summary>Gets the ImGui viewport that owns the rendered window.</summary>
    public uint? ViewportId { get; init; }

    public bool IsFresh(TimeSpan maximumAge, DateTimeOffset now) =>
        maximumAge >= TimeSpan.Zero && now - RenderedAtUtc <= maximumAge;

    public (Vector2 Uv0, Vector2 Uv1) GetUvBounds(float paddingPixels = 8f)
    {
        if (!float.IsFinite(paddingPixels) || paddingPixels < 0f ||
            !float.IsFinite(WindowPosition.X) || !float.IsFinite(WindowPosition.Y) ||
            WindowSize.X <= 0f || WindowSize.Y <= 0f ||
            !float.IsFinite(WindowSize.X) || !float.IsFinite(WindowSize.Y) ||
            !float.IsFinite(ViewportPosition.X) || !float.IsFinite(ViewportPosition.Y) ||
            ViewportSize.X <= 0f || ViewportSize.Y <= 0f ||
            !float.IsFinite(ViewportSize.X) || !float.IsFinite(ViewportSize.Y))
            throw new InvalidOperationException("Window and viewport bounds must be finite and have positive size.");

        var min = WindowPosition - new Vector2(paddingPixels) - ViewportPosition;
        var max = WindowPosition + WindowSize + new Vector2(paddingPixels) - ViewportPosition;
        var result = (
            new Vector2(Math.Clamp(min.X / ViewportSize.X, 0f, 1f), Math.Clamp(min.Y / ViewportSize.Y, 0f, 1f)),
            new Vector2(Math.Clamp(max.X / ViewportSize.X, 0f, 1f), Math.Clamp(max.Y / ViewportSize.Y, 0f, 1f)));
        if (result.Item1.X >= result.Item2.X || result.Item1.Y >= result.Item2.Y)
            throw new InvalidOperationException("The rendered window does not overlap its viewport.");
        return result;
    }
}
