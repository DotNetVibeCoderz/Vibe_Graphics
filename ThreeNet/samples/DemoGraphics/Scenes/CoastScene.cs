using System.Numerics;
using DemoGraphics.Framework;
using ThreeNet;
using ThreeNet.Avalonia;

namespace DemoGraphics.Scenes;

/// <summary>
/// The flagship scene: open water around a small island, under a sky that knows
/// what time it is. The waves are a vertex shader, their normals come from the
/// same function in the surface shader, and the sun, the wind and the clouds are
/// the app's shared weather - so turning the wind up moves the water, the trees
/// and the cloud sheet together.
/// </summary>
public sealed class CoastScene : DemoScene
{
    private readonly List<Node> _palms = [];
    private Material _water = null!;
    private Material _island = null!;
    private Material _foliage = null!;
    private Node _sun = null!;
    private Node _skyLight = null!;
    private Node _beacon = null!;
    private Node _beaconLight = null!;
    private Geometry _waterGeometry = null!;

    public CoastScene()
    {
        Declare(
            DemoParameter.Slider("waveHeight", "Wave height", 0.42f, 0f, 2.5f, "m"),
            DemoParameter.Slider("waveLength", "Wave length", 14f, 2f, 40f, "m"),
            DemoParameter.Slider("waveSpeed", "Wave speed", 1.1f, 0f, 4f, "x"),
            DemoParameter.Slider("choppiness", "Choppiness", 0.45f, 0f, 1.5f),
            DemoParameter.Slider("foam", "Foam", 0.45f, 0f, 1f),
            DemoParameter.Slider("clarity", "Water clarity", 0.55f, 0f, 1f),
            DemoParameter.Slider("detail", "Water mesh", 180f, 40f, 400f, "cells", 10f,
                "Rebuilds the water grid: the cost of a wave is a vertex cost.", requiresRebuild: true),
            DemoParameter.Toggle("beacon", "Lighthouse beam", true),
            DemoParameter.Toggle("palms", "Shore planting", true));

        AddPreset("Calm dawn", "Clear", 5.8f,
            ("waveHeight", 0.18f), ("waveLength", 14f), ("waveSpeed", 0.7f), ("choppiness", 0.2f), ("foam", 0.35f), ("clarity", 0.7f));
        AddPreset("Tropical noon", "Clear", 12.2f,
            ("waveHeight", 0.3f), ("waveLength", 8f), ("waveSpeed", 1.1f), ("choppiness", 0.4f), ("foam", 0.6f), ("clarity", 0.95f));
        AddPreset("Windy afternoon", "Partly cloudy", 15.5f,
            ("waveHeight", 0.8f), ("waveLength", 11f), ("waveSpeed", 1.6f), ("choppiness", 0.7f), ("foam", 0.85f), ("clarity", 0.5f));
        AddPreset("Storm", "Storm", 16.5f,
            ("waveHeight", 2.1f), ("waveLength", 17f), ("waveSpeed", 2.6f), ("choppiness", 1.2f), ("foam", 1f), ("clarity", 0.15f));
        AddPreset("Sunset", "Partly cloudy", 18.3f,
            ("waveHeight", 0.55f), ("waveLength", 12f), ("waveSpeed", 1f), ("choppiness", 0.45f), ("foam", 0.6f), ("clarity", 0.45f));
        AddPreset("Night", "Clear", 23.2f,
            ("waveHeight", 0.35f), ("waveLength", 10f), ("waveSpeed", 0.9f), ("choppiness", 0.35f), ("foam", 0.5f), ("clarity", 0.35f));
    }

    public override string Id => "COA";

    public override string Title => "Dawn coast";

    public override string Category => "Environment";

    public override string Summary =>
        "Gerstner-style water, a procedural sky and an island, all driven by one shared weather state.";

