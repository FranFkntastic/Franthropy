namespace Franthropy.Dalamud.AgentBridge;

public sealed record RenderedUiVirtualPointerInputState(
    int PositionX,
    int PositionY,
    int MouseWheel,
    int MouseButtonHeldFlags,
    int MouseButtonPressedFlags,
    int MouseButtonReleasedFlags,
    int MouseButtonHeldThrottledFlags,
    int DeltaX,
    int DeltaY,
    bool IsGameWindowFocused);

public static class RenderedUiVirtualPointerPolicy
{
    public static bool TryConvertRenderedCoordinate(
        float rendered,
        bool isScreenSizeScaled,
        float screenSizeScale,
        out int cursorCoordinate)
    {
        cursorCoordinate = 0;
        if (!float.IsFinite(rendered))
            return false;
        if (!isScreenSizeScaled)
        {
            cursorCoordinate = (int)Math.Clamp(MathF.Round(rendered), int.MinValue, int.MaxValue);
            return true;
        }
        if (!float.IsFinite(screenSizeScale) || screenSizeScale <= 0)
            return false;
        cursorCoordinate = (int)Math.Clamp(MathF.Round(rendered / screenSizeScale), int.MinValue, int.MaxValue);
        return true;
    }

    public static bool IsValidTarget(
        string addonName,
        string nodePath,
        float left,
        float top,
        float right,
        float bottom) =>
        !string.IsNullOrWhiteSpace(addonName) &&
        addonName.Length <= 64 &&
        !string.IsNullOrWhiteSpace(nodePath) &&
        nodePath.Length <= 256 &&
        nodePath.StartsWith($"{addonName}/", StringComparison.Ordinal) &&
        float.IsFinite(left) &&
        float.IsFinite(top) &&
        float.IsFinite(right) &&
        float.IsFinite(bottom) &&
        right > left &&
        bottom > top;

    public static bool ContainsPoint(float left, float top, float right, float bottom, float x, float y) =>
        float.IsFinite(left) &&
        float.IsFinite(top) &&
        float.IsFinite(right) &&
        float.IsFinite(bottom) &&
        float.IsFinite(x) &&
        float.IsFinite(y) &&
        right > left &&
        bottom > top &&
        x >= left &&
        x < right &&
        y >= top &&
        y < bottom;

    public static RenderedUiVirtualPointerInputState CreateMove(
        RenderedUiVirtualPointerInputState source,
        int targetX,
        int targetY) =>
        source with
        {
            PositionX = targetX,
            PositionY = targetY,
            MouseWheel = 0,
            MouseButtonHeldFlags = 0,
            MouseButtonPressedFlags = 0,
            MouseButtonReleasedFlags = 0,
            MouseButtonHeldThrottledFlags = 0,
            DeltaX = targetX - source.PositionX,
            DeltaY = targetY - source.PositionY,
            IsGameWindowFocused = true,
        };

    public static TResult ExecuteRestored<TResult>(
        RenderedUiVirtualPointerInputState snapshot,
        RenderedUiVirtualPointerInputState temporary,
        Action<RenderedUiVirtualPointerInputState> apply,
        Func<TResult> action)
    {
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            apply(temporary);
            return action();
        }
        finally
        {
            apply(snapshot);
        }
    }

    public static TResult ExecutePointerRestored<TResult>(
        nint snapshot,
        nint temporary,
        Action<nint> apply,
        Func<TResult> action)
    {
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            apply(temporary);
            return action();
        }
        finally
        {
            apply(snapshot);
        }
    }

    public static TResult ExecuteValueRestored<TState, TResult>(
        TState snapshot,
        TState temporary,
        Action<TState> apply,
        Func<TResult> action)
    {
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            apply(temporary);
            return action();
        }
        finally
        {
            apply(snapshot);
        }
    }
}
