using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>A thousand animated nodes, to watch culling and draw calls work.</summary>
public sealed class StressTestSample : GallerySample
{
    private const int Count = 1000;

    private readonly List<(Node Node, Vector3 Axis, float Speed, float Radius, float Phase)> _cubes = [];

    public override string Title => "1000 objects";

    public override string Category => "Performance";

    public override string Summary => "One geometry, a handful of materials, a thousand nodes: watch the culled count as you orbit.";

    public override void Build(Scene scene)
    {
        _cubes.Clear();

        Geometry cube = scene.CreateBoxGeometry(0.4f, 0.4f, 0.4f);
        Material[] materials =
        [
            scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0xF0A030), 0.3f, 0.35f)),
            scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x4FD1E5), 0.1f, 0.5f)),
            scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0xB14EFF), 0.8f, 0.2f)),
            scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x30D158), 0f, 0.7f)),
        ];

        Random random = new(42);
        for (int i = 0; i < Count; i++)
        {
            Node node = scene.AddMesh(cube, materials[i % materials.Length], name: $"cube {i}");

            // Distribute the cubes on a spherical shell so orbiting the camera
            // pushes a visible share of them out of the frustum.
            float theta = (float)(random.NextDouble() * Math.Tau);
            float phi = MathF.Acos((float)((random.NextDouble() * 2) - 1));
            float radius = 6f + ((float)random.NextDouble() * 6f);

            _cubes.Add((
                node,
                Vector3.Normalize(new Vector3(
                    (float)random.NextDouble() - 0.5f,
                    (float)random.NextDouble() - 0.5f,
                    (float)random.NextDouble() - 0.5f)),
                0.3f + ((float)random.NextDouble() * 1.4f),
                radius,
                theta + phi));

            node.Position = new Vector3(
                radius * MathF.Sin(phi) * MathF.Cos(theta),
                radius * MathF.Cos(phi),
                radius * MathF.Sin(phi) * MathF.Sin(theta));
        }

        Node key = scene.AddLight(Light.Directional(Vector3.One, 2.5f));
        key.Position = new Vector3(8f, 10f, 8f);
        key.LookAt(Vector3.Zero);

        Node fill = scene.AddLight(Light.Point(MathHelpers.FromHex(0x4FD1E5).AsVector3(), 200f, range: 40f));
        fill.Position = new Vector3(-10f, -4f, -6f);

        scene.Environment = scene.Environment with { AmbientIntensity = 0.06f };
    }

    public override void Update(Scene scene, float deltaSeconds, double totalSeconds)
    {
        foreach ((Node node, Vector3 axis, float speed, float _, float phase) in _cubes)
        {
            node.Rotation = Quaternion.CreateFromAxisAngle(axis, ((float)totalSeconds * speed) + phase);
        }
    }

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = Vector3.Zero;
        orbit.Distance = 26f;
        orbit.Yaw = 0.5f;
        orbit.Pitch = 0.25f;
    }
}
