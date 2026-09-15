using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>Every built-in primitive, side by side on a ground plane.</summary>
public sealed class PrimitivesSample : GallerySample
{
    private readonly List<Node> _shapes = [];

    public override string Title => "Primitives";

    public override string Category => "Basics";

    public override string Summary => "Box, sphere, cylinder, cone, torus and plane, each with a PBR material.";

    public override void Build(Scene scene)
    {
        _shapes.Clear();

        // A ground plane catches the light and gives the shapes a context.
        Material floorMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x2A2F3A), 0f, 0.95f));
        Node floor = scene.AddMesh(scene.CreatePlaneGeometry(40f, 40f), floorMaterial, name: "floor");
        floor.EulerAngles = new Vector3(-MathF.PI / 2f, 0f, 0f);
        floor.Position = new Vector3(0f, -1f, 0f);

        // Each entry pairs a geometry with the colour it is drawn in.
        (Geometry Geometry, Vector4 Color, string Name)[] shapes =
        [
            (scene.CreateBoxGeometry(1.4f, 1.4f, 1.4f), Colors.Orange, "box"),
            (scene.CreateSphereGeometry(0.85f), Colors.Cyan, "sphere"),
            (scene.CreateCylinderGeometry(0.6f, 0.6f, 1.6f), Colors.Green, "cylinder"),
            (scene.CreateConeGeometry(0.75f, 1.6f), Colors.Yellow, "cone"),
            (scene.CreateTorusGeometry(0.7f, 0.28f), Colors.Purple, "torus"),
        ];

        float spacing = 2.4f;
        float start = -(shapes.Length - 1) * spacing * 0.5f;
        for (int i = 0; i < shapes.Length; i++)
        {
            (Geometry geometry, Vector4 color, string name) = shapes[i];
            Material material = scene.CreateMaterial(MaterialOptions.Pbr(color, metallic: 0.1f, roughness: 0.35f));
            Node node = scene.AddMesh(geometry, material, name: name);
            node.Position = new Vector3(start + (i * spacing), 0f, 0f);
            _shapes.Add(node);
        }

        Node key = scene.AddLight(Light.Directional(Vector3.One, 3.2f), name: "key");
        key.Position = new Vector3(5f, 8f, 6f);
        key.LookAt(Vector3.Zero);

        Node rim = scene.AddLight(Light.Point(new Vector3(0.35f, 0.6f, 1f), 25f, range: 16f), name: "rim");
        rim.Position = new Vector3(-4f, 2.5f, -4f);

        scene.Environment = scene.Environment with { AmbientIntensity = 0.12f };
    }

    public override void Update(Scene scene, float deltaSeconds, double totalSeconds)
    {
        // Rotate each shape at a slightly different speed so the lighting reads.
        for (int i = 0; i < _shapes.Count; i++)
        {
            _shapes[i].EulerAngles = new Vector3(0f, (float)totalSeconds * (0.3f + (i * 0.12f)), 0f);
        }
    }

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 0.2f, 0f);
        orbit.Distance = 11f;
        orbit.Yaw = 0.35f;
        orbit.Pitch = 0.32f;
    }
}
