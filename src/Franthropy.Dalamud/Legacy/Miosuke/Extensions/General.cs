using System.Numerics;
using Dalamud.Game.ClientState.Keys;
using Miosuke.Action;

namespace Miosuke.Extensions;

public static class VirtualKeyExtensions
{
    private static readonly Dictionary<VirtualKey, string> VirtualKeyNames = new() {
        { VirtualKey.KEY_0, "0"},
        { VirtualKey.KEY_1, "1"},
        { VirtualKey.KEY_2, "2"},
        { VirtualKey.KEY_3, "3"},
        { VirtualKey.KEY_4, "4"},
        { VirtualKey.KEY_5, "5"},
        { VirtualKey.KEY_6, "6"},
        { VirtualKey.KEY_7, "7"},
        { VirtualKey.KEY_8, "8"},
        { VirtualKey.KEY_9, "9"},
        { VirtualKey.CONTROL, "Ctrl"},
        { VirtualKey.MENU, "Alt"},
        { VirtualKey.SHIFT, "Shift"},
    };

    public static string GetKeyName(this VirtualKey vk) => VirtualKeyNames.TryGetValue(vk, out var value) ? value : vk.ToString();
    public static string HotkeyToString(this IEnumerable<VirtualKey> hotkey) => string.Join("+", hotkey.Select(k => k.GetKeyName()));
}

public static class FloatArrayExtensions
{
    public static Vector2 ToVector2(this float[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length < 2) throw new ArgumentException("The source array must contain at least 2 elements.", nameof(source));
        return new Vector2(source[0], source[1]);
    }
    public static Vector3 ToVector3(this float[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length < 3) throw new ArgumentException("The source array must contain at least 3 elements.", nameof(source));
        return new Vector3(source[0], source[1], source[2]);
    }
    public static Vector4 ToVector4(this float[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length < 4) throw new ArgumentException("The source array must contain at least 4 elements.", nameof(source));
        return new Vector4(source[0], source[1], source[2], source[3]);
    }
}

public static class VectorExtensions
{
    public static float[] ToArray(this Vector2 vector) => [vector.X, vector.Y];
    public static float[] ToArray(this Vector3 vector) => [vector.X, vector.Y, vector.Z];
    public static float[] ToArray(this Vector4 vector) => [vector.X, vector.Y, vector.Z, vector.W];
}
