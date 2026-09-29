using System.Numerics;
using DemoGraphics.Framework;
using ThreeNet;
using ThreeNet.Avalonia;

namespace DemoGraphics.Scenes;

/// <summary>
/// A set built to make shadows misbehave: a thin fence for acne, a wide arch for
/// cascade seams, pebbles for contact shadows and a colonnade marching into the
/// distance. The light can be a sun, a spot or a point light, which is the whole
/// point - the three shadow paths are different code.
/// </summary>
public sealed class ShadowLabScene : DemoScene
{
    private Node _sun = null!;
    private Node _spot = null!;
    private Node _point = null!;
    private Node _pointMarker = null!;
    private Node _far = null!;

    public ShadowLabScene()
    {
        Declare(
            DemoParameter.Choice("light", "Caster", 0, ["Sun (cascades)", "Spot (perspective)", "Point (cube)"]),
            DemoParameter.Slider("azimuth", "Light azimuth", 40f, 0f, 360f, "deg"),
            DemoParameter.Slider("elevation", "Light elevation", 42f, 5f, 88f, "deg"),
            DemoParameter.Slider("intensity", "Light intensity", 3.4f, 0.2f, 12f),
            DemoParameter.Slider("bias", "Depth bias", 0.0006f, 0f, 0.01f, "", 0f,
                "Too little and surfaces shadow themselves; too much and shadows detach."),
            DemoParameter.Slider("normalBias", "Normal bias", 1.6f, 0f, 8f, "texels"),
            DemoParameter.Slider("strength", "Shadow strength", 1f, 0f, 1f),
            DemoParameter.Toggle("distant", "Colonnade into the distance", true,
                "Shows where one cascade hands over to the next."),
            DemoParameter.Toggle("sweep", "Sweep the light", false));

        AddPreset("Hard noon", ("light", 0f), ("elevation", 78f), ("intensity", 5f), ("bias", 0.0006f), ("normalBias", 1.4f));
        AddPreset("Low evening", ("light", 0f), ("elevation", 12f), ("intensity", 2.6f), ("normalBias", 3.2f));
        AddPreset("Shadow acne", ("light", 0f), ("elevation", 14f), ("bias", 0f), ("normalBias", 0f),
            ("intensity", 4f));
        AddPreset("Detached shadows", ("light", 0f), ("bias", 0.006f), ("normalBias", 6f), ("elevation", 40f));
        AddPreset("Spot", ("light", 1f), ("intensity", 6f));
        AddPreset("Point cube", ("light", 2f), ("intensity", 6f));
        AddPreset("No shadows", ("strength", 0f));
    }

    public override string Id => "SHD";

    public override string Title => "Shadow laboratory";

    public override string Category => "Lighting";

    public override string Summary =>
        "Cascades, spot maps and cube maps on the same set, with bias and softness where you can see them fail.";

