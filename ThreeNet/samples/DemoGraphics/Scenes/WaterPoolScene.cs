using System.Numerics;
using DemoGraphics.Framework;
using ThreeNet;
using ThreeNet.Avalonia;
using ThreeNet.Effects;

namespace DemoGraphics.Scenes;

/// <summary>
/// Simulated water: a wave equation on a height field, the surface that follows
/// it, caustics on the bottom of the pool and things floating on top. Click the
/// water to drop a stone in.
/// </summary>
/// <remarks>
/// This is <see cref="WaterSurface"/> doing all the work. The scene only decides
/// how big the pool is, what colour the water is and what floats on it; the
/// ripples, the normals, the caustics and the shoreline foam come from the
/// library, as does the height the floats read to bob.
/// </remarks>
public sealed class WaterPoolScene : DemoScene
{
    private const float PoolSize = 12f;

    private readonly List<Node> _floats = [];
    private readonly List<float> _floatSpin = [];
    private WaterSurface _water = null!;
    private Node _sun = null!;
    private float _rainClock;
    private int _splashes;

    public WaterPoolScene()
    {
        Declare(
            DemoParameter.Choice("style", "Water", 0, ["Pool", "Lake", "Ocean"]),
            DemoParameter.Slider("absorption", "Absorption", 0.55f, 0.05f, 3f, "/m",
                note: "How fast the water swallows light. Low is a clear pool, high is open sea."),
            DemoParameter.Slider("caustics", "Caustics", 1.6f, 0f, 4f,
                note: "Sunlight focused by the waves onto the bottom."),
            DemoParameter.Slider("foam", "Foam", 0.5f, 0f, 1.5f),
            DemoParameter.Slider("detail", "Fine ripples", 0.10f, 0f, 0.5f,
                note: "Chop too small for the simulation grid, added in the shader."),
            DemoParameter.Slider("displacement", "Wave height", 1f, 0f, 3f, "x"),
            DemoParameter.Slider("damping", "Damping", 0.994f, 0.95f, 0.9995f,
                note: "How much of a wave survives each step. Lower settles sooner."),
            DemoParameter.Slider("rain", "Rain", 0f, 0f, 400f, "/s"),
            DemoParameter.Toggle("floats", "Floating things", true),
            DemoParameter.Toggle("drip", "Steady drip", true, "One drop a second, so there is always something moving."));

        AddPreset("Still pool", ("style", 0f), ("absorption", 0.55f), ("caustics", 1.6f), ("foam", 0.4f), ("rain", 0f), ("damping", 0.994f));
        AddPreset("Rain on the pool", ("style", 0f), ("absorption", 0.5f), ("caustics", 2.2f), ("foam", 0.6f), ("rain", 160f), ("damping", 0.993f));
        AddPreset("Green lake", ("style", 1f), ("absorption", 1.1f), ("caustics", 0.8f), ("foam", 0.25f), ("detail", 0.14f));
        AddPreset("Open sea", ("style", 2f), ("absorption", 1.8f), ("caustics", 0.3f), ("foam", 1f), ("detail", 0.3f), ("displacement", 1.6f), ("damping", 0.9985f));
        AddPreset("Downpour", ("style", 1f), ("rain", 400f), ("foam", 1.1f), ("caustics", 0.6f), ("damping", 0.9975f));
    }

    public override string Id => "WAT";

    public override string Title => "Water simulation";

    public override string Category => "Simulation";

    public override string Summary =>
        "A wave equation on a height field: ripples that spread, reflect and die, with caustics underneath.";