    public override IReadOnlyList<string> Features =>
        ["Custom vertex + surface shaders", "Cascaded shadows", "Fog", "Bloom", "Procedural geometry"];

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        Shadows = true,
        ShadowMapSize = 2048,
        ShadowCascades = 3,
        ShadowDistance = 150f,
        ShadowSoftness = 1,
        Ssao = true,
        SsaoSamples = 12,
        SsaoRadius = 0.7f,
        Bloom = true,
        BloomIntensity = 0.45f,
        BloomThreshold = 1.1f,
        Exposure = 1f,
    };

    public override void ConfigureCamera(OrbitController orbit)
    {
        // The island peaks around 25 m, so the camera has to sit above it or it
        // ends up inside the hill.
        orbit.Target = new Vector3(0f, 8f, 0f);
        orbit.Distance = 125f;
        orbit.Yaw = 5.6f;
        orbit.Pitch = 0.28f;
        orbit.MaxDistance = 320f;
    }

    public override IReadOnlyList<CameraKey> CameraPath =>
    [
        new(new Vector3(0f, 8f, 0f), 125f, 5.6f, 0.28f, 0f),
        new(new Vector3(20f, 4f, -14f), 60f, 0.4f, 0.14f, 4f),
        new(new Vector3(-10f, 6f, 16f), 150f, 2.0f, 0.36f, 8f),
        new(new Vector3(0f, 8f, 0f), 125f, 5.6f, 0.28f, 12f),
    ];

    public override IEnumerable<(string Label, string Value)> Metrics()
    {
        yield return ("Water cells", $"{(int)P("detail")} x {(int)P("detail")}");
        yield return ("Wave height", $"{P("waveHeight"):F2} m");
        yield return ("Wind", $"{World.WindSpeed:F1} m/s at {World.WindDirection:F0} deg");
        yield return ("Shore planting", _palms.Count.ToString());
    }

    protected override void OnBuild()
    {
        // -------------------------------------------------------------- water
        int cells = (int)P("detail");
        _waterGeometry = Procedural.Ground(Scene, 400f, cells);
        Shader waterShader = Scene.CreateShader(ShaderHooks.Water, ShaderLanguage.Wgsl, "water");
        _water = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x0B3F52), 0f, 0.05f) with
        {
            Shader = waterShader,
            Reflectance = 0.95f,
        });
        Node water = Scene.AddMesh(_waterGeometry, _water, name: "water");
        // A 400 m sheet would swamp the shadow cascades and shadow nothing useful.
        water.CastShadow = false;

        // ------------------------------------------------------------- island
        Geometry islandGeometry = Procedural.Ground(Scene, 260f, 170, IslandHeight, 6f);
        Shader terrainShader = Scene.CreateShader(ShaderHooks.Terrain, ShaderLanguage.Wgsl, "terrain");
        _island = Scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.9f) with { Shader = terrainShader });
        Scene.AddMesh(islandGeometry, _island, name: "island");

        // -------------------------------------------------------- shore props
        Shader foliageShader = Scene.CreateShader(ShaderHooks.Foliage, ShaderLanguage.Wgsl, "foliage");
        _foliage = Scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.8f) with { Shader = foliageShader });
        PlantShore(6);
        BuildLighthouse();
        ScatterRocks();

        // -------------------------------------------------------------- light
        _sun = Scene.AddLight(Light.Directional(Vector3.One, 4f) with { CastShadow = true, ShadowNormalBias = 2.2f }, name: "sun");
        _skyLight = Scene.AddLight(Light.Ambient(new Vector3(0.5f, 0.62f, 0.85f), 0.4f), name: "sky light");
    }

    protected override void OnApplyEnvironment()
    {
        _sun.Position = World.SunPosition * 90f;
        _sun.LookAt(Vector3.Zero);
        Light sun = _sun.Light!.Value;
        sun.Color = World.SunColor;
        sun.Intensity = World.SunIntensity;
        _sun.Light = sun;

        (Vector3 color, float intensity) = World.SkyLight;
        Light ambient = _skyLight.Light!.Value;
        ambient.Color = color;
        ambient.Intensity = intensity;
        _skyLight.Light = ambient;

        // Fog starts beyond the sky dome so the sky keeps its gradient.
        Vector3 horizon = Vector3.Lerp(new Vector3(0.42f, 0.56f, 0.80f), new Vector3(0.022f, 0.035f, 0.085f), World.NightFactor);
        Scene.Environment = Scene.Environment with
        {
            // The engine draws the sky: a pass at the far plane, so it is never
            // fogged and never lands in the depth prepass.
            Sky = SkyMode.Procedural,
            SunDirection = World.SunPosition,
            SkyIntensity = 1f,
            SkyHaze = Math.Clamp(World.FogDensity * 18f, 0.05f, 1f),
            SkyClouds = World.CloudCover,
            Background = new Vector4(horizon * 0.4f, 1f),
            FogColor = horizon,
            FogDensity = MathF.Max(World.FogDensity, 0.0075f),
            FogStart = 65f,
            AmbientIntensity = 0.02f,
        };

        _island.Update(options => options with
        {
            Custom0 = new Vector4(0.15f, 3.5f, 0.55f, World.SnowAmount),
            Custom1 = new Vector4(MathF.Max(World.Wetness, 0.25f), 0f, 0f, 0f),
        });

        ApplyFoliageWind();
        ApplyWaterParameters();
    }

    protected override void OnApplyParameters()
    {
        ApplyWaterParameters();
        ApplyFoliageWind();

        Light beacon = _beaconLight.Light!.Value;
        beacon.Enabled = B("beacon");
        _beaconLight.Light = beacon;
        _beacon.Visible = B("beacon");

        foreach (Node palm in _palms)
        {
            palm.Visible = B("palms");
        }
    }

    public override void Update(float deltaSeconds, double totalSeconds)
    {
        if (B("beacon"))
        {
            // The beam sweeps once every eight seconds.
            _beaconLight.EulerAngles = new Vector3(-0.18f, (float)(totalSeconds * 0.78f), 0f);
        }
    }

    protected override void OnUnload()
    {
        _palms.Clear();
    }

    /// <summary>A single island: a radial cone softened by noise, below zero out at sea.</summary>
    private static float IslandHeight(float x, float z)
    {
        float distance = MathF.Sqrt((x * x) + (z * z));
        float shape = Math.Clamp(1f - (distance / 78f), -1f, 1f);
        float ridge = Noise.Ridged((x * 0.012f) + 11f, (z * 0.012f) + 7f, 5);
        float detail = Noise.Fbm(x * 0.05f, z * 0.05f, 4) - 0.5f;
        float height = (shape * shape * 26f * (0.45f + (0.85f * ridge))) + (detail * 2.2f * MathF.Max(shape, 0f));
        // Out past the island the bed drops away, so the water reads as deep.
        return shape > 0f ? height - 1.6f : -6f + (detail * 1.5f);
    }

    private void PlantShore(int variants)
    {
        Geometry[] trees = new Geometry[variants];
        for (int i = 0; i < variants; i++)
        {
            trees[i] = Procedural.Tree(Scene, 6.5f + (i * 0.7f), Noise.Hash(i, 91), conifer: false);
        }

        // Deterministic scatter: the same island every run, benchmark included.
        for (int i = 0; i < 70; i++)
        {
            float angle = Noise.Hash(i, 3) * MathF.Tau;
            float radius = 16f + (Noise.Hash(i, 5) * 52f);
            float x = MathF.Sin(angle) * radius;
            float z = MathF.Cos(angle) * radius;
            float height = IslandHeight(x, z);
            if (height < 0.6f || height > 17f)
            {
                continue;
            }

            Node palm = Scene.AddMesh(trees[i % variants], _foliage, name: $"palm-{i}");
            palm.Position = new Vector3(x, height - 0.2f, z);
            float scale = 0.8f + (Noise.Hash(i, 13) * 0.7f);
            palm.Scale = new Vector3(scale);
            palm.EulerAngles = new Vector3(0f, Noise.Hash(i, 17) * MathF.Tau, 0f);
            _palms.Add(palm);
        }
    }

    private void BuildLighthouse()
    {
        float x = 26f;
        float z = -18f;
        float ground = MathF.Max(IslandHeight(x, z), 1f);

        MeshBuilder tower = new();
        tower.AddTube(Vector3.Zero, 15f, 3.1f, 2.1f, 20, new Vector2(0f, 0f));
        tower.AddTube(new Vector3(0f, 15f, 0f), 1.2f, 2.9f, 2.9f, 20, new Vector2(0f, 0f));
        tower.AddCone(new Vector3(0f, 18.4f, 0f), 3.4f, 3.1f, 20, new Vector2(0f, 0f));

        Material stone = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0xD8D2C4), 0f, 0.75f));
        Node node = Scene.AddMesh(tower.Build(Scene), stone, name: "lighthouse");
        node.Position = new Vector3(x, ground, z);

        Material lampGlass = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0xFFE7B0), 0f, 0.1f) with
        {
            Emissive = Palette.Rgb(0xFFD27A),
            EmissiveIntensity = 14f,
        });
        _beacon = Scene.AddMesh(Scene.CreateSphereGeometry(1.1f, 20, 12), lampGlass, parent: node, name: "lamp");
        _beacon.Position = new Vector3(0f, 16.4f, 0f);
        _beacon.CastShadow = false;

        _beaconLight = Scene.AddLight(
            Light.Spot(Palette.Rgb(0xFFD79A), 900f, 190f, 3.5f.ToRadians(), 9f.ToRadians()),
            parent: node,
            name: "beacon");
        _beaconLight.Position = new Vector3(0f, 16.4f, 0f);
    }

    private void ScatterRocks()
    {
        Material rock = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x5A5852), 0f, 0.85f));
        Geometry boulder = Scene.CreateSphereGeometry(1f, 14, 8);
        for (int i = 0; i < 40; i++)
        {
            float angle = Noise.Hash(i, 71) * MathF.Tau;
            float radius = 58f + (Noise.Hash(i, 73) * 34f);
            float x = MathF.Sin(angle) * radius;
            float z = MathF.Cos(angle) * radius;
            float height = IslandHeight(x, z);
            Node node = Scene.AddMesh(boulder, rock, name: $"rock-{i}");
            node.Position = new Vector3(x, height + 0.2f, z);
            float scale = 0.8f + (Noise.Hash(i, 79) * 2.6f);
            node.Scale = new Vector3(scale, scale * (0.5f + (Noise.Hash(i, 83) * 0.5f)), scale);
        }
    }

    private void ApplyWaterParameters()
    {
        Vector2 wind = World.WindVector;
        if (wind.LengthSquared() < 1e-4f)
        {
            wind = new Vector2(0.7f, 0.7f);
        }

        // Wind raises the sea on its own: the sliders set the shape, the weather
        // sets how hard it is blowing.
        float gain = 0.6f + (World.WindStrength * 1.8f);
        float wavelength = MathF.Max(P("waveLength"), 1f);
        _water.Update(options => options with
        {
            Custom0 = new Vector4(
                P("waveHeight") * gain,
                MathF.Tau / wavelength,
                P("waveSpeed") * 1.6f,
                P("choppiness")),
            Custom1 = new Vector4(wind.X, wind.Y, P("foam"), P("clarity")),
        });
    }

    private void ApplyFoliageWind()
    {
        _foliage.Update(options => options with
        {
            Custom0 = new Vector4(Palette.Rgb(0x4A3B28), World.WindDirection.ToRadians()),
            Custom1 = new Vector4(Palette.Rgb(0x2E5426), World.WindStrength),
        });
    }
}