    public override IReadOnlyList<string> Features =>
        ["Cascaded shadow maps", "Spot shadows", "Point cube shadows", "PCF softness", "Depth and normal bias"];

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        Shadows = true,
        ShadowMapSize = 2048,
        ShadowCascades = 4,
        ShadowDistance = 120f,
        ShadowSoftness = 1,
        Ssao = true,
        SsaoRadius = 0.4f,
        Bloom = false,
    };

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 1.6f, -4f);
        orbit.Distance = 22f;
        orbit.Yaw = 0.55f;
        orbit.Pitch = 0.2f;
        orbit.MaxDistance = 180f;
    }

    public override IReadOnlyList<CameraKey> CameraPath =>
    [
        new(new Vector3(0f, 1.6f, -4f), 24f, 0.5f, 0.2f, 0f),
        new(new Vector3(0f, 1f, -20f), 12f, 1.6f, 0.06f, 4f),
        new(new Vector3(0f, 3f, -40f), 60f, 2.8f, 0.3f, 8f),
        new(new Vector3(0f, 1.6f, -4f), 24f, 4.2f, 0.2f, 12f),
    ];

    public override IEnumerable<(string Label, string Value)> Metrics()
    {
        yield return ("Caster", I("light") switch { 1 => "spot, 1 map", 2 => "point, 6 faces", _ => "sun, cascades" });
        yield return ("Depth bias", $"{P("bias"):F4}");
        yield return ("Normal bias", $"{P("normalBias"):F1} texels");
    }

    protected override void OnBuild()
    {
        Scene.Environment = SceneEnvironment.Default with
        {
            Background = Palette.Rgba(0x0D1117),
            AmbientColor = new Vector3(0.52f, 0.6f, 0.78f),
            AmbientIntensity = 0.13f,
            FogColor = new Vector3(0.1f, 0.12f, 0.16f),
            FogDensity = 0.004f,
            FogStart = 40f,
        };

        Material ground = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x8C887E), 0f, 0.72f));
        Scene.AddMesh(Procedural.Ground(Scene, 260f, 1), ground, name: "ground");

        Material stone = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0xC8C2B4), 0f, 0.6f));
        Material dark = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x33373D), 0f, 0.85f));

        // A picket fence: thin, tall, and exactly what makes acne visible.
        MeshBuilder fence = new();
        for (int i = 0; i < 22; i++)
        {
            fence.AddBox(new Vector3(-5.2f + (i * 0.5f), 0.9f, 2.6f), new Vector3(0.09f, 1.8f, 0.09f), Vector2.Zero);
        }

        fence.AddBox(new Vector3(0f, 1.5f, 2.6f), new Vector3(11f, 0.08f, 0.05f), Vector2.Zero);
        fence.AddBox(new Vector3(0f, 0.6f, 2.6f), new Vector3(11f, 0.08f, 0.05f), Vector2.Zero);
        Scene.AddMesh(fence.Build(Scene), dark, name: "fence");

        // An arch, for a shadow that spans cascades.
        MeshBuilder arch = new();
        arch.AddTube(new Vector3(-3.4f, 0f, -3.5f), 5.6f, 0.45f, 0.4f, 16, Vector2.Zero);
        arch.AddTube(new Vector3(3.4f, 0f, -3.5f), 5.6f, 0.45f, 0.4f, 16, Vector2.Zero);
        arch.AddBox(new Vector3(0f, 5.9f, -3.5f), new Vector3(8f, 0.6f, 0.9f), Vector2.Zero);
        Scene.AddMesh(arch.Build(Scene), stone, name: "arch");

        // A subject with creases, and pebbles for contact shadows.
        Node statue = Scene.AddMesh(Scene.CreateSphereGeometry(1.1f, 44, 24), stone, name: "statue");
        statue.Position = new Vector3(0f, 1.1f, -1f);
        Node plinth = Scene.AddMesh(Scene.CreateBoxGeometry(2.4f, 0.4f, 2.4f), dark, name: "plinth");
        plinth.Position = new Vector3(0f, 0.2f, -1f);

        Geometry pebble = Scene.CreateSphereGeometry(1f, 12, 8);
        for (int i = 0; i < 90; i++)
        {
            Node node = Scene.AddMesh(pebble, stone, name: $"pebble-{i}");
            float scale = 0.05f + (Noise.Hash(i, 61) * 0.14f);
            node.Position = new Vector3(
                (Noise.Hash(i, 31) - 0.5f) * 16f,
                scale * 0.8f,
                (Noise.Hash(i, 37) - 0.5f) * 12f - 2f);
            node.Scale = new Vector3(scale, scale * 0.7f, scale);
        }

        // The colonnade: the same object at 10, 20, 40, 80 m.
        _far = Scene.CreateNode(name: "colonnade");
        for (int i = 0; i < 16; i++)
        {
            float z = -10f - (i * 6.5f);
            float scale = 1f + (i * 0.12f);
            Node left = Scene.AddMesh(Scene.CreateCylinderGeometry(0.5f, 0.6f, 5f * scale, 20), stone, parent: _far, name: $"column-l{i}");
            left.Position = new Vector3(-6f, 2.5f * scale, z);
            Node right = Scene.AddMesh(Scene.CreateCylinderGeometry(0.5f, 0.6f, 5f * scale, 20), stone, parent: _far, name: $"column-r{i}");
            right.Position = new Vector3(6f, 2.5f * scale, z);
        }

        // ------------------------------------------------------------- lights
        _sun = Scene.AddLight(Light.Directional(Palette.Kelvin(5400f), 3.4f) with { CastShadow = true }, name: "sun");
        _spot = Scene.AddLight(
            Light.Spot(Palette.Kelvin(5000f), 900f, 90f, 16f.ToRadians(), 30f.ToRadians()) with { CastShadow = true },
            name: "spot");
        _point = Scene.AddLight(
            Light.Point(Palette.Kelvin(3400f), 260f, 40f) with { CastShadow = true },
            name: "point");
        _pointMarker = Scene.AddMesh(
            Scene.CreateSphereGeometry(0.22f, 18, 12),
            Scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.3f) with
            {
                Emissive = Palette.Kelvin(3400f),
                EmissiveIntensity = 10f,
            }),
            parent: _point,
            name: "point marker");
        _pointMarker.CastShadow = false;

        Scene.AddLight(Light.Ambient(new Vector3(0.5f, 0.6f, 0.82f), 0.13f), name: "sky light");
    }

    protected override void OnApplyParameters()
    {
        int kind = I("light");
        float azimuth = P("azimuth").ToRadians();
        float elevation = P("elevation").ToRadians();
        Vector3 direction = new(
            MathF.Sin(azimuth) * MathF.Cos(elevation),
            MathF.Sin(elevation),
            MathF.Cos(azimuth) * MathF.Cos(elevation));

        Vector3 focus = new(0f, 1.2f, -1f);
        _sun.Position = focus + (direction * 70f);
        _sun.LookAt(focus);
        _spot.Position = focus + (direction * 22f);
        _spot.LookAt(focus);
        _point.Position = focus + (direction * 9f);

        Apply(_sun, kind == 0);
        Apply(_spot, kind == 1);
        Apply(_point, kind == 2);
        _pointMarker.Visible = kind == 2;

        _far.Visible = B("distant");

        void Apply(Node node, bool enabled)
        {
            Light light = node.Light!.Value;
            light.Enabled = enabled;
            light.Intensity = P("intensity") * (node == _sun ? 1f : node == _spot ? 200f : 60f);
            light.ShadowBias = P("bias");
            light.ShadowNormalBias = P("normalBias");
            light.ShadowStrength = P("strength");
            node.Light = light;
        }
    }

    public override void Update(float deltaSeconds, double totalSeconds)
    {
        if (B("sweep"))
        {
            SetParameter("azimuth", (P("azimuth") + (deltaSeconds * 18f)) % 360f);
        }
    }
}
