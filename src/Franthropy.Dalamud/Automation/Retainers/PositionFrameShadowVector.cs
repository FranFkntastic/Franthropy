using System.Numerics;

namespace Franthropy.Dalamud.Automation.Retainers;

public sealed record PositionFrameShadowVector(float X, float Y, float Z)
{
    public static PositionFrameShadowVector From(Vector3 value) =>
        new(value.X, value.Y, value.Z);
}
