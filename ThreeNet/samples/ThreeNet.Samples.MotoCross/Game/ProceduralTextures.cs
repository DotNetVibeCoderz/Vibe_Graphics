using System.Numerics;
using ThreeNet;

namespace MotoCross.Game;

/// <summary>
/// Every texture in the game is generated at start up, so the sample ships no
/// binary assets. Each generator returns a tiling sRGB colour map, and the
/// height based ones also hand back a matching tangent space normal map.
/// </summary>
public static class ProceduralTextures
{
    public const int Size = 256;

    /// <summary>Dry motocross dirt with ruts, gravel and tyre marks.</summary>
    public static (Texture Color, Texture Normal) Dirt(Scene scene)
    {
        return Build(scene, (u, v) =>
        {
            float grain = Noise.Fbm(u * 26f, v * 26f, 4, 1f, 3);
            float rut = MathF.Sin((v * MathF.Tau * 2f) + (Noise.Value(u * 4f, v * 4f, 9) * 1.4f)) * 0.5f;
            float gravel = Noise.Ridge(u * 60f, v * 60f, 2, 1f, 17);
            float height = (grain * 0.6f) + (rut * 0.25f) + (gravel * 0.15f);
            Vector3 dry = new(0.42f, 0.30f, 0.20f);
            Vector3 damp = new(0.24f, 0.17f, 0.12f);
            Vector3 color = Vector3.Lerp(damp, dry, Math.Clamp((grain * 1.3f) + (gravel * 0.3f), 0f, 1f));
            return (color, height);
        });
    }

    /// <summary>Meadow grass with patches of dry stalks.</summary>
    public static (Texture Color, Texture Normal) Grass(Scene scene)
    {
        return Build(scene, (u, v) =>
        {
            float blades = Noise.Fbm(u * 90f, v * 90f, 3, 1f, 5);
            float clumps = Noise.Fbm(u * 8f, v * 8f, 3, 1f, 13);
            float dryness = Math.Clamp((clumps * 1.4f) - 0.35f, 0f, 1f);
            Vector3 green = new(0.11f, 0.26f, 0.09f);
            Vector3 lush = new(0.16f, 0.38f, 0.12f);
            Vector3 straw = new(0.40f, 0.36f, 0.16f);
            Vector3 color = Vector3.Lerp(Vector3.Lerp(green, lush, blades), straw, dryness * 0.7f);
            return (color, (blades * 0.7f) + (clumps * 0.3f));
        });
    }

    /// <summary>Weathered concrete for kerbs, ramps and the start gate.</summary>
    public static (Texture Color, Texture Normal) Concrete(Scene scene)
    {
        return Build(scene, (u, v) =>
        {
            float pores = Noise.Fbm(u * 70f, v * 70f, 3, 1f, 29);
            float stain = Noise.Fbm(u * 6f, v * 6f, 3, 1f, 31);
            float grey = 0.45f + (pores * 0.18f) - (stain * 0.12f);
            return (new Vector3(grey, grey * 0.99f, grey * 0.96f), pores);
        });
    }

