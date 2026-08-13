using System.Numerics;

namespace Franthropy.Dalamud.UI.Styling;

public enum DalamudUiDensity
{
    Compact,
    Comfortable,
}

public enum DalamudUiMotionLevel
{
    Off,
    Subtle,
    Expressive,
}

[Flags]
public enum DalamudUiThemeParts
{
    None = 0,
    Text = 1 << 0,
    Controls = 1 << 1,
    Surfaces = 1 << 2,
    Metrics = 1 << 3,
    All = Text | Controls | Surfaces | Metrics,
}

public readonly record struct DalamudUiMetrics(
    Vector2 FramePadding,
    Vector2 ItemSpacing,
    Vector2 ItemInnerSpacing,
    Vector2 WindowPadding,
    float FrameRounding,
    float ChildRounding,
    float PopupRounding,
    float GrabRounding,
    float BorderSize,
    float SectionSpacing)
{
    public static DalamudUiMetrics For(DalamudUiDensity density) =>
        density switch
        {
            DalamudUiDensity.Compact => new(
                new(7f, 3f),
                new(7f, 5f),
                new(5f, 4f),
                new(9f, 8f),
                4f,
                5f,
                5f,
                4f,
                1f,
                8f),
            _ => new(
                new(9f, 5f),
                new(9f, 7f),
                new(7f, 5f),
                new(12f, 10f),
                6f,
                7f,
                7f,
                6f,
                1f,
                12f),
        };
}

public readonly record struct DalamudUiMotion(
    DalamudUiMotionLevel Level,
    float HoverSeconds,
    float PressSeconds,
    float SelectionSeconds,
    float AttentionSeconds)
{
    public static DalamudUiMotion Off => new(DalamudUiMotionLevel.Off, 0f, 0f, 0f, 0f);

    public static DalamudUiMotion Subtle =>
        new(DalamudUiMotionLevel.Subtle, 0.10f, 0.07f, 0.16f, 0.24f);

    public static DalamudUiMotion Expressive =>
        new(DalamudUiMotionLevel.Expressive, 0.16f, 0.10f, 0.24f, 0.42f);

    public float Resolve(float configuredSeconds) =>
        Level == DalamudUiMotionLevel.Off
            ? 0f
            : Math.Max(0f, configuredSeconds);
}

public sealed record DalamudUiTheme(
    DalamudUiPalette Palette,
    DalamudUiMetrics Metrics,
    DalamudUiMotion Motion)
{
    public static DalamudUiTheme Dark(
        Vector4 accent,
        DalamudUiDensity density = DalamudUiDensity.Compact,
        DalamudUiMotionLevel motion = DalamudUiMotionLevel.Off) =>
        new(
            DalamudUiPalette.Dark(accent),
            DalamudUiMetrics.For(density),
            motion switch
            {
                DalamudUiMotionLevel.Subtle => DalamudUiMotion.Subtle,
                DalamudUiMotionLevel.Expressive => DalamudUiMotion.Expressive,
                _ => DalamudUiMotion.Off,
            });
}
