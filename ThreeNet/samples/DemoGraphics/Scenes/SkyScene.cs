using System.Numerics;
using DemoGraphics.Framework;
using ThreeNet;
using ThreeNet.Avalonia;

namespace DemoGraphics.Scenes;

/// <summary>
/// The sky itself: the engine's own pass, with the sun, the haze, the clouds and
/// the stars on sliders, and a mirror ball and a lake to show what the rest of
/// the scene makes of it.
/// </summary>
/// <remarks>
/// The sky is drawn as one triangle at the far plane with the depth test on, so
/// it fills exactly what no geometry covered. That is cheaper than a dome and
/// avoids the two things a dome gets wrong: it is fogged like any other surface,
/// and it lands in the depth prepass that SSAO and depth of field read. Because
/// the same function is in the shading library, a reflective surface asks for
/// the sky in a direction and gets the sky actually overhead.
/// </remarks>
public sealed class SkyScene : DemoScene
{
    private readonly List<Node> _spheres = [];
    private Node _sun = null!;
    private Node _water = null!;

    public SkyScene()
    {
        Declare(
            DemoParameter.Choice("mode", "Sky", 1, ["Flat colour", "Procedural"],
                "Flat colour is the plain background; procedural is the pass that draws a sky."),
            DemoParameter.Slider("elevation", "Sun elevation", 28f, -20f, 90f, "deg"),
            DemoParameter.Slider("azimuth", "Sun bearing", 130f, 0f, 360f, "deg"),
            DemoParameter.Slider("intensity", "Sky brightness", 1f, 0f, 3f, "x"),
            DemoParameter.Slider("haze", "Haze", 0.3f, 0f, 1f,
                note: "Thickens the air near the horizon and flattens the gradient."),
            DemoParameter.Slider("clouds", "Cloud cover", 0.35f, 0f, 1f),
            DemoParameter.Slider("rotation", "Sky rotation", 0f, 0f, 360f, "deg"),
            DemoParameter.Toggle("ball", "Mirror ball", true),
            DemoParameter.Toggle("water", "Water", true, "Still water, so the reflection is the sky and nothing else."));

        AddPreset("Noon", ("mode", 1f), ("elevation", 72f), ("azimuth", 170f), ("haze", 0.18f), ("clouds", 0.25f), ("intensity", 1f));
        AddPreset("Golden hour", ("mode", 1f), ("elevation", 7f), ("azimuth", 255f), ("haze", 0.55f), ("clouds", 0.4f), ("intensity", 1.1f));
        AddPreset("Overcast", ("mode", 1f), ("elevation", 34f), ("azimuth", 150f), ("haze", 0.85f), ("clouds", 0.95f), ("intensity", 0.8f));
        AddPreset("Blue hour", ("mode", 1f), ("elevation", -4f), ("azimuth", 280f), ("haze", 0.4f), ("clouds", 0.2f), ("intensity", 1.2f));
        AddPreset("Night", ("mode", 1f), ("elevation", -24f), ("azimuth", 20f), ("haze", 0.2f), ("clouds", 0.1f), ("intensity", 1.4f));
    }

    public override string Id => "SKY";

    public override string Title => "Sky and horizon";

    public override string Category => "Environment";

    public override string Summary =>
        "The engine's sky pass: sun, haze, clouds, moon and stars, reflected by what is under it.";

