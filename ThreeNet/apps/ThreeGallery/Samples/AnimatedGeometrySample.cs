using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;
using ThreeNet.Interop;

namespace ThreeGallery.Samples;

/// <summary>Rewrites vertex data every frame: a rippling height field.</summary>
public sealed class AnimatedGeometrySample : GallerySample
{
    private const int Resolution = 96;
    private const float Size = 12f;

    private Geometry _surface = null!;
    private Vertex[] _vertices = [];
    private uint[] _indices = [];

    public override string Title => "Animated geometry";

    public override string Category => "Animation";

    public override string Summary => "A 96x96 height field streamed to the GPU each frame, with normals recomputed on the CPU.";

    public override void Build(Scene scene)
    {
        BuildGrid();
        _surface = scene.CreateGeometry(_vertices, _indices);

        Material material = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x2F6FED), 0.25f, 0.25f) with
        {
            CullMode = CullMode.None,
        });
        scene.AddMesh(_surface, material, name: "surface");

        Node key = scene.AddLight(Light.Directional(new Vector3(1f, 0.96f, 0.9f), 2.8f));
        key.Position = new Vector3(6f, 8f, 4f);
        key.LookAt(Vector3.Zero);

        Node rim = scene.AddLight(Light.Point(MathHelpers.FromHex(0x4FD1E5).AsVector3(), 60f, range: 25f));
        rim.Position = new Vector3(-6f, 3f, -6f);

        scene.Environment = scene.Environment with { AmbientIntensity = 0.08f };
    }

    public override void Update(Scene scene, float deltaSeconds, double totalSeconds)
    {
        float t = (float)totalSeconds;
        float step = Size / (Resolution - 1);

        // Two interfering waves plus a radial ripple.
        for (int z = 0; z < Resolution; z++)
        {
            for (int x = 0; x < Resolution; x++)
            {
                int index = (z * Resolution) + x;
                float px = (x * step) - (Size * 0.5f);
                float pz = (z * step) - (Size * 0.5f);
                float distance = MathF.Sqrt((px * px) + (pz * pz));

                float height =
                    (MathF.Sin((px * 0.9f) + (t * 1.6f)) * 0.28f) +
                    (MathF.Cos((pz * 0.7f) - (t * 1.1f)) * 0.24f) +
                    (MathF.Sin((distance * 1.6f) - (t * 2.4f)) * 0.45f / (1f + (distance * 0.4f)));

                _vertices[index].Position = new Vector3(px, height, pz);
            }
        }

        RecomputeNormals();
        _surface.Update(_vertices, _indices);
    }

    private void BuildGrid()
    {
        _vertices = new Vertex[Resolution * Resolution];
        List<uint> indices = new((Resolution - 1) * (Resolution - 1) * 6);
        float step = Size / (Resolution - 1);

        for (int z = 0; z < Resolution; z++)
        {
            for (int x = 0; x < Resolution; x++)
            {
                int index = (z * Resolution) + x;
                _vertices[index] = new Vertex(
                    new Vector3((x * step) - (Size * 0.5f), 0f, (z * step) - (Size * 0.5f)),
                    Vector3.UnitY,
                    new Vector2(x / (float)(Resolution - 1), z / (float)(Resolution - 1)));
            }
        }

        for (int z = 0; z < Resolution - 1; z++)
        {
            for (int x = 0; x < Resolution - 1; x++)
            {
                uint a = (uint)((z * Resolution) + x);
                uint b = a + 1;
                uint c = a + Resolution;
                uint d = c + 1;
                indices.AddRange([a, c, b, b, c, d]);
            }
        }

        _indices = [.. indices];
    }

    /// <summary>
    /// Central differences on the height field: far cheaper than accumulating
    /// face normals, and exact enough for a smooth surface.
    /// </summary>
    private void RecomputeNormals()
    {
        float step = Size / (Resolution - 1);
        for (int z = 0; z < Resolution; z++)
        {
            for (int x = 0; x < Resolution; x++)
            {
                int index = (z * Resolution) + x;
                int left = (z * Resolution) + Math.Max(x - 1, 0);
                int right = (z * Resolution) + Math.Min(x + 1, Resolution - 1);
                int back = (Math.Max(z - 1, 0) * Resolution) + x;
                int front = (Math.Min(z + 1, Resolution - 1) * Resolution) + x;

                float dx = (_vertices[right].Position.Y - _vertices[left].Position.Y) / (2f * step);
                float dz = (_vertices[front].Position.Y - _vertices[back].Position.Y) / (2f * step);
                _vertices[index].Normal = Vector3.Normalize(new Vector3(-dx, 1f, -dz));
            }
        }
    }

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = Vector3.Zero;
        orbit.Distance = 13f;
        orbit.Yaw = 0.6f;
        orbit.Pitch = 0.45f;
    }
}
