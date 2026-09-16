using System.Numerics;
using ThreeNet.Interop;

namespace ThreeNet;

/// <summary>Entry points for the native core itself: version, logging, checks.</summary>
public static class ThreeNetRuntime
{
    /// <summary>ABI revision the managed bindings were generated against.</summary>
    public const uint ExpectedAbiVersion = 2;

    /// <summary>Semantic version of the loaded native core.</summary>
    public static unsafe string NativeVersion => NativeError.ReadString((buffer, capacity) =>
        NativeMethods.tn_version((byte*)buffer, capacity));

    /// <summary>ABI revision reported by the loaded native core.</summary>
    public static uint NativeAbiVersion => NativeMethods.tn_abi_version();

    /// <summary>
    /// Loads the native core and verifies that its ABI matches these bindings.
    /// Calling it is optional: the library loads lazily on first use.
    /// </summary>
    /// <exception cref="ThreeNetException">The native ABI does not match.</exception>
    public static void Initialize(LogLevel logLevel = LogLevel.Warning)
    {
        uint abi = NativeAbiVersion;
        if (abi != ExpectedAbiVersion)
        {
            throw new ThreeNetException(
                $"the native core reports ABI {abi} but these bindings expect {ExpectedAbiVersion}; " +
                "rebuild the Rust core or update the ThreeNet package");
        }

        NativeMethods.tn_init_logging((uint)logLevel);
    }
}

/// <summary>Small helpers that keep sample and application code readable.</summary>
public static class MathHelpers
{
    /// <summary>Degrees to radians.</summary>
    public static float ToRadians(this float degrees) => degrees * (MathF.PI / 180f);

    /// <summary>Radians to degrees.</summary>
    public static float ToDegrees(this float radians) => radians * (180f / MathF.PI);

    /// <summary>Builds a linear RGBA colour from 0..1 components.</summary>
    public static Vector4 Rgba(float r, float g, float b, float a = 1f) => new(r, g, b, a);

    /// <summary>
    /// Converts an sRGB hex colour (<c>0xRRGGBB</c>) to the linear RGBA the
    /// renderer works in.
    /// </summary>
    public static Vector4 FromHex(uint hex, float alpha = 1f)
    {
        float r = ((hex >> 16) & 0xFF) / 255f;
        float g = ((hex >> 8) & 0xFF) / 255f;
        float b = (hex & 0xFF) / 255f;
        return new Vector4(SrgbToLinear(r), SrgbToLinear(g), SrgbToLinear(b), alpha);
    }

    /// <summary>Standard sRGB electro-optical transfer function.</summary>
    public static float SrgbToLinear(float channel) =>
        channel <= 0.04045f ? channel / 12.92f : MathF.Pow((channel + 0.055f) / 1.055f, 2.4f);

    /// <summary>Inverse of <see cref="SrgbToLinear"/>.</summary>
    public static float LinearToSrgb(float channel) =>
        channel <= 0.0031308f ? channel * 12.92f : (1.055f * MathF.Pow(channel, 1f / 2.4f)) - 0.055f;
}

/// <summary>Ready made linear colours, matching the common CSS names.</summary>
public static class Colors
{
    public static Vector4 White => new(1f, 1f, 1f, 1f);
    public static Vector4 Black => new(0f, 0f, 0f, 1f);
    public static Vector4 Red => MathHelpers.FromHex(0xE5484D);
    public static Vector4 Green => MathHelpers.FromHex(0x30A46C);
    public static Vector4 Blue => MathHelpers.FromHex(0x3E63DD);
    public static Vector4 Yellow => MathHelpers.FromHex(0xFFC53D);
    public static Vector4 Orange => MathHelpers.FromHex(0xF76B15);
    public static Vector4 Purple => MathHelpers.FromHex(0x8E4EC6);
    public static Vector4 Cyan => MathHelpers.FromHex(0x00A2C7);
    public static Vector4 Gray => MathHelpers.FromHex(0x8B8D98);
}
