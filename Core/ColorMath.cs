using System.Numerics;

namespace IzzysFurniture;

internal static class ColorMath
{
    public static Vector4 Clamp01(Vector4 color)
        => Vector4.Clamp(color, Vector4.Zero, Vector4.One);
}
