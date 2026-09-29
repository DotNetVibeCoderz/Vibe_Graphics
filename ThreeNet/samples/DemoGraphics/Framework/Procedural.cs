using System.Numerics;
using ThreeNet;
using ThreeNet.Interop;

namespace DemoGraphics.Framework;

/// <summary>
/// Deterministic value noise. Every scene that needs a landscape, a scatter or a
/// texture uses this, so two runs of the same benchmark get the same world.
/// </summary>
public static class Noise
{
    /// <summary>Hash of two integers to 0..1.</summary>
    public static float Hash(int x, int y)
    {
        unchecked
        {
            int hash = (x * 374761393) + (y * 668265263) + 1442695040;
            hash = (hash ^ (hash >> 13)) * 1274126177;
            return ((hash ^ (hash >> 16)) & 0x7FFFFFF) / (float)0x7FFFFFF;
        }
    }

    /// <summary>Smooth value noise at a point, in 0..1.</summary>
    public static float Value(float x, float y)
    {
        int xi = (int)MathF.Floor(x);
        int yi = (int)MathF.Floor(y);
        float xf = x - xi;
        float yf = y - yi;
        // Smoothstep weights keep the derivative continuous across cells.
        float sx = xf * xf * (3f - (2f * xf));
        float sy = yf * yf * (3f - (2f * yf));
        float a = float.Lerp(Hash(xi, yi), Hash(xi + 1, yi), sx);
        float b = float.Lerp(Hash(xi, yi + 1), Hash(xi + 1, yi + 1), sx);
        return float.Lerp(a, b, sy);
    }

    /// <summary>Fractal sum of value noise, in 0..1.</summary>
    public static float Fbm(float x, float y, int octaves = 5, float gain = 0.5f, float lacunarity = 2f)
    {
        float sum = 0f;
        float amplitude = 1f;
        float total = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += Value(x, y) * amplitude;
            total += amplitude;
            amplitude *= gain;
            x *= lacunarity;
            y *= lacunarity;
        }

        return total <= 0f ? 0f : sum / total;
    }

    /// <summary>Ridged noise, for mountains and dunes.</summary>
    public static float Ridged(float x, float y, int octaves = 5)
    {
        float sum = 0f;
        float amplitude = 1f;
        float total = 0f;
        for (int i = 0; i < octaves; i++)
        {
            float ridge = 1f - MathF.Abs((Value(x, y) * 2f) - 1f);
            sum += ridge * ridge * amplitude;
            total += amplitude;
            amplitude *= 0.5f;
            x *= 2.03f;
            y *= 2.03f;
        }

        return total <= 0f ? 0f : sum / total;
    }
}

/// <summary>
/// Collects triangles into one vertex and index buffer. Merging a whole prop
/// into a single geometry keeps it at one draw call, which is what makes ten
/// thousand trees affordable.
/// </summary>
public sealed class MeshBuilder
{
    private readonly List<Vertex> _vertices = [];
    private readonly List<uint> _indices = [];

    public int VertexCount => _vertices.Count;

    public int TriangleCount => _indices.Count / 3;

    public void AddTriangle(Vertex a, Vertex b, Vertex c)
    {
        uint start = (uint)_vertices.Count;
        _vertices.Add(a);
        _vertices.Add(b);
        _vertices.Add(c);
        _indices.Add(start);
        _indices.Add(start + 1);
        _indices.Add(start + 2);
    }

    public void AddQuad(Vertex a, Vertex b, Vertex c, Vertex d)
    {
        uint start = (uint)_vertices.Count;
        _vertices.Add(a);
        _vertices.Add(b);
        _vertices.Add(c);
        _vertices.Add(d);
        _indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
    }

    /// <summary>A cylinder or truncated cone along +Y, open at both ends.</summary>
    public void AddTube(Vector3 baseCenter, float height, float bottomRadius, float topRadius, int sides, Vector2 uv)
    {
        for (int i = 0; i < sides; i++)
        {
            float a0 = MathF.Tau * i / sides;
            float a1 = MathF.Tau * (i + 1) / sides;
            Vector3 d0 = new(MathF.Sin(a0), 0f, MathF.Cos(a0));
            Vector3 d1 = new(MathF.Sin(a1), 0f, MathF.Cos(a1));
            Vector3 up = new(0f, height, 0f);
            // The slope tilts the normal, so a cone is not lit like a cylinder.
            float slope = (bottomRadius - topRadius) / MathF.Max(height, 1e-3f);
            Vector3 n0 = Vector3.Normalize(new Vector3(d0.X, slope, d0.Z));
            Vector3 n1 = Vector3.Normalize(new Vector3(d1.X, slope, d1.Z));
            AddQuad(
                new Vertex(baseCenter + (d0 * bottomRadius), n0, uv),
                new Vertex(baseCenter + (d1 * bottomRadius), n1, uv),
                new Vertex(baseCenter + up + (d1 * topRadius), n1, uv),
                new Vertex(baseCenter + up + (d0 * topRadius), n0, uv));
        }
    }

