using System.Numerics;
using DemoGraphics.Framework;
using ThreeNet;
using ThreeNet.Avalonia;

namespace DemoGraphics.Scenes;

/// <summary>
/// A valley with a full day in it. The point of this one is the clock: sun
/// colour, sky, shadow direction, exposure and the light the scene gets at night
/// all follow one number, and the timelapse runs it fast enough to watch.
/// </summary>
public sealed class TimeOfDayScene : DemoScene
{
    private readonly List<Node> _trees = [];
    private SkyDome _sky = null!;
    private Material _ground = null!;
    private Material _foliage = null!;
    private Node _sun = null!;
    private Node _moon = null!;
    private Node _skyLight = null!;
    private Node _lamp = null!;
    private Material _lampGlass = null!;

    public TimeOfDayScene()
    {
        Declare(
            DemoParameter.Slider("timelapse", "Timelapse", 0f, 0f, 240f, "min/s", 5f,
                "0 leaves the clock to the Time of day slider."),
            DemoParameter.Slider("relief", "Relief", 34f, 4f, 70f, "m", 0f, requiresRebuild: true),
            DemoParameter.Slider("snowLine", "Snow line", 26f, 4f, 60f, "m"),
            DemoParameter.Slider("trees", "Conifers", 900f, 0f, 4000f, "", 100f, requiresRebuild: true),
            DemoParameter.Toggle("lamp", "Valley lamp", true, "A warm point light with a cube shadow map."),
            DemoParameter.Toggle("moon", "Moonlight", true));

        AddPreset("Dawn", "Clear", 5.6f, ("snowLine", 26f));
        AddPreset("Morning", "Partly cloudy", 8.5f, ("snowLine", 26f));
        AddPreset("Noon", "Clear", 12f, ("snowLine", 30f));
        AddPreset("Golden hour", "Clear", 17.4f, ("snowLine", 26f));
        AddPreset("Blue hour", "Partly cloudy", 19.1f, ("snowLine", 24f));
        AddPreset("Midnight", "Clear", 0.4f, ("snowLine", 22f));
        AddPreset("Winter", "Snow", 9.5f, ("snowLine", 8f));
        AddPreset("Timelapse", null, null, ("timelapse", 90f));
    }

    public override string Id => "TOD";

    public override string Title => "Time of day";

    public override string Category => "Environment";

    public override string Summary =>
        "One valley, twenty-four hours: sun arc, sky colour, cascaded shadows and night lighting on a single clock.";

