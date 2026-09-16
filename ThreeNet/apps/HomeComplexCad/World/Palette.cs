using System.Numerics;
using ThreeNet;

namespace HomeComplexCad.World;

/// <summary>Deterministic value noise used by the texture generators.</summary>
internal static class Noise
{
    private static float Hash(int x, int y, int seed)
    {
        int h = (x * 374761393) + (y * 668265263) + (seed * 362437);
        h = (h ^ (h >> 13)) * 1274126177;
        return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
    }

    public static float Value(float x, float y, int seed = 0)
    {
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        float fx = x - x0;
        float fy = y - y0;
        fx = fx * fx * (3f - (2f * fx));
        fy = fy * fy * (3f - (2f * fy));
        float a = Hash(x0, y0, seed);
        float b = Hash(x0 + 1, y0, seed);
        float c = Hash(x0, y0 + 1, seed);
        float d = Hash(x0 + 1, y0 + 1, seed);
        return float.Lerp(float.Lerp(a, b, fx), float.Lerp(c, d, fx), fy);
    }

    public static float Fbm(float x, float y, int octaves = 4, int seed = 0)
    {
        float sum = 0f;
        float amplitude = 1f;
        float total = 0f;
        float frequency = 1f;
        for (int i = 0; i < octaves; i++)
        {
            sum += Value(x * frequency, y * frequency, seed + i) * amplitude;
            total += amplitude;
            amplitude *= 0.5f;
            frequency *= 2f;
        }

        return sum / total;
    }
}

/// <summary>
/// Every surface finish in the estate, generated at start up: asphalt, paving,
/// brick, roof tiles, stucco, parquet, ceramic, marble, lawn, water and the
/// fabrics and metals of the furniture. Nothing is loaded from disk.
/// </summary>
public sealed class Palette
{
    private const int Size = 256;
    private readonly Scene _scene;

