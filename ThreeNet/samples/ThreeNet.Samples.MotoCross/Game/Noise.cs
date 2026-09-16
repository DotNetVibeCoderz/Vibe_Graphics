using System.Numerics;

namespace MotoCross.Game;

/// <summary>
/// Deterministic value noise. The whole track, its textures and the scenery are
/// generated from it, so the same seed always produces the same circuit.
/// </summary>
public static class Noise
{
    private static float Hash(int x, int y, int seed)
    {
        int h = (x * 374761393) + (y * 668265263) + (seed * 362437);
        h = (h ^ (h >> 13)) * 1274126177;
        return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
    }

    private static float Smooth(float t) => t * t * (3f - (2f * t));

    /// <summary>Value noise in [0, 1] with smooth interpolation.</summary>
    public static float Value(float x, float y, int seed = 0)
    {
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        float fx = Smooth(x - x0);
        float fy = Smooth(y - y0);

        float a = Hash(x0, y0, seed);
        float b = Hash(x0 + 1, y0, seed);
        float c = Hash(x0, y0 + 1, seed);
        float d = Hash(x0 + 1, y0 + 1, seed);
        return float.Lerp(float.Lerp(a, b, fx), float.Lerp(c, d, fx), fy);
    }

    /// <summary>Sum of octaves; each one halves the amplitude and doubles the frequency.</summary>
    public static float Fbm(float x, float y, int octaves = 4, float frequency = 1f, int seed = 0)
    {
        float sum = 0f;
        float amplitude = 1f;
        float total = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += Value(x * frequency, y * frequency, seed + i) * amplitude;
            total += amplitude;
            amplitude *= 0.5f;
            frequency *= 2f;
        }

        return sum / MathF.Max(total, 1e-4f);
    }

    /// <summary>Ridged variant, useful for rocky detail.</summary>
    public static float Ridge(float x, float y, int octaves = 3, float frequency = 1f, int seed = 0)
    {
        float value = Fbm(x, y, octaves, frequency, seed);
        return 1f - MathF.Abs((value * 2f) - 1f);
    }

    /// <summary>Catmull-Rom interpolation between four control points.</summary>
    public static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return 0.5f * (
            (2f * p1) +
            ((-p0 + p2) * t) +
            (((2f * p0) - (5f * p1) + (4f * p2) - p3) * t2) +
            ((-p0 + (3f * p1) - (3f * p2) + p3) * t3));
    }
}
