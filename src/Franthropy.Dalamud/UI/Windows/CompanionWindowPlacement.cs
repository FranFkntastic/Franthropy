using System.Numerics;

namespace Franthropy.Dalamud.UI.Windows;

public enum CompanionWindowPlacementKind
{
    Right,
    Left,
    Overlay,
}

public sealed record CompanionWindowPlacementResult(
    Vector2 Position,
    CompanionWindowPlacementKind Kind);

public static class CompanionWindowPlacement
{
    public static CompanionWindowPlacementResult Calculate(
        Vector2 anchorPosition,
        Vector2 anchorSize,
        Vector2 companionSize,
        Vector2 workPosition,
        Vector2 workSize,
        float gap = 8f)
    {
        if (!IsFinite(anchorPosition) || !IsFinite(anchorSize) || !IsFinite(companionSize) ||
            !IsFinite(workPosition) || !IsFinite(workSize) || !float.IsFinite(gap) || gap < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(anchorPosition), "Window geometry and gap must be finite and non-negative.");
        }

        var width = Math.Max(0f, companionSize.X);
        var height = Math.Max(0f, companionSize.Y);
        var workMax = workPosition + workSize;
        var right = anchorPosition.X + anchorSize.X + gap;
        var left = anchorPosition.X - width - gap;
        float x;
        CompanionWindowPlacementKind kind;
        if (right + width <= workMax.X)
        {
            x = right;
            kind = CompanionWindowPlacementKind.Right;
        }
        else if (left >= workPosition.X)
        {
            x = left;
            kind = CompanionWindowPlacementKind.Left;
        }
        else
        {
            x = Math.Clamp(
                anchorPosition.X + anchorSize.X - width - gap,
                workPosition.X,
                Math.Max(workPosition.X, workMax.X - width));
            kind = CompanionWindowPlacementKind.Overlay;
        }

        var y = Math.Clamp(anchorPosition.Y, workPosition.Y, Math.Max(workPosition.Y, workMax.Y - height));
        return new CompanionWindowPlacementResult(new Vector2(x, y), kind);
    }

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