    /// <summary>A cone with its apex up, closed by its own sides.</summary>
    public void AddCone(Vector3 baseCenter, float height, float radius, int sides, Vector2 uv)
    {
        Vector3 apex = baseCenter + new Vector3(0f, height, 0f);
        float slope = radius / MathF.Max(height, 1e-3f);
        for (int i = 0; i < sides; i++)
        {
            float a0 = MathF.Tau * i / sides;
            float a1 = MathF.Tau * (i + 1) / sides;
            Vector3 d0 = new(MathF.Sin(a0), 0f, MathF.Cos(a0));
            Vector3 d1 = new(MathF.Sin(a1), 0f, MathF.Cos(a1));
            Vector3 n0 = Vector3.Normalize(new Vector3(d0.X, slope, d0.Z));
            Vector3 n1 = Vector3.Normalize(new Vector3(d1.X, slope, d1.Z));
            AddTriangle(
                new Vertex(baseCenter + (d0 * radius), n0, uv),
                new Vertex(baseCenter + (d1 * radius), n1, uv),
                new Vertex(apex, Vector3.Normalize(n0 + n1), uv));
        }

        // Cap, so a cone seen from below is not hollow.
        for (int i = 0; i < sides; i++)
        {
            float a0 = MathF.Tau * i / sides;
            float a1 = MathF.Tau * (i + 1) / sides;
            AddTriangle(
                new Vertex(baseCenter, -Vector3.UnitY, uv),
                new Vertex(baseCenter + (new Vector3(MathF.Sin(a1), 0f, MathF.Cos(a1)) * radius), -Vector3.UnitY, uv),
                new Vertex(baseCenter + (new Vector3(MathF.Sin(a0), 0f, MathF.Cos(a0)) * radius), -Vector3.UnitY, uv));
        }
    }

    /// <summary>An axis aligned box.</summary>
    public void AddBox(Vector3 center, Vector3 size, Vector2 uv)
    {
        Vector3 h = size * 0.5f;
        void Face(Vector3 normal, Vector3 right, Vector3 up)
        {
            Vector3 origin = center + (normal * Vector3.Abs(h));
            // Wound so the face points along its normal: with back face culling
            // the wrong order makes a box invisible from outside.
            AddQuad(
                new Vertex(origin - right - up, normal, uv),
                new Vertex(origin - right + up, normal, uv),
                new Vertex(origin + right + up, normal, uv),
                new Vertex(origin + right - up, normal, uv));
        }

        Face(Vector3.UnitX, Vector3.UnitZ * h.Z, Vector3.UnitY * h.Y);
        Face(-Vector3.UnitX, -Vector3.UnitZ * h.Z, Vector3.UnitY * h.Y);
        Face(Vector3.UnitY, Vector3.UnitX * h.X, -Vector3.UnitZ * h.Z);
        Face(-Vector3.UnitY, Vector3.UnitX * h.X, Vector3.UnitZ * h.Z);
        Face(Vector3.UnitZ, -Vector3.UnitX * h.X, Vector3.UnitY * h.Y);
        Face(-Vector3.UnitZ, Vector3.UnitX * h.X, Vector3.UnitY * h.Y);
    }

    public Geometry Build(Scene scene) => scene.CreateGeometry(_vertices.ToArray(), _indices.ToArray());

    public Vertex[] Vertices => _vertices.ToArray();

    public uint[] Indices => _indices.ToArray();
}

/// <summary>Meshes and textures the scenes generate instead of loading.</summary>
public static class Procedural
{
    /// <summary>
    /// A grid on the XZ plane, centred on the origin and facing up. Both the
    /// water and the terrain use this: object space equals world space here, so a
    /// vertex shader and a surface shader can evaluate the same wave.
    /// </summary>
    public static Geometry Ground(
        Scene scene,
        float size,
        int segments,
        Func<float, float, float>? height = null,
        float uvScale = 1f)
    {
        segments = Math.Max(1, segments);
        int stride = segments + 1;
        Vertex[] vertices = new Vertex[stride * stride];
        float step = size / segments;
        float half = size * 0.5f;

        for (int z = 0; z < stride; z++)
        {
            for (int x = 0; x < stride; x++)
            {
                float wx = (x * step) - half;
                float wz = (z * step) - half;
                float wy = height?.Invoke(wx, wz) ?? 0f;
                vertices[(z * stride) + x] = new Vertex(
                    new Vector3(wx, wy, wz),
                    Vector3.UnitY,
                    new Vector2(x / (float)segments * uvScale, z / (float)segments * uvScale));
            }
        }

        uint[] indices = new uint[segments * segments * 6];
        int i = 0;
        for (int z = 0; z < segments; z++)
        {
            for (int x = 0; x < segments; x++)
            {
                uint a = (uint)((z * stride) + x);
                uint b = a + 1;
                uint c = a + (uint)stride;
                uint d = c + 1;
                indices[i++] = a;
                indices[i++] = c;
                indices[i++] = b;
                indices[i++] = b;
                indices[i++] = c;
                indices[i++] = d;
            }
        }

        Geometry geometry = scene.CreateGeometry(vertices, indices);
        if (height is not null)
        {
            geometry.ComputeNormals();
        }

        return geometry;
    }

