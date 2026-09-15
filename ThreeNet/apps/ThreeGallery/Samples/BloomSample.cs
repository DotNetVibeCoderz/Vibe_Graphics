using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>HDR emissive materials pushed through the bloom and tone mapping chain.</summary>
public sealed class BloomSample : GallerySample
{
    private readonly List<(Node Node, float Phase)> _emitters = [];

    public override string Title => "Bloom & tone mapping";

    public override string Category => "Post-processing";

    public override string Summary => "Emissive values above 1.0 bleed through the bright pass; ACES keeps the highlights from clipping.";

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        Bloom = true,
        BloomIntensity = 0.9f,
        BloomThreshold = 1.0f,
        ToneMapping = ToneMapping.Aces,
        Exposure = 1.1f,
    };

    public override void Build(Scene scene)
    {
        _emitters.Clear();

        Material floorMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x14161C), 0.2f, 0.45f));
        Node floor = scene.AddMesh(scene.CreatePlaneGeometry(40f, 40f), floorMaterial, name: "floor");
        floor.EulerAngles = new Vector3(-MathF.PI / 2f, 0f, 0f);
        floor.Position = new Vector3(0f, -1.6f, 0f);

        Geometry orb = scene.CreateSphereGeometry(0.45f, 32, 24);
        Vector4[] colors =
        [
            MathHelpers.FromHex(0xFF7A18),
            MathHelpers.FromHex(0x4FD1E5),
            MathHelpers.FromHex(0xB14EFF),
            MathHelpers.FromHex(0x30D158),
        ];

        for (int i = 0; i < colors.Length; i++)
        {
            // Emissive intensity above 1 is what the bright pass picks up.
            Material material = scene.CreateMaterial(MaterialOptions.Basic(colors[i]) with
            {
                Emissive = colors[i].AsVector3(),
                EmissiveIntensity = 4.5f,
            });
            Node node = scene.AddMesh(orb, material, name: $"emitter {i}");
            _emitters.Add((node, i * MathF.Tau / colors.Length));

            // A matching point light, parented to the orb, ties the glow to the
            // rest of the scene.
            scene.AddLight(Light.Point(colors[i].AsVector3(), 18f, range: 10f), node);
        }

        Material chromeMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0xDDE3EA), 1f, 0.12f));
        scene.AddMesh(scene.CreateTorusGeometry(1.6f, 0.45f, 24, 96), chromeMaterial, name: "torus");

        scene.Environment = scene.Environment with
        {
            Background = MathHelpers.FromHex(0x05060A),
            AmbientIntensity = 0.02f,
        };
    }

    public override void Update(Scene scene, float deltaSeconds, double totalSeconds)
    {
        float t = (float)totalSeconds;
        foreach ((Node node, float phase) in _emitters)
        {
            float angle = (t * 0.6f) + phase;
            node.Position = new Vector3(MathF.Cos(angle) * 2.6f, MathF.Sin(angle * 1.7f) * 0.9f, MathF.Sin(angle) * 2.6f);
        }
    }

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = Vector3.Zero;
        orbit.Distance = 9f;
        orbit.Yaw = 0.6f;
        orbit.Pitch = 0.25f;
    }
}
