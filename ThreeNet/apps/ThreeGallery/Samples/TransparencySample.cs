using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>Alpha blending, alpha masking and render order.</summary>
public sealed class TransparencySample : GallerySample
{
    private readonly List<Node> _panes = [];

    public override string Title => "Transparency";

    public override string Category => "Materials";

    public override string Summary => "Blended panes are sorted back to front automatically; masked materials use an alpha cutoff.";

    public override void Build(Scene scene)
    {
        _panes.Clear();
        Geometry pane = scene.CreatePlaneGeometry(2.6f, 2.6f);

        Vector4[] colors =
        [
            MathHelpers.FromHex(0xE5484D, 0.35f),
            MathHelpers.FromHex(0x30A46C, 0.35f),
            MathHelpers.FromHex(0x3E63DD, 0.35f),
        ];

        for (int i = 0; i < colors.Length; i++)
        {
            Material material = scene.CreateMaterial(MaterialOptions.Pbr(colors[i], 0f, 0.2f) with
            {
                AlphaMode = AlphaMode.Blend,
                CullMode = CullMode.None,
            });
            Node node = scene.AddMesh(pane, material, name: $"pane {i}");
            node.Position = new Vector3((i - 1) * 0.7f, 0f, i * 0.9f);
            _panes.Add(node);
        }

        // An opaque core behind the panes shows how the blending accumulates.
        Material coreMaterial = scene.CreateMaterial(MaterialOptions.Pbr(Colors.Orange, 0.6f, 0.25f));
        Node core = scene.AddMesh(scene.CreateSphereGeometry(0.8f), coreMaterial, name: "core");
        core.Position = new Vector3(0f, 0f, -1.2f);

        Node key = scene.AddLight(Light.Directional(Vector3.One, 3f));
        key.Position = new Vector3(3f, 5f, 6f);
        key.LookAt(Vector3.Zero);

        scene.Environment = scene.Environment with { AmbientIntensity = 0.2f };
    }

    public override void Update(Scene scene, float deltaSeconds, double totalSeconds)
    {
        for (int i = 0; i < _panes.Count; i++)
        {
            float phase = (float)totalSeconds * 0.6f + (i * 0.8f);
            _panes[i].Position = new Vector3(MathF.Sin(phase) * 1.2f, MathF.Cos(phase * 0.7f) * 0.4f, i * 0.9f);
        }
    }

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 0f, 0.6f);
        orbit.Distance = 8f;
        orbit.Yaw = 0.6f;
        orbit.Pitch = 0.2f;
    }
}
