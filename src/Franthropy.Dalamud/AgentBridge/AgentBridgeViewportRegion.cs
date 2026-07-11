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
    public bool IsFresh(TimeSpan maximumAge, DateTimeOffset now) =>
        maximumAge >= TimeSpan.Zero && now - RenderedAtUtc <= maximumAge;

    public (Vector2 Uv0, Vector2 Uv1) GetUvBounds(float paddingPixels = 8f)
    {
        if (!float.IsFinite(paddingPixels) || paddingPixels < 0f ||
            ViewportSize.X <= 0f || ViewportSize.Y <= 0f ||
            !float.IsFinite(ViewportSize.X) || !float.IsFinite(ViewportSize.Y))
            throw new InvalidOperationException("Viewport size and padding must be finite and positive.");

        var min = WindowPosition - new Vector2(paddingPixels) - ViewportPosition;
        var max = WindowPosition + WindowSize + new Vector2(paddingPixels) - ViewportPosition;
        return (
            new Vector2(Math.Clamp(min.X / ViewportSize.X, 0f, 1f), Math.Clamp(min.Y / ViewportSize.Y, 0f, 1f)),
            new Vector2(Math.Clamp(max.X / ViewportSize.X, 0f, 1f), Math.Clamp(max.Y / ViewportSize.Y, 0f, 1f)));
    }
}
