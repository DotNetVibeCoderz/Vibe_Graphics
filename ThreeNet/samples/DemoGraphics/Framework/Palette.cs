using System.Numerics;
using ThreeNet;

namespace DemoGraphics.Framework;

/// <summary>Colour work the scenes share: black body temperature and swatches.</summary>
public static class Palette
{
    /// <summary>
    /// Linear RGB of a black body at <paramref name="kelvin"/>, normalised so the
    /// brightest channel is 1. Good enough for lighting a scene by colour
    /// temperature, which is how photographers and gaffers think about lights.
    /// </summary>
    public static Vector3 Kelvin(float kelvin)
    {
        float temperature = Math.Clamp(kelvin, 1000f, 15000f) / 100f;

        float red = temperature <= 66f
            ? 255f
            : 329.7f * MathF.Pow(temperature - 60f, -0.1332f);

        float green = temperature <= 66f
            ? (99.47f * MathF.Log(temperature)) - 161.12f
            : 288.12f * MathF.Pow(temperature - 60f, -0.0755f);

        float blue = temperature >= 66f
            ? 255f
            : temperature <= 19f
                ? 0f
                : (138.52f * MathF.Log(temperature - 10f)) - 305.04f;

        Vector3 srgb = new(
            Math.Clamp(red, 0f, 255f) / 255f,
            Math.Clamp(green, 0f, 255f) / 255f,
            Math.Clamp(blue, 0f, 255f) / 255f);

        Vector3 linear = new(
            MathHelpers.SrgbToLinear(srgb.X),
            MathHelpers.SrgbToLinear(srgb.Y),
            MathHelpers.SrgbToLinear(srgb.Z));

        float peak = MathF.Max(linear.X, MathF.Max(linear.Y, linear.Z));
        return peak > 1e-4f ? linear / peak : Vector3.One;
    }

    /// <summary>Linear RGB from an sRGB hex colour.</summary>
    public static Vector3 Rgb(uint hex)
    {
        Vector4 rgba = MathHelpers.FromHex(hex);
        return new Vector3(rgba.X, rgba.Y, rgba.Z);
    }

    /// <summary>Linear RGBA from an sRGB hex colour.</summary>
    public static Vector4 Rgba(uint hex, float alpha = 1f) => MathHelpers.FromHex(hex, alpha);
}