    public override IReadOnlyList<string> Features =>
        ["Cascaded shadow maps", "Point light cube shadows", "Procedural terrain", "Fog", "Wind"];

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        Shadows = true,
        ShadowMapSize = 2048,
        ShadowCascades = 4,
        ShadowDistance = 180f,
        Ssao = true,
        Bloom = true,
        BloomIntensity = 0.4f,
    };

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 12f, 0f);
        orbit.Distance = 96f;
        orbit.Yaw = 0.9f;
        orbit.Pitch = 0.16f;
        orbit.MaxDistance = 320f;
    }

    public override IReadOnlyList<CameraKey> CameraPath =>
    [
        new(new Vector3(0f, 12f, 0f), 96f, 0.9f, 0.16f, 0f),
        new(new Vector3(-18f, 8f, 14f), 54f, 2.1f, 0.10f, 4f),
        new(new Vector3(14f, 16f, -10f), 120f, 3.6f, 0.34f, 8f),
        new(new Vector3(0f, 12f, 0f), 96f, 5.0f, 0.16f, 12f),
    ];

    public override IEnumerable<(string Label, string Value)> Metrics()
    {
        yield return ("Clock", World.Clock);
        yield return ("Sun elevation", $"{MathF.Asin(Math.Clamp(World.SunPosition.Y, -1f, 1f)).ToDegrees():F1} deg");
        yield return ("Sun intensity", $"{World.SunIntensity:F2}");
        yield return ("Conifers", _trees.Count.ToString());
    }

    protected override void OnBuild()
    {
        _sky = SkyDome.Add(Scene);

        float relief = P("relief");
        Geometry terrain = Procedural.Ground(Scene, 400f, 220, (x, z) => Height(x, z, relief), 8f);
        Shader terrainShader = Scene.CreateShader(ShaderHooks.Terrain, ShaderLanguage.Wgsl, "terrain");
        _ground = Scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.9f) with { Shader = terrainShader });
        Scene.AddMesh(terrain, _ground, name: "valley");

        Shader foliageShader = Scene.CreateShader(ShaderHooks.Foliage, ShaderLanguage.Wgsl, "foliage");
        _foliage = Scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.8f) with { Shader = foliageShader });
        PlantForest((int)P("trees"), relief);

        BuildLamp(relief);

        _sun = Scene.AddLight(Light.Directional(Vector3.One, 4f) with { CastShadow = true, ShadowNormalBias = 2.4f }, name: "sun");
        _moon = Scene.AddLight(Light.Directional(Palette.Rgb(0x9FB6E8), 0.12f) with { CastShadow = false }, name: "moon");
        _skyLight = Scene.AddLight(Light.Ambient(new Vector3(0.5f, 0.6f, 0.85f), 0.4f), name: "sky light");
    }

    protected override void OnApplyEnvironment()
    {
        _sky.Apply(World);

        _sun.Position = World.SunPosition * 150f;
        _sun.LookAt(Vector3.Zero);
        Light sun = _sun.Light!.Value;
        sun.Color = World.SunColor;
        sun.Intensity = World.SunIntensity;
        _sun.Light = sun;

        _moon.Position = World.MoonPosition * 150f;
        _moon.LookAt(Vector3.Zero);
        Light moon = _moon.Light!.Value;
        moon.Intensity = B("moon") ? 0.22f * World.NightFactor : 0f;
        moon.Enabled = B("moon") && World.NightFactor > 0.02f;
        _moon.Light = moon;

        (Vector3 color, float intensity) = World.SkyLight;
        Light ambient = _skyLight.Light!.Value;
        ambient.Color = color;
        ambient.Intensity = intensity;
        _skyLight.Light = ambient;

        Vector3 horizon = Vector3.Lerp(new Vector3(0.42f, 0.56f, 0.80f), new Vector3(0.022f, 0.035f, 0.085f), World.NightFactor);
        Scene.Environment = Scene.Environment with
        {
            Background = new Vector4(horizon * 0.35f, 1f),
            FogColor = horizon,
            FogDensity = World.FogDensity,
            FogStart = 70f,
            AmbientIntensity = 0.02f,
        };

        _ground.Update(options => options with
        {
            Custom0 = new Vector4(1.5f, P("snowLine"), 0.5f, MathF.Max(World.SnowAmount, 0.35f)),
            Custom1 = new Vector4(World.Wetness, 0f, 0f, 0f),
        });

        _foliage.Update(options => options with
        {
            Custom0 = new Vector4(Palette.Rgb(0x3E3226), World.WindDirection.ToRadians()),
            Custom1 = new Vector4(Palette.Rgb(0x1F3A22), World.WindStrength),
        });
    }

    protected override void OnApplyParameters()
    {
        OnApplyEnvironment();

        Light lamp = _lamp.Light!.Value;
        lamp.Enabled = B("lamp");
        _lamp.Light = lamp;
        _lampGlass.Update(options => options with { EmissiveIntensity = B("lamp") ? 16f : 0f });
    }

    public override void Update(float deltaSeconds, double totalSeconds)
    {
        _sky.Follow(Viewer);

        float minutesPerSecond = P("timelapse");
        if (minutesPerSecond > 0f)
        {
            // Moving the shared clock re-applies the weather for every scene.
            World.Hour += minutesPerSecond / 60f * deltaSeconds;
        }
    }

    protected override void OnUnload() => _trees.Clear();

    private static float Height(float x, float z, float relief)
    {
        // A valley floor with ridges either side, plus a river bed down the middle.
        float ridges = Noise.Ridged((x * 0.0055f) + 3f, (z * 0.0055f) + 5f, 6);
        float across = Math.Clamp(MathF.Abs(x) / 150f, 0f, 1f);
        float detail = Noise.Fbm(x * 0.03f, z * 0.03f, 4) - 0.5f;
        float valley = MathF.Pow(across, 1.7f);
        float river = MathF.Exp(-(x * x) / 420f) * 3.2f;
        return (ridges * relief * valley) + (detail * 2.6f) - river;
    }

    private void PlantForest(int count, float relief)
    {
        if (count <= 0)
        {
            return;
        }

        const int variants = 8;
        Geometry[] trees = new Geometry[variants];
        for (int i = 0; i < variants; i++)
        {
            // Each variant carries its own sway phase, so a forest built from
            // eight shared geometries does not move as one block.
            trees[i] = Procedural.Tree(Scene, 7f + (i * 0.55f), Noise.Hash(i, 41), conifer: true);
        }

        for (int i = 0; i < count; i++)
        {
            float x = (Noise.Hash(i, 101) - 0.5f) * 360f;
            float z = (Noise.Hash(i, 103) - 0.5f) * 360f;
            float y = Height(x, z, relief);
            // Trees stop where it gets steep or cold, which is what makes a tree
            // line look deliberate rather than scattered.
            float slope = MathF.Abs(Height(x + 2f, z, relief) - y) + MathF.Abs(Height(x, z + 2f, relief) - y);
            if (y < 0.4f || y > P("snowLine") + 4f || slope > 3.2f)
            {
                continue;
            }

            Node tree = Scene.AddMesh(trees[i % variants], _foliage, name: $"conifer-{i}");
            tree.Position = new Vector3(x, y - 0.1f, z);
            float scale = 0.75f + (Noise.Hash(i, 107) * 0.9f);
            tree.Scale = new Vector3(scale);
            tree.EulerAngles = new Vector3(0f, Noise.Hash(i, 109) * MathF.Tau, 0f);
            _trees.Add(tree);
        }
    }

    private void BuildLamp(float relief)
    {
        float y = Height(0f, 24f, relief);
        MeshBuilder post = new();
        post.AddTube(Vector3.Zero, 5.2f, 0.16f, 0.12f, 8, Vector2.Zero);
        post.AddTube(new Vector3(0f, 5.2f, 0f), 0.5f, 0.55f, 0.1f, 10, Vector2.Zero);
        Material metal = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x2A2D33), 0.8f, 0.45f));
        Node node = Scene.AddMesh(post.Build(Scene), metal, name: "lamp post");
        node.Position = new Vector3(0f, y, 24f);

        _lampGlass = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0xFFE0A8), 0f, 0.2f) with
        {
            Emissive = Palette.Rgb(0xFFC978),
            EmissiveIntensity = 16f,
        });
        Node glass = Scene.AddMesh(Scene.CreateSphereGeometry(0.42f, 16, 10), _lampGlass, parent: node, name: "lamp glass");
        glass.Position = new Vector3(0f, 5.1f, 0f);
        glass.CastShadow = false;

        // A point light with shadows: six cube faces, which FrameStats reports.
        _lamp = Scene.AddLight(
            Light.Point(Palette.Rgb(0xFFC978), 60f, 34f) with { CastShadow = true, ShadowBias = 0.002f },
            parent: node,
            name: "lamp light");
        _lamp.Position = new Vector3(0f, 5.1f, 0f);
    }
}