    public override IReadOnlyList<string> Features =>
        ["Wave simulation", "Caustics", "Sky reflection", "Depth absorption", "Live textures", "Raycast picking"];

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        Shadows = true,
        ShadowMapSize = 2048,
        ShadowCascades = 3,
        ShadowDistance = 40f,
        ShadowSoftness = 2,
        Ssao = true,
        SsaoRadius = 0.4f,
        Bloom = true,
        BloomIntensity = 0.5f,
        BloomThreshold = 1.1f,
        Exposure = 1f,
    };

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, -0.4f, 0f);
        orbit.Distance = 13f;
        orbit.Yaw = 0.6f;
        orbit.Pitch = 0.42f;
        orbit.MinDistance = 2f;
        orbit.MaxDistance = 40f;
    }

    public override IReadOnlyList<CameraKey> CameraPath =>
    [
        new(new Vector3(0f, -0.4f, 0f), 13f, 0.6f, 0.42f, 0f),
        new(new Vector3(0f, -0.2f, 0f), 7f, 1.9f, 0.14f, 4f),
        new(new Vector3(0f, -0.6f, 0f), 16f, 3.6f, 0.62f, 8f),
        new(new Vector3(0f, -0.4f, 0f), 13f, 5.0f, 0.42f, 12f),
    ];

    public override IEnumerable<(string Label, string Value)> Metrics()
    {
        yield return ("Wave grid", $"{_water.Simulation.Resolution} x {_water.Simulation.Resolution} cells");
        yield return ("Steps", $"{_water.Simulation.Steps:N0}");
        yield return ("Centre height", $"{_water.Simulation.SampleHeight(0f, 0f) * 100f:F1} cm");
        yield return ("Splashes", _splashes.ToString());
    }

    protected override void OnBuild()
    {
        _floats.Clear();
        _floatSpin.Clear();
        _splashes = 0;

        Scene.Environment = SceneEnvironment.Default with
        {
            Background = Palette.Rgba(0x0E1620),
            Sky = SkyMode.Procedural,
            SunDirection = World.SunPosition,
            SkyIntensity = 1f,
            SkyHaze = 0.3f,
            SkyClouds = World.CloudCover,
            AmbientColor = new Vector3(0.45f, 0.58f, 0.74f),
            AmbientIntensity = 0.3f,
        };

        BuildPool();

        _water = new WaterSurface(Scene, Vector3.Zero, PoolSize, -1.6f, resolution: 160, segments: 160)
        {
            SunDirection = World.SunPosition,
        };

        // The floor goes under the water, so it takes the caustics shader.
        Material tiles = _water.AddFloor(MaterialOptions.Pbr(Palette.Rgba(0xB9BCB2), 0f, 0.55f) with
        {
            BaseColorMap = Procedural.Checker(Scene, 256, 16, 0xE8E6DE, 0x7FA7B4),
        });
        Node floor = Scene.AddMesh(Procedural.Ground(Scene, PoolSize, 1, uvScale: 6f), tiles, name: "pool floor");
        floor.Position = new Vector3(0f, -1.6f, 0f);

        BuildFloats();

        _sun = Scene.AddLight(Light.Directional(World.SunColor, World.SunIntensity) with { CastShadow = true }, name: "sun");
        _sun.Position = World.SunPosition * 30f;
        _sun.LookAt(Vector3.Zero);
        Scene.AddLight(Light.Ambient(new Vector3(0.5f, 0.62f, 0.78f), 0.3f), name: "sky");

        // Something has to disturb it, or the first frame is a mirror.
        _water.Splash(new Vector3(-1.4f, 0f, 0.8f), 0.3f, 0.09f);
        _water.Splash(new Vector3(2.2f, 0f, -1.6f), 0.22f, 0.06f);
    }

    /// <summary>The walls and the deck: what makes the water read as a pool.</summary>
    private void BuildPool()
    {
        Material stone = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0xC9C6BC), 0f, 0.75f));
        Material deck = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x8E8577), 0f, 0.9f));

        Node ground = Scene.AddMesh(Procedural.Ground(Scene, 80f, 1), deck, name: "deck");
        ground.Position = new Vector3(0f, -0.02f, 0f);

        const float Wall = 0.5f;
        const float Half = PoolSize * 0.5f;
        for (int i = 0; i < 4; i++)
        {
            bool alongX = i < 2;
            float sign = i % 2 == 0 ? 1f : -1f;
            Node wall = Scene.AddMesh(
                Scene.CreateBoxGeometry(alongX ? PoolSize + (Wall * 2f) : Wall, 1.9f, alongX ? Wall : PoolSize + (Wall * 2f)),
                stone,
                name: $"wall-{i}");
            wall.Position = alongX
                ? new Vector3(0f, -0.75f, sign * (Half + (Wall * 0.5f)))
                : new Vector3(sign * (Half + (Wall * 0.5f)), -0.75f, 0f);
        }
    }

    /// <summary>Balls that ride the surface, which is what proves it is simulated.</summary>
    private void BuildFloats()
    {
        uint[] colours = [0xE8564A, 0xF2C14E, 0x4FA3D1, 0x7FC77F, 0xE59BC4];
        Geometry ball = Scene.CreateSphereGeometry(0.3f, 24, 16);
        for (int i = 0; i < 5; i++)
        {
            Material material = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(colours[i]), 0f, 0.35f));
            Node node = Scene.AddMesh(ball, material, name: $"float-{i}");
            float angle = MathF.Tau * i / 5f;
            float radius = 1.6f + (i * 0.7f);
            node.Position = new Vector3(MathF.Sin(angle) * radius, 0f, MathF.Cos(angle) * radius);
            _floats.Add(node);
            _floatSpin.Add(0.3f + (Noise.Hash(i, 17) * 0.9f));
        }
    }

    protected override void OnApplyParameters()
    {
        WaterStyle style = I("style") switch
        {
            1 => WaterStyle.Lake,
            2 => WaterStyle.Ocean,
            _ => WaterStyle.Pool,
        };

        _water.Style = style with
        {
            Absorption = P("absorption"),
            Caustics = P("caustics"),
            Foam = P("foam"),
            Detail = P("detail"),
            Displacement = P("displacement"),
        };

        _water.Simulation.Damping = P("damping");
        _water.Simulation.RainRate = P("rain");

        foreach (Node node in _floats)
        {
            node.Visible = B("floats");
        }
    }

    protected override void OnApplyEnvironment()
    {
        Scene.Environment = Scene.Environment with
        {
            SunDirection = World.SunPosition,
            SkyClouds = World.CloudCover,
            SkyHaze = Math.Clamp(0.2f + (World.FogDensity * 14f), 0.05f, 1f),
        };

        if (_water is not null)
        {
            _water.SunDirection = World.SunPosition;
        }

        if (_sun is not null)
        {
            Light light = _sun.Light!.Value;
            light.Color = World.SunColor;
            light.Intensity = World.SunIntensity;
            _sun.Light = light;
            _sun.Position = World.SunPosition * 30f;
            _sun.LookAt(Vector3.Zero);
        }
    }

    public override void Update(float deltaSeconds, double totalSeconds)
    {
        if (B("drip"))
        {
            _rainClock += deltaSeconds;
            if (_rainClock >= 1.1f)
            {
                _rainClock = 0f;
                float angle = (float)(totalSeconds * 1.7);
                _water.Splash(
                    new Vector3(MathF.Sin(angle) * 3.4f, 0f, MathF.Cos(angle * 1.31f) * 3.4f),
                    0.16f,
                    0.05f);
            }
        }

        _water.Update(deltaSeconds);

        if (B("floats"))
        {
            for (int i = 0; i < _floats.Count; i++)
            {
                Node node = _floats[i];
                Vector3 position = node.Position;
                // The ball sits in the water, not on it: half a radius down.
                position.Y = _water.HeightAt(position.X, position.Z) - 0.12f;
                node.Position = position;

                // And it leans the way the surface does, which is the part that
                // sells the simulation.
                Vector3 normal = _water.NormalAt(position.X, position.Z);
                node.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)totalSeconds * _floatSpin[i])
                    * ShortestArc(Vector3.UnitY, normal);
            }
        }
    }

    /// <summary>The rotation that takes <paramref name="from"/> onto <paramref name="to"/>.</summary>
    private static Quaternion ShortestArc(Vector3 from, Vector3 to)
    {
        Vector3 axis = Vector3.Cross(from, to);
        float length = axis.Length();
        if (length < 1e-5f)
        {
            return Quaternion.Identity;
        }

        return Quaternion.CreateFromAxisAngle(axis / length, MathF.Asin(Math.Clamp(length, -1f, 1f)));
    }

    public override void OnPick(RayHit? hit)
    {
        if (hit is not { } landed)
        {
            return;
        }

        // Anything in the pool makes a splash, the water itself included.
        _water.Splash(landed.Point, 0.26f, 0.11f);
        _splashes++;
    }

    protected override void OnUnload()
    {
        _floats.Clear();
        _floatSpin.Clear();
        _water = null!;
        _sun = null!;
    }
}
