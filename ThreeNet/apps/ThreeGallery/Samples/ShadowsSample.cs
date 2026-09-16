using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>
/// Cascaded shadow maps for the sun, a perspective shadow map for the spot
/// light, and SSAO for the contact darkening where objects meet the floor.
/// </summary>
public sealed class ShadowsSample : GallerySample
{
    private Node? _sun;
    private Node? _spot;
    private readonly List<(Node Node, float Phase, float Radius)> _movers = [];

    public override string Title => "Shadows & SSAO";

    public override string Category => "Lighting";

    public override string Summary =>
        "Directional cascades plus a spot light shadow, with screen space ambient occlusion filling the creases.";

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        Shadows = true,
        ShadowMapSize = 2048,
        ShadowCascades = 3,
        ShadowDistance = 45f,
        ShadowSoftness = 1,
        Ssao = true,
        SsaoRadius = 0.7f,
        SsaoIntensity = 1.6f,
        SsaoDirectStrength = 0.3f,
    };

    public override void Build(Scene scene)
    {
        _movers.Clear();

        Material floorMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x9AA3AE), 0f, 0.8f));
        Node floor = scene.AddMesh(scene.CreatePlaneGeometry(60f, 60f), floorMaterial, name: "floor");
        floor.EulerAngles = new Vector3(-MathF.PI / 2f, 0f, 0f);
        // A floor only receives shadows; leaving it out of the shadow maps keeps
        // the cascades tight around the objects that matter.
        floor.CastShadow = false;

        Material wallMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x6F7783), 0f, 0.9f));
        Node wall = scene.AddMesh(scene.CreatePlaneGeometry(20f, 8f), wallMaterial, name: "wall");
        wall.Position = new Vector3(0f, 4f, -6f);

        Material pillarMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0xE4E7EC), 0.05f, 0.55f));
        for (int i = -2; i <= 2; i++)
        {
            Node pillar = scene.AddMesh(scene.CreateBoxGeometry(0.8f, 3.4f, 0.8f), pillarMaterial, name: $"pillar {i}");
            pillar.Position = new Vector3(i * 2.8f, 1.7f, -2.5f);
        }

        Material sphereMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0xE8734A), 0.1f, 0.35f));
        Geometry sphere = scene.CreateSphereGeometry(0.7f, 32, 24);
        for (int i = 0; i < 3; i++)
        {
            Node node = scene.AddMesh(sphere, sphereMaterial, name: $"sphere {i}");
            _movers.Add((node, i * MathF.Tau / 3f, 2.4f + (i * 0.7f)));
        }

        Material torusMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x4FD1E5), 0.9f, 0.2f));
        Node torus = scene.AddMesh(scene.CreateTorusGeometry(1.1f, 0.35f, 20, 72), torusMaterial, name: "torus");
        torus.Position = new Vector3(0f, 1.4f, 1.5f);

        _sun = scene.AddLight(
            Light.Directional(MathHelpers.FromHex(0xFFF3E0).AsVector3(), 3.2f) with { CastShadow = true },
            name: "sun");
        _sun.Position = new Vector3(6f, 9f, 6f);
        _sun.LookAt(Vector3.Zero);

        _spot = scene.AddLight(
            Light.Spot(MathHelpers.FromHex(0x8FB6FF).AsVector3(), 60f, range: 30f, innerAngle: 0.28f, outerAngle: 0.45f) with
            {
                CastShadow = true,
                // A tight cone concentrates the map, so it needs less bias.
                ShadowNormalBias = 1.0f,
            },
            name: "spot");
        _spot.Position = new Vector3(-5f, 6.5f, 4f);
        _spot.LookAt(new Vector3(0f, 1f, 0f));

        scene.Environment = scene.Environment with
        {
            Background = MathHelpers.FromHex(0x1A1D24),
            AmbientColor = MathHelpers.FromHex(0x8FA5C8).AsVector3(),
            AmbientIntensity = 0.35f,
        };
    }

    public override void Update(Scene scene, float deltaSeconds, double totalSeconds)
    {
        float t = (float)totalSeconds;
        foreach ((Node node, float phase, float radius) in _movers)
        {
            float angle = (t * 0.5f) + phase;
            node.Position = new Vector3(
                MathF.Cos(angle) * radius,
                0.9f + (MathF.Abs(MathF.Sin(angle * 1.6f)) * 1.8f),
                MathF.Sin(angle) * radius);
        }

        // Sweeping the sun makes the cascade transitions easy to inspect.
        if (_sun is not null)
        {
            _sun.Position = new Vector3(MathF.Cos(t * 0.15f) * 8f, 9f, MathF.Sin(t * 0.15f) * 8f);
            _sun.LookAt(Vector3.Zero);
        }
    }

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 1.4f, 0f);
        orbit.Distance = 14f;
        orbit.Yaw = 0.55f;
        orbit.Pitch = 0.3f;
    }
}
