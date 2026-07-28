using System.Numerics;

namespace Franthropy.Dalamud.UI.Styling;

public enum DalamudUiTone
{
    Neutral,
    Accent,
    Success,
    Warning,
    Error,
}

public readonly record struct DalamudUiPalette(
    Vector4 Accent,
    Vector4 Success,
    Vector4 Warning,
    Vector4 Error,
    Vector4 Text,
    Vector4 Muted,
    Vector4 Surface,
    Vector4 SurfaceRaised,
    Vector4 Border)
{
    public static DalamudUiPalette Dark(Vector4 accent) =>
        new(
            accent,
            new(0.49f, 0.83f, 0.55f, 1f),
            new(0.94f, 0.77f, 0.39f, 1f),
            new(0.93f, 0.40f, 0.40f, 1f),
            new(0.92f, 0.92f, 0.91f, 1f),
            new(0.66f, 0.68f, 0.66f, 1f),
            new(0.07f, 0.08f, 0.075f, 0.94f),
            new(0.15f, 0.16f, 0.15f, 0.96f),
            new(0.30f, 0.31f, 0.29f, 1f));

    public Vector4 Resolve(DalamudUiTone tone) =>
        tone switch
        {
            DalamudUiTone.Accent => Accent,
            DalamudUiTone.Success => Success,
            DalamudUiTone.Warning => Warning,
            DalamudUiTone.Error => Error,
            _ => Muted,
        };

    public Vector4 ToneSurface(DalamudUiTone tone, float strength = 0.16f)
    {
        var tint = Resolve(tone);
        var amount = Math.Clamp(strength, 0f, 1f);
        return new(
            Surface.X + ((tint.X - Surface.X) * amount),
            Surface.Y + ((tint.Y - Surface.Y) * amount),
            Surface.Z + ((tint.Z - Surface.Z) * amount),
            Surface.W);
    }

    public static Vector4 WithAlpha(Vector4 color, float alpha) =>
        new(color.X, color.Y, color.Z, Math.Clamp(alpha, 0f, 1f));
}
