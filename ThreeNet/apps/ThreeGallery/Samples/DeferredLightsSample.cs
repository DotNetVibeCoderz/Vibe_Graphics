using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>
/// The deferred renderer: geometry is written once into a G-buffer and a
/// single fullscreen pass lights every pixel, so a hundred moving point lights
/// cost about the same as a handful.
/// </summary>
public sealed class DeferredLightsSample : GallerySample
{
    private const int LightCount = 100;
    private readonly List<(Node Node, float Radius, float Speed, float Phase, float Height)> _lights = [];

    public override string Title => "Deferred: 100 lights";

    public override string Category => "Lighting";

    public override string Summary =>
        "RenderPath.Deferred with a G-buffer and one lighting pass; 100 coloured point lights orbit a field of pillars.";

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        RenderPath = RenderPath.Deferred,
        Bloom = true,
        BloomIntensity = 0.5f,
    };

    public override void Build(Scene scene)
    {
        _lights.Clear();

        Material floorMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x5C6370), 0.1f, 0.45f));
        Node floor = scene.AddMesh(scene.CreatePlaneGeometry(40f, 40f), floorMaterial, name: "floor");
        floor.EulerAngles = new Vector3(-MathF.PI / 2f, 0f, 0f);

        Geometry pillar = scene.CreateBoxGeometry(0.6f, 2.4f, 0.6f);
        Material pillarMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0xD9DDE3), 0f, 0.6f));
        for (int x = -5; x <= 5; x++)
        {
            for (int z = -5; z <= 5; z++)
            {
                if ((x + z) % 2 != 0)
                {
                    continue;
                }

                Node node = scene.AddMesh(pillar, pillarMaterial);
                node.Position = new Vector3(x * 2.4f, 1.2f, z * 2.4f);
            }
        }

        Geometry bulb = scene.CreateSphereGeometry(0.08f, 12, 8);
        Random random = new(7);
        for (int i = 0; i < LightCount; i++)
        {
            Vector3 color = Hue(i / (float)LightCount);
            Node light = scene.AddLight(Light.Point(color, 6f, range: 4.5f), name: $"light {i}");
            Material glow = scene.CreateMaterial(MaterialOptions.Basic(new Vector4(color, 1f)) with
            {
                Emissive = color,
                EmissiveIntensity = 4f,
            });
            scene.AddMesh(bulb, glow, parent: light);
            _lights.Add((light,
                2f + (random.NextSingle() * 11f),
                (0.2f + (random.NextSingle() * 0.5f)) * (random.Next(2) == 0 ? -1f : 1f),
                random.NextSingle() * MathF.Tau,
                0.4f + (random.NextSingle() * 1.6f)));
        }

        scene.Environment = scene.Environment with
        {
            Background = MathHelpers.FromHex(0x07080C),
            AmbientIntensity = 0.02f,
        };
    }

    public override void Update(Scene scene, float deltaSeconds, double totalSeconds)
    {
        float t = (float)totalSeconds;
        foreach ((Node node, float radius, float speed, float phase, float height) in _lights)
        {
            float angle = phase + (t * speed);
            node.Position = new Vector3(MathF.Cos(angle) * radius, height, MathF.Sin(angle) * radius);
        }
    }

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 0.8f, 0f);
        orbit.Distance = 20f;
        orbit.Yaw = 0.6f;
        orbit.Pitch = 0.55f;
    }

    private static Vector3 Hue(float h)
    {
        Vector3 color = new(
            MathF.Abs((h * 6f) - 3f) - 1f,
            2f - MathF.Abs((h * 6f) - 2f),
            2f - MathF.Abs((h * 6f) - 4f));
        return Vector3.Clamp(color, Vector3.Zero, Vector3.One);
    }
}