    /// <summary>
    /// One tree as a single geometry: trunk and canopy in one draw call, with
    /// <c>uv.x</c> marking which is which and <c>uv.y</c> carrying a sway phase
    /// so a forest built from a handful of variants does not move as one block.
    /// </summary>
    public static Geometry Tree(Scene scene, float height, float phase, bool conifer)
    {
        MeshBuilder builder = new();
        Vector2 bark = new(0f, phase);
        Vector2 leaf = new(1f, phase);
        float trunkHeight = conifer ? height * 0.34f : height * 0.52f;
        builder.AddTube(Vector3.Zero, trunkHeight, height * 0.045f, height * 0.028f, 7, bark);

        if (conifer)
        {
            for (int i = 0; i < 4; i++)
            {
                float t = i / 3f;
                float y = trunkHeight * 0.55f + (t * height * 0.5f);
                float radius = float.Lerp(height * 0.26f, height * 0.08f, t);
                float skirt = float.Lerp(height * 0.3f, height * 0.18f, t);
                builder.AddCone(new Vector3(0f, y, 0f), skirt, radius, 9, leaf);
            }
        }
        else
        {
            // A broadleaf canopy: three overlapping blobs, each a squat cone pair.
            for (int i = 0; i < 3; i++)
            {
                float angle = MathF.Tau * i / 3f;
                Vector3 offset = new(MathF.Sin(angle) * height * 0.11f, trunkHeight * 0.92f, MathF.Cos(angle) * height * 0.11f);
                float radius = height * 0.26f;
                builder.AddCone(offset, height * 0.42f, radius, 9, leaf);
            }
        }

        return builder.Build(scene);
    }

    /// <summary>A checkerboard, useful for reading scale and filtering.</summary>
    public static Texture Checker(Scene scene, int size, int cells, uint colorA, uint colorB)
    {
        byte[] pixels = new byte[size * size * 4];
        (byte r0, byte g0, byte b0) = Bytes(colorA);
        (byte r1, byte g1, byte b1) = Bytes(colorB);
        int cell = Math.Max(1, size / Math.Max(1, cells));
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool even = ((x / cell) + (y / cell)) % 2 == 0;
                int i = ((y * size) + x) * 4;
                pixels[i] = even ? r0 : r1;
                pixels[i + 1] = even ? g0 : g1;
                pixels[i + 2] = even ? b0 : b1;
                pixels[i + 3] = 255;
            }
        }

        return scene.CreateTexture(size, size, pixels);
    }

    /// <summary>Mottled noise, for surfaces that should not look moulded.</summary>
    public static Texture NoiseTexture(Scene scene, int size, float scale, uint dark, uint light)
    {
        byte[] pixels = new byte[size * size * 4];
        (byte r0, byte g0, byte b0) = Bytes(dark);
        (byte r1, byte g1, byte b1) = Bytes(light);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float n = Noise.Fbm(x / (float)size * scale, y / (float)size * scale, 5);
                int i = ((y * size) + x) * 4;
                pixels[i] = (byte)float.Lerp(r0, r1, n);
                pixels[i + 1] = (byte)float.Lerp(g0, g1, n);
                pixels[i + 2] = (byte)float.Lerp(b0, b1, n);
                pixels[i + 3] = 255;
            }
        }

        return scene.CreateTexture(size, size, pixels);
    }

    /// <summary>
    /// A normal map from the same noise, so a material can show what a normal
    /// map does without shipping an image.
    /// </summary>
    public static Texture NoiseNormals(Scene scene, int size, float scale, float strength)
    {
        byte[] pixels = new byte[size * size * 4];
        float Height(int x, int y) => Noise.Fbm(x / (float)size * scale, y / (float)size * scale, 4);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = Height(x + 1, y) - Height(x - 1, y);
                float dy = Height(x, y + 1) - Height(x, y - 1);
                Vector3 normal = Vector3.Normalize(new Vector3(-dx * strength * size * 0.02f, -dy * strength * size * 0.02f, 1f));
                int i = ((y * size) + x) * 4;
                pixels[i] = (byte)((normal.X * 0.5f + 0.5f) * 255f);
                pixels[i + 1] = (byte)((normal.Y * 0.5f + 0.5f) * 255f);
                pixels[i + 2] = (byte)((normal.Z * 0.5f + 0.5f) * 255f);
                pixels[i + 3] = 255;
            }
        }

        // Normals are data, not colour, so they must not be read as sRGB.
        return scene.CreateTexture(size, size, pixels, TextureFormat.Rgba8Unorm);
    }

    private static (byte R, byte G, byte B) Bytes(uint hex) =>
        ((byte)((hex >> 16) & 0xFF), (byte)((hex >> 8) & 0xFF), (byte)(hex & 0xFF));
}
