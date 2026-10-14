using System.Numerics;

namespace ThreeNet;

/// <summary>
/// The light arriving at a point from every direction, stored as nine numbers
/// per colour channel.
/// </summary>
/// <remarks>
/// <para>
/// Bake one with <see cref="Renderer.BakeLightProbe"/> and hand it to
/// <see cref="Scene.LightProbe"/>. What it buys over a single ambient colour is
/// direction: a room with a window on one side lights the side of an object
/// facing the window and leaves the other side dark, which no scalar ambient
/// term can do.
/// </para>
/// <para>
/// Nine coefficients - the first three bands of spherical harmonics - is enough
/// to reconstruct the irradiance of any environment to within about a percent,
/// because a matte surface gathers light over a cosine lobe so broad that it
/// erases everything sharper. The sharp half is what a cube probe is for.
/// </para>
/// </remarks>
public sealed class LightProbe
{
    /// <summary>Coefficients in three bands: 1 + 3 + 5.</summary>
    public const int CoefficientCount = 9;

    private readonly Vector3[] _coefficients;

    /// <summary>A probe that contributes nothing.</summary>
    public LightProbe() => _coefficients = new Vector3[CoefficientCount];

    /// <summary>Wraps nine coefficients, which must be exactly nine.</summary>
    public LightProbe(ReadOnlySpan<Vector3> coefficients)
        : this()
    {
        if (coefficients.Length != CoefficientCount)
        {
            throw new ArgumentException(
                $"a light probe holds {CoefficientCount} coefficients, not {coefficients.Length}",
                nameof(coefficients));
        }

        coefficients.CopyTo(_coefficients);
    }

    /// <summary>The raw coefficients, in the usual band order.</summary>
    public ReadOnlySpan<Vector3> Coefficients => _coefficients;

    internal Vector3[] Storage => _coefficients;

    /// <summary>A probe holding the same radiance from every direction.</summary>
    public static LightProbe Uniform(Vector3 radiance)
    {
        LightProbe probe = new();
        // Only the constant band is non-zero, and it integrates to 4 pi.
        probe._coefficients[0] = radiance * (0.282095f * 4f * MathF.PI);
        return probe;
    }

    /// <summary>True when every coefficient is zero, so the probe adds no light.</summary>
    public bool IsDark
    {
        get
        {
            foreach (Vector3 coefficient in _coefficients)
            {
                if (coefficient != Vector3.Zero)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// What a matte surface facing <paramref name="normal"/> reflects, per unit
    /// albedo - the same value the shader computes, for when game code needs to
    /// ask rather than draw.
    /// </summary>
    public Vector3 Irradiance(Vector3 normal)
    {
        Vector3 n = Vector3.Normalize(normal);
        float x = n.X;
        float y = n.Y;
        float z = n.Z;

        // The band weights are the cosine lobe a matte surface gathers over,
        // each already divided by the pi that turns irradiance into reflected
        // radiance.
        const float band1 = 2f / 3f;
        const float band2 = 0.25f;
        Vector3 total = _coefficients[0] * 0.282095f;
        total += _coefficients[1] * (0.488603f * y * band1);
        total += _coefficients[2] * (0.488603f * z * band1);
        total += _coefficients[3] * (0.488603f * x * band1);
        total += _coefficients[4] * (1.092548f * x * y * band2);
        total += _coefficients[5] * (1.092548f * y * z * band2);
        total += _coefficients[6] * (0.315392f * ((3f * z * z) - 1f) * band2);
        total += _coefficients[7] * (1.092548f * x * z * band2);
        total += _coefficients[8] * (0.546274f * ((x * x) - (y * y)) * band2);

        // A three band fit of a dark room with a bright window in it can ring
        // below zero. Negative light is not light.
        return Vector3.Max(total, Vector3.Zero);
    }

    /// <summary>Every coefficient scaled, for exposure or for turning it down.</summary>
    public LightProbe Scaled(float factor)
    {
        LightProbe probe = new();
        for (int i = 0; i < CoefficientCount; i++)
        {
            probe._coefficients[i] = _coefficients[i] * factor;
        }

        return probe;
    }
}