    public Palette(Scene scene)
    {
        _scene = scene;

        Asphalt = Surface(
            (u, v) =>
            {
                float grain = Noise.Fbm(u * 120f, v * 120f, 4, 3);
                float patch = Noise.Fbm(u * 9f, v * 9f, 3, 7);
                float grey = 0.055f + (grain * 0.05f) + (patch * 0.02f);
                return (new Vector3(grey, grey * 1.02f, grey * 1.06f), grain);
            },
            roughness: 0.92f,
            uvScale: new Vector2(18f, 18f));

        Paving = Surface(
            (u, v) =>
            {
                // 4x4 pavers with a darker joint.
                float jx = MathF.Abs(((u * 4f) % 1f) - 0.5f);
                float jy = MathF.Abs(((v * 4f) % 1f) - 0.5f);
                float joint = MathF.Min(jx, jy) < 0.45f ? 1f : 0.35f;
                float speck = Noise.Fbm(u * 90f, v * 90f, 3, 11);
                Vector3 color = new Vector3(0.62f, 0.60f, 0.56f) * (0.7f + (speck * 0.25f)) * joint;
                return (color, joint * 0.8f);
            },
            roughness: 0.85f,
            uvScale: new Vector2(6f, 6f));

        Brick = Surface(
            (u, v) =>
            {
                // Running bond: every other course is offset by half a brick.
                int row = (int)(v * 16f);
                float offset = (row % 2 == 0) ? 0f : 0.5f;
                float bx = ((u * 8f) + offset) % 1f;
                float by = (v * 16f) % 1f;
                bool mortar = bx < 0.05f || by < 0.09f;
                float wear = Noise.Fbm(u * 60f, v * 60f, 3, 17);
                Vector3 clay = Vector3.Lerp(new Vector3(0.36f, 0.14f, 0.10f), new Vector3(0.48f, 0.23f, 0.16f), wear);
                Vector3 color = mortar ? new Vector3(0.62f, 0.60f, 0.57f) : clay;
                return (color, mortar ? 0.2f : 0.75f + (wear * 0.25f));
            },
            roughness: 0.88f,
            uvScale: new Vector2(3f, 2f));

        RoofTile = Surface(
            (u, v) =>
            {
                // Interlocking pantiles: a sine ripple across, courses down.
                float ripple = MathF.Sin(u * MathF.Tau * 12f) * 0.5f + 0.5f;
                float course = ((v * 10f) % 1f) < 0.12f ? 0.55f : 1f;
                float wear = Noise.Fbm(u * 40f, v * 40f, 3, 23);
                Vector3 color = Vector3.Lerp(new Vector3(0.28f, 0.08f, 0.05f), new Vector3(0.42f, 0.15f, 0.09f), ripple) * course;
                color = Vector3.Lerp(color, new Vector3(0.24f, 0.2f, 0.18f), wear * 0.25f);
                return (color, (ripple * 0.7f) + (course * 0.3f));
            },
            roughness: 0.82f,
            uvScale: new Vector2(4f, 4f));

        Stucco = Surface(
            (u, v) =>
            {
                float grain = Noise.Fbm(u * 150f, v * 150f, 4, 29);
                float stain = Noise.Fbm(u * 5f, v * 5f, 3, 31);
                float tone = 0.72f + (grain * 0.08f) - (stain * 0.05f);
                return (new Vector3(tone, tone * 0.99f, tone * 0.95f), grain);
            },
            roughness: 0.9f,
            uvScale: new Vector2(2.5f, 2.5f));

        Parquet = Surface(
            (u, v) =>
            {
                // Plank floor: 8 planks across with staggered ends.
                int plank = (int)(v * 8f);
                float shift = ((plank * 0.37f) % 1f);
                float along = ((u * 2f) + shift) % 1f;
                bool gap = ((v * 8f) % 1f) < 0.04f || along < 0.012f;
                float grain = Noise.Fbm((u * 120f) + (plank * 13f), v * 18f, 4, 37);
                Vector3 wood = Vector3.Lerp(new Vector3(0.24f, 0.13f, 0.06f), new Vector3(0.45f, 0.27f, 0.13f), grain);
                return (gap ? wood * 0.45f : wood, grain);
            },
            roughness: 0.35f,
            uvScale: new Vector2(3f, 3f));

        Ceramic = Surface(
            (u, v) =>
            {
                float jx = ((u * 6f) % 1f);
                float jy = ((v * 6f) % 1f);
                bool grout = jx < 0.03f || jy < 0.03f;
                float mottle = Noise.Fbm(u * 30f, v * 30f, 3, 41);
                Vector3 tile = Vector3.Lerp(new Vector3(0.80f, 0.80f, 0.78f), new Vector3(0.88f, 0.89f, 0.9f), mottle);
                return (grout ? new Vector3(0.55f, 0.55f, 0.54f) : tile, grout ? 0.1f : 0.9f);
            },
            roughness: 0.18f,
            uvScale: new Vector2(4f, 4f));

        Marble = Surface(
            (u, v) =>
            {
                float veins = MathF.Sin((u * 6f) + (Noise.Fbm(u * 4f, v * 4f, 4, 43) * 9f));
                float value = 0.82f + (veins * 0.08f);
                Vector3 color = new Vector3(value, value * 0.99f, value * 0.97f);
                color = Vector3.Lerp(color, new Vector3(0.45f, 0.46f, 0.5f), MathF.Max(0f, veins - 0.86f) * 4f);
                return (color, veins * 0.5f + 0.5f);
            },
            roughness: 0.12f,
            uvScale: new Vector2(3f, 3f));

        Lawn = Surface(
            (u, v) =>
            {
                float blades = Noise.Fbm(u * 200f, v * 200f, 3, 47);
                float patches = Noise.Fbm(u * 12f, v * 12f, 3, 53);
                Vector3 color = Vector3.Lerp(new Vector3(0.07f, 0.22f, 0.06f), new Vector3(0.13f, 0.34f, 0.09f), (blades * 0.7f) + (patches * 0.3f));
                return (color, blades);
            },
            roughness: 0.95f,
            uvScale: new Vector2(40f, 40f));

        // --- plain materials -------------------------------------------------
        Concrete = Pbr(new Vector4(0.52f, 0.52f, 0.5f, 1f), 0f, 0.85f);
        WhiteWall = Pbr(new Vector4(0.86f, 0.86f, 0.84f, 1f), 0f, 0.8f);
        WarmWall = Pbr(new Vector4(0.80f, 0.74f, 0.64f, 1f), 0f, 0.8f);
        AccentWall = Pbr(new Vector4(0.24f, 0.32f, 0.38f, 1f), 0f, 0.7f);
        Timber = Pbr(new Vector4(0.30f, 0.18f, 0.09f, 1f), 0f, 0.6f);
        DarkTimber = Pbr(new Vector4(0.16f, 0.10f, 0.06f, 1f), 0f, 0.55f);
        Metal = Pbr(new Vector4(0.55f, 0.57f, 0.60f, 1f), 0.9f, 0.3f);
        DarkMetal = Pbr(new Vector4(0.13f, 0.14f, 0.16f, 1f), 0.8f, 0.45f);
        Chrome = Pbr(new Vector4(0.85f, 0.86f, 0.88f, 1f), 1f, 0.12f);
        Glass = _scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.35f, 0.5f, 0.6f, 0.30f), 0.1f, 0.05f) with
        {
            AlphaMode = AlphaMode.Blend,
            CullMode = CullMode.None,
            RenderOrder = 20,
        });
        Water = _scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.06f, 0.30f, 0.42f, 0.82f), 0.25f, 0.06f) with
        {
            AlphaMode = AlphaMode.Blend,
            RenderOrder = 15,
        });
        Fabric = Pbr(new Vector4(0.30f, 0.34f, 0.42f, 1f), 0f, 0.9f);
        FabricWarm = Pbr(new Vector4(0.55f, 0.36f, 0.24f, 1f), 0f, 0.9f);
        Linen = Pbr(new Vector4(0.88f, 0.87f, 0.83f, 1f), 0f, 0.85f);
        Foliage = Pbr(new Vector4(0.08f, 0.24f, 0.07f, 1f), 0f, 0.85f);
        FoliageLight = Pbr(new Vector4(0.13f, 0.33f, 0.11f, 1f), 0f, 0.85f);
        Bark = Pbr(new Vector4(0.17f, 0.11f, 0.07f, 1f), 0f, 0.95f);
        Terracotta = Pbr(new Vector4(0.45f, 0.20f, 0.12f, 1f), 0f, 0.8f);
        SignBlue = Pbr(new Vector4(0.08f, 0.24f, 0.52f, 1f), 0.2f, 0.4f);
        SignGreen = Pbr(new Vector4(0.06f, 0.32f, 0.18f, 1f), 0.2f, 0.4f);
        RoadLine = Pbr(new Vector4(0.88f, 0.88f, 0.82f, 1f), 0f, 0.7f);

        LampGlow = _scene.CreateMaterial(MaterialOptions.Basic(new Vector4(1f, 0.93f, 0.78f, 1f)) with
        {
            Emissive = new Vector3(1f, 0.92f, 0.75f),
            EmissiveIntensity = 5f,
        });
        WindowGlow = _scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.35f, 0.5f, 0.6f, 0.45f), 0.1f, 0.05f) with
        {
            AlphaMode = AlphaMode.Blend,
            CullMode = CullMode.None,
            Emissive = new Vector3(1f, 0.86f, 0.62f),
            EmissiveIntensity = 0f,
            RenderOrder = 20,
        });
        ScreenGlow = _scene.CreateMaterial(MaterialOptions.Basic(new Vector4(0.05f, 0.06f, 0.08f, 1f)) with
        {
            Emissive = new Vector3(0.25f, 0.45f, 0.75f),
            EmissiveIntensity = 1.5f,
        });
    }

    public Material Asphalt { get; }

    public Material Paving { get; }

    public Material Brick { get; }

    public Material RoofTile { get; }

    public Material Stucco { get; }

    public Material Parquet { get; }

    public Material Ceramic { get; }

    public Material Marble { get; }

    public Material Lawn { get; }

    public Material Concrete { get; }

    public Material WhiteWall { get; }

    public Material WarmWall { get; }

    public Material AccentWall { get; }

    public Material Timber { get; }

    public Material DarkTimber { get; }

    public Material Metal { get; }

    public Material DarkMetal { get; }

    public Material Chrome { get; }

    public Material Glass { get; }

    public Material Water { get; }

    public Material Fabric { get; }

    public Material FabricWarm { get; }

    public Material Linen { get; }

    public Material Foliage { get; }

    public Material FoliageLight { get; }

    public Material Bark { get; }

    public Material Terracotta { get; }

    public Material SignBlue { get; }

    public Material SignGreen { get; }

    public Material RoadLine { get; }

    /// <summary>Emissive lamp heads, dimmed during the day.</summary>
    public Material LampGlow { get; }

    /// <summary>Window glass that lights up from the inside after sunset.</summary>
    public Material WindowGlow { get; }

    public Material ScreenGlow { get; }

    private Material Pbr(Vector4 color, float metallic, float roughness) =>
        _scene.CreateMaterial(MaterialOptions.Pbr(color, metallic, roughness));

    /// <summary>Builds a colour + normal map pair and the material that uses them.</summary>
    private Material Surface(Func<float, float, (Vector3 Color, float Height)> sample, float roughness, Vector2 uvScale)
    {
        byte[] color = new byte[Size * Size * 4];
        float[] height = new float[Size * Size];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                (Vector3 rgb, float h) = sample(x / (float)Size, y / (float)Size);
                int index = ((y * Size) + x) * 4;
                color[index] = Encode(rgb.X);
                color[index + 1] = Encode(rgb.Y);
                color[index + 2] = Encode(rgb.Z);
                color[index + 3] = 255;
                height[(y * Size) + x] = h;
            }
        }

        Texture colorMap = _scene.CreateTexture(Size, Size, color);
        colorMap.SetSampler(WrapMode.Repeat, WrapMode.Repeat, linearFilter: true, mipmaps: true, anisotropy: 8);
        Texture normalMap = NormalFromHeight(height);

        return _scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 0f, roughness) with
        {
            BaseColorMap = colorMap,
            NormalMap = normalMap,
            NormalScale = 0.9f,
            UvScale = uvScale,
        });
    }

    private Texture NormalFromHeight(float[] height, float strength = 2.2f)
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

        Texture texture = _scene.CreateTexture(Size, Size, pixels, TextureFormat.Rgba8Unorm);
        texture.SetSampler(WrapMode.Repeat, WrapMode.Repeat, linearFilter: true, mipmaps: true, anisotropy: 8);
        return texture;
    }

    // Textures are uploaded as sRGB; the generators work in linear space.
    private static byte Encode(float linear)
    {
        float clamped = Math.Clamp(linear, 0f, 1f);
        float srgb = clamped <= 0.0031308f
            ? clamped * 12.92f
            : (1.055f * MathF.Pow(clamped, 1f / 2.4f)) - 0.055f;
        return (byte)MathF.Round(srgb * 255f);
    }
}