    /// <summary>Checkerboard for banners, the finish line and marker flags.</summary>
    public static Texture Checker(Scene scene, Vector3 a, Vector3 b, int squares = 8)
    {
        byte[] pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                bool odd = (((x * squares / Size) + (y * squares / Size)) & 1) == 1;
                Vector3 color = odd ? a : b;
                Write(pixels, x, y, color, 1f);
            }
        }

        Texture texture = scene.CreateTexture(Size, Size, pixels);
        texture.SetSampler(WrapMode.Repeat, WrapMode.Repeat, linearFilter: true, mipmaps: true, anisotropy: 8);
        return texture;
    }

    /// <summary>Hazard stripes used on the safety fences and jump lips.</summary>
    public static Texture Hazard(Scene scene, Vector3 a, Vector3 b)
    {
        byte[] pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                bool odd = (((x + y) / 32) & 1) == 1;
                float wear = Noise.Fbm(x * 0.08f, y * 0.08f, 3, 1f, 41) * 0.25f;
                Vector3 color = Vector3.Lerp(odd ? a : b, new Vector3(0.3f, 0.28f, 0.26f), wear);
                Write(pixels, x, y, color, 1f);
            }
        }

        Texture texture = scene.CreateTexture(Size, Size, pixels);
        texture.SetSampler(WrapMode.Repeat, WrapMode.Repeat, linearFilter: true, mipmaps: true, anisotropy: 8);
        return texture;
    }

    /// <summary>Soft radial blob used for dust puffs and light glows.</summary>
    public static Texture RadialPuff(Scene scene, Vector3 color)
    {
        byte[] pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                float dx = ((x / (float)Size) - 0.5f) * 2f;
                float dy = ((y / (float)Size) - 0.5f) * 2f;
                float radius = MathF.Sqrt((dx * dx) + (dy * dy));
                float noise = Noise.Fbm(x * 0.05f, y * 0.05f, 3, 1f, 53);
                float alpha = Math.Clamp(1f - radius, 0f, 1f);
                alpha = alpha * alpha * (0.6f + (noise * 0.6f));
                Write(pixels, x, y, color, alpha);
            }
        }

        Texture texture = scene.CreateTexture(Size, Size, pixels);
        texture.SetSampler(WrapMode.ClampToEdge, WrapMode.ClampToEdge, linearFilter: true, mipmaps: true, anisotropy: 4);
        return texture;
    }

    private static (Texture Color, Texture Normal) Build(Scene scene, Func<float, float, (Vector3 Color, float Height)> sample)
    {
        byte[] color = new byte[Size * Size * 4];
        float[] height = new float[Size * Size];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                (Vector3 rgb, float h) = sample(x / (float)Size, y / (float)Size);
                Write(color, x, y, rgb, 1f);
                height[(y * Size) + x] = h;
            }
        }

        Texture colorTexture = scene.CreateTexture(Size, Size, color);
        colorTexture.SetSampler(WrapMode.Repeat, WrapMode.Repeat, linearFilter: true, mipmaps: true, anisotropy: 8);
        Texture normalTexture = NormalFromHeight(scene, height);
        return (colorTexture, normalTexture);
    }

    /// <summary>Sobel filtered height field turned into a tangent space normal map.</summary>
    private static Texture NormalFromHeight(Scene scene, float[] height, float strength = 2.4f)
    {
        byte[] pixels = new byte[Size * Size * 4];
        float At(int x, int y) => height[(((y + Size) % Size) * Size) + ((x + Size) % Size)];

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                float dx = At(x + 1, y) - At(x - 1, y);
                float dy = At(x, y + 1) - At(x, y - 1);
                Vector3 normal = Vector3.Normalize(new Vector3(-dx * strength, -dy * strength, 1f));
                int index = ((y * Size) + x) * 4;
                pixels[index] = (byte)((normal.X * 0.5f + 0.5f) * 255f);
                pixels[index + 1] = (byte)((normal.Y * 0.5f + 0.5f) * 255f);
                pixels[index + 2] = (byte)((normal.Z * 0.5f + 0.5f) * 255f);
                pixels[index + 3] = 255;
            }
        }

        Texture texture = scene.CreateTexture(Size, Size, pixels, TextureFormat.Rgba8Unorm);
        texture.SetSampler(WrapMode.Repeat, WrapMode.Repeat, linearFilter: true, mipmaps: true, anisotropy: 8);
        return texture;
    }

    private static void Write(byte[] pixels, int x, int y, Vector3 color, float alpha)
    {
        int index = ((y * Size) + x) * 4;
        pixels[index] = ToByte(color.X);
        pixels[index + 1] = ToByte(color.Y);
        pixels[index + 2] = ToByte(color.Z);
        // Alpha is never gamma encoded.
        pixels[index + 3] = (byte)MathF.Round(Math.Clamp(alpha, 0f, 1f) * 255f);
    }

    // Textures are uploaded as sRGB, so the linear values computed above are
    // encoded back to sRGB here.
    private static byte ToByte(float linear)
    {
        float clamped = Math.Clamp(linear, 0f, 1f);
        float srgb = clamped <= 0.0031308f
            ? clamped * 12.92f
            : (1.055f * MathF.Pow(clamped, 1f / 2.4f)) - 0.055f;
        return (byte)MathF.Round(srgb * 255f);
    }
}