    public override IReadOnlyList<string> Features =>
        ["Sky pass", "Sun and moon discs", "Cloud sheet", "Stars", "Sky reflections", "Bloom"];

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        Shadows = true,
        ShadowMapSize = 2048,
        ShadowCascades = 3,
        ShadowDistance = 60f,
        Ssao = false,
        Bloom = true,
        BloomIntensity = 0.7f,
        BloomThreshold = 1.2f,
        Exposure = 1f,
    };

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 1.4f, 0f);
        orbit.Distance = 11f;
        orbit.Yaw = 2.2f;
        // Tilted up: the sky is the subject here.
        orbit.Pitch = -0.08f;
        orbit.MinDistance = 2f;
        orbit.MaxDistance = 60f;
    }

    public override IReadOnlyList<CameraKey> CameraPath =>
    [
        new(new Vector3(0f, 1.4f, 0f), 11f, 0f, -0.05f, 0f),
        new(new Vector3(0f, 1.8f, 0f), 11f, 2.1f, 0.12f, 4f),
        new(new Vector3(0f, 1.4f, 0f), 11f, 4.2f, -0.18f, 8f),
        new(new Vector3(0f, 1.4f, 0f), 11f, 6.28f, -0.05f, 12f),
    ];

    public override IEnumerable<(string Label, string Value)> Metrics()
    {
        Vector3 sun = SunDirection();
        yield return ("Sun", $"{P("elevation"):F0} deg up, {P("azimuth"):F0} deg round");
        yield return ("Above the horizon", sun.Y > 0f ? "yes" : "no, the moon is out");
        yield return ("Draw cost", "one triangle, one pass");
        yield return ("Mode", I("mode") == 0 ? "flat colour" : "procedural");
    }

    protected override void OnBuild()
    {
        _spheres.Clear();

        Scene.Environment = SceneEnvironment.Default with
        {
            Background = Palette.Rgba(0x1A2430),
            Sky = SkyMode.Procedural,
            AmbientColor = new Vector3(0.5f, 0.6f, 0.78f),
            AmbientIntensity = 0.35f,
        };

        Material ground = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x2C3027), 0f, 0.92f));
        Scene.AddMesh(Procedural.Ground(Scene, 220f, 1), ground, name: "ground");

        // A row from mirror to matte: the left end shows the sky, the right end
        // shows only the light the sky casts.
        Geometry ball = Scene.CreateSphereGeometry(0.9f, 48, 32);
        for (int i = 0; i < 5; i++)
        {
            float roughness = i / 4f;
            Material material = Scene.CreateMaterial(
                MaterialOptions.Pbr(Palette.Rgba(0xCFD4D8), 1f, MathF.Max(0.02f, roughness * 0.9f)));
            Node node = Scene.AddMesh(ball, material, name: $"ball-{i}");
            node.Position = new Vector3((i - 2) * 2.4f, 0.9f, -1.5f);
            _spheres.Add(node);
        }

        // Still water: nothing ripples, so what shows in it is the sky.
        Material lake = Scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.02f, 0.05f, 0.07f, 1f), 0f, 0.02f) with
        {
            Reflectance = 1f,
        });
        _water = Scene.AddMesh(Procedural.Ground(Scene, 90f, 1), lake, name: "water");
        _water.Position = new Vector3(0f, 0.02f, 24f);

        _sun = Scene.AddLight(Light.Directional(Vector3.One, 3.5f) with { CastShadow = true }, name: "sun");

        AddAction("Sweep the sun round", () =>
        {
            SetParameter("azimuth", (P("azimuth") + 45f) % 360f);
        });
    }

    protected override void OnApplyParameters()
    {
        Vector3 sun = SunDirection();
        Scene.Environment = Scene.Environment with
        {
            Sky = I("mode") == 0 ? SkyMode.Color : SkyMode.Procedural,
            SunDirection = sun,
            SkyIntensity = P("intensity"),
            SkyHaze = P("haze"),
            SkyClouds = P("clouds"),
            SkyRotation = P("rotation") * MathF.PI / 180f,
        };

        // The light has to agree with the sky, or the shadows point the wrong way.
        float day = Math.Clamp((sun.Y + 0.1f) / 0.4f, 0f, 1f);
        Light light = _sun.Light!.Value;
        light.Color = Vector3.Lerp(Palette.Rgb(0x6F86C4), Palette.Rgb(0xFFF3DE), day);
        light.Intensity = float.Lerp(0.25f, 3.8f, day);
        _sun.Light = light;
        _sun.Position = sun * 60f;
        _sun.LookAt(Vector3.Zero);

        Scene.Environment = Scene.Environment with
        {
            AmbientIntensity = float.Lerp(0.08f, 0.4f, day),
        };

        foreach (Node node in _spheres)
        {
            node.Visible = B("ball");
        }

        _water.Visible = B("water");
    }

    private Vector3 SunDirection()
    {
        float elevation = P("elevation") * MathF.PI / 180f;
        float azimuth = P("azimuth") * MathF.PI / 180f;
        return Vector3.Normalize(new Vector3(
            MathF.Cos(elevation) * MathF.Sin(azimuth),
            MathF.Sin(elevation),
            MathF.Cos(elevation) * MathF.Cos(azimuth)));
    }

    protected override void OnUnload()
    {
        _spheres.Clear();
        _sun = null!;
        _water = null!;
    }
}
