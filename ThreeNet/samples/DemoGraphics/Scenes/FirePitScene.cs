using System.Numerics;
using DemoGraphics.Framework;
using ThreeNet;
using ThreeNet.Avalonia;
using ThreeNet.Effects;

namespace DemoGraphics.Scenes;

/// <summary>
/// Volumetric fire: the flame is a box whose fragments ray march a procedural
/// volume, with embers and smoke around it and a light that gutters with it.
/// </summary>
/// <remarks>
/// Two ways of making fire stand side by side here. The flame itself is
/// <see cref="FireEffect"/>, a ray marched volume - it has depth, it reads
/// right from any angle and it needs no texture. The embers and the smoke are
/// <see cref="ParticleEffect"/>, thousands of camera facing quads rebuilt every
/// frame. Neither replaces the other: a volume cannot throw a spark across the
/// yard, and a billboard cannot look like the inside of a flame.
/// </remarks>
public sealed class FirePitScene : DemoScene
{
    private readonly ParticleEffect _embers = new(2400, 19);
    private readonly ParticleEffect _smoke = new(1600, 41);

    private FireEffect _fire = null!;
    private readonly List<FireEffect> _torches = [];
    private Geometry _emberGeometry = null!;
    private Geometry _smokeGeometry = null!;
    private Material _emberMaterial = null!;
    private Node _fireLight = null!;
    private Shader _flameShader = null!;

    public FirePitScene()
    {
        Declare(
            DemoParameter.Choice("style", "Flame", 0, ["Campfire", "Torch", "Candle", "Witchfire"]),
            DemoParameter.Slider("height", "Flame height", 1.8f, 0.3f, 5f, "m"),
            DemoParameter.Slider("width", "Flame width", 1.1f, 0.2f, 4f, "m"),
            DemoParameter.Slider("magnitude", "Turbulence", 1.3f, 0.2f, 3f,
                note: "How far the noise tears the flame apart. Low is a candle, high is a bonfire."),
            DemoParameter.Slider("speed", "Rise", 0.32f, 0.05f, 1.2f),
            DemoParameter.Slider("detail", "Flame detail", 1f, 0.4f, 4f,
                note: "Size of the turbulence. Larger numbers make a finer, busier flame."),
            DemoParameter.Slider("brightness", "Brightness", 2.4f, 0.5f, 6f, "x"),
            DemoParameter.Slider("embers", "Embers", 260f, 0f, 1500f, "/s"),
            DemoParameter.Slider("smoke", "Smoke", 90f, 0f, 600f, "/s"),
            DemoParameter.Toggle("torches", "Torches around the yard", true),
            DemoParameter.Toggle("light", "Light from the fire", true));

        AddPreset("Campfire", null, 21.5f, ("style", 0f), ("height", 1.8f), ("width", 1.1f), ("magnitude", 1.3f), ("speed", 0.32f), ("brightness", 2.4f), ("embers", 260f), ("smoke", 90f));
        AddPreset("Bonfire", null, 22f, ("style", 0f), ("height", 3.6f), ("width", 2.2f), ("magnitude", 2.1f), ("speed", 0.45f), ("brightness", 3.2f), ("embers", 900f), ("smoke", 260f));
        AddPreset("Torch", null, 21f, ("style", 1f), ("height", 0.9f), ("width", 0.35f), ("magnitude", 1.6f), ("speed", 0.45f), ("detail", 1.6f), ("embers", 90f), ("smoke", 30f));
        AddPreset("Candle", null, 21f, ("style", 2f), ("height", 0.35f), ("width", 0.14f), ("magnitude", 0.75f), ("speed", 0.18f), ("detail", 2.4f), ("embers", 0f), ("smoke", 8f), ("torches", 0f));
        AddPreset("Witchfire", null, 23f, ("style", 3f), ("height", 2.4f), ("width", 1.3f), ("magnitude", 1.5f), ("speed", 0.4f), ("brightness", 2.8f), ("embers", 420f), ("smoke", 40f));
    }

    public override string Id => "FIR";

    public override string Title => "Volumetric fire";

    public override string Category => "Simulation";

    public override string Summary =>
        "A ray marched flame with embers and smoke, and a light that gutters with it.";

    public override IReadOnlyList<string> Features =>
        ["Ray marched volume", "Simplex turbulence", "Additive blending", "Particles", "Bloom", "Flickering light"];

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        Shadows = true,
        ShadowMapSize = 1024,
        ShadowCascades = 2,
        ShadowDistance = 26f,
        Ssao = true,
        SsaoIntensity = 1.1f,
        Bloom = true,
        BloomIntensity = 0.95f,
        BloomThreshold = 0.75f,
        Exposure = 1f,
    };

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 1.1f, 0f);
        orbit.Distance = 7.5f;
        orbit.Yaw = 0.55f;
        orbit.Pitch = 0.12f;
        orbit.MinDistance = 1.5f;
        orbit.MaxDistance = 30f;
    }

    public override IReadOnlyList<CameraKey> CameraPath =>
    [
        new(new Vector3(0f, 1.1f, 0f), 7.5f, 0.55f, 0.12f, 0f),
        new(new Vector3(0f, 1.4f, 0f), 3.4f, 2.1f, 0.05f, 4f),
        new(new Vector3(0f, 1.0f, 0f), 11f, 3.9f, 0.34f, 8f),
        new(new Vector3(0f, 1.1f, 0f), 7.5f, 5.6f, 0.12f, 12f),
    ];

    public override IEnumerable<(string Label, string Value)> Metrics()
    {
        yield return ("Flame", $"{_torches.Count + 1} volumes, 24 steps each");
        yield return ("Embers", $"{_embers.Alive:N0} alive");
        yield return ("Smoke", $"{_smoke.Alive:N0} alive");
        yield return ("Flicker", $"{_fire.Flicker:F2} x");
    }

    protected override void OnBuild()
    {
        _torches.Clear();

        Scene.Environment = SceneEnvironment.Default with
        {
            Background = Palette.Rgba(0x05070B),
            Sky = SkyMode.Procedural,
            SunDirection = World.SunPosition,
            SkyIntensity = 0.9f,
            SkyHaze = 0.25f,
            SkyClouds = World.CloudCover,
            AmbientColor = new Vector3(0.32f, 0.40f, 0.62f),
            AmbientIntensity = 0.07f,
            FogColor = new Vector3(0.03f, 0.04f, 0.06f),
            FogDensity = 0.012f,
            FogStart = 8f,
        };

        BuildYard();

        // One compilation for every flame in the yard.
        _flameShader = Scene.CreateShader(EffectShaders.Fire, ShaderLanguage.Wgsl, "flame");
        _fire = new FireEffect(Scene, new Vector3(0f, 0.22f, 0f), new Vector3(1.1f, 1.8f, 1.1f), shader: _flameShader);

        Shader sprite = Scene.CreateShader(EffectShaders.Particle, ShaderLanguage.Wgsl, "ember");
        Shader smoke = Scene.CreateShader(EffectShaders.Smoke, ShaderLanguage.Wgsl, "smoke");

        _emberGeometry = _embers.CreateGeometry(Scene);
        _emberMaterial = Scene.CreateMaterial(ParticleEffect.GlowMaterial(
            sprite,
            new Vector3(1f, 0.62f, 0.18f),
            new Vector3(0.55f, 0.06f, 0.01f),
            brightness: 3.2f) with
        {
            Custom2 = new Vector4(1f, 0.35f, 0f, 0f),
        });
        Scene.AddMesh(_emberGeometry, _emberMaterial, name: "embers").CastShadow = false;

        _smokeGeometry = _smoke.CreateGeometry(Scene);
        Material smokeMaterial = Scene.CreateMaterial(
            ParticleEffect.SmokeMaterial(smoke, new Vector3(0.10f, 0.10f, 0.12f), 0.30f) with
            {
                Custom1 = new Vector4(0.34f, 0f, 0f, 0f),
            });
        Scene.AddMesh(_smokeGeometry, smokeMaterial, name: "smoke").CastShadow = false;

        _fireLight = Scene.AddLight(Light.Point(Palette.Rgb(0xFF8A2E), 22f, 16f), name: "fire light");
        _fireLight.Position = new Vector3(0f, 1f, 0f);

        Scene.AddLight(Light.Ambient(new Vector3(0.36f, 0.46f, 0.72f), 0.06f), name: "night sky");
        Node moon = Scene.AddLight(Light.Directional(Palette.Rgb(0x9FB6E8), 0.3f) with { CastShadow = true }, name: "moon");
        moon.Position = new Vector3(-8f, 14f, 7f);
        moon.LookAt(Vector3.Zero);

        BuildTorches();
    }

    private void BuildYard()
    {
        Material ground = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x2B2822), 0f, 0.9f));
        Scene.AddMesh(Procedural.Ground(Scene, 70f, 1), ground, name: "ground");

        Material stone = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x4E4A44), 0f, 0.82f));
        Geometry rock = Scene.CreateSphereGeometry(1f, 12, 8);
        for (int i = 0; i < 14; i++)
        {
            float angle = MathF.Tau * i / 14f;
            Node node = Scene.AddMesh(rock, stone, name: $"ring-{i}");
            node.Position = new Vector3(MathF.Sin(angle) * 1.05f, 0.1f, MathF.Cos(angle) * 1.05f);
            float scale = 0.2f + (Noise.Hash(i, 91) * 0.12f);
            node.Scale = new Vector3(scale, scale * 0.8f, scale);
        }

        Material wood = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x33241A), 0f, 0.92f));
        Geometry log = Scene.CreateCylinderGeometry(0.1f, 0.12f, 1.6f, 12);
        for (int i = 0; i < 5; i++)
        {
            Node node = Scene.AddMesh(log, wood, name: $"log-{i}");
            node.Position = new Vector3(0f, 0.2f, 0f);
            node.EulerAngles = new Vector3(1.32f, MathF.Tau * i / 5f, 0f);
        }

        // Something for the firelight to fall on.
        Material plank = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x5A4631), 0f, 0.86f));
        for (int i = 0; i < 3; i++)
        {
            float angle = (MathF.Tau * i / 3f) + 0.4f;
            Node bench = Scene.AddMesh(Scene.CreateBoxGeometry(1.8f, 0.12f, 0.4f), plank, name: $"bench-{i}");
            bench.Position = new Vector3(MathF.Sin(angle) * 3.1f, 0.42f, MathF.Cos(angle) * 3.1f);
            bench.EulerAngles = new Vector3(0f, -angle, 0f);

            for (int leg = -1; leg <= 1; leg += 2)
            {
                Node support = Scene.AddMesh(Scene.CreateBoxGeometry(0.12f, 0.42f, 0.3f), plank, name: $"bench-{i}-leg");
                support.Position = bench.Position + new Vector3(MathF.Cos(angle) * leg * 0.7f, -0.21f, -MathF.Sin(angle) * leg * 0.7f);
                support.EulerAngles = bench.EulerAngles;
            }
        }
    }

    private void BuildTorches()
    {
        Material iron = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x24201C), 0.7f, 0.5f));
        Geometry post = Scene.CreateCylinderGeometry(0.05f, 0.07f, 2.1f, 10);
        for (int i = 0; i < 4; i++)
        {
            float angle = (MathF.Tau * i / 4f) + 0.78f;
            Vector3 at = new(MathF.Sin(angle) * 6.2f, 0f, MathF.Cos(angle) * 6.2f);

            Node pole = Scene.AddMesh(post, iron, name: $"torch-{i}");
            pole.Position = at + new Vector3(0f, 1.05f, 0f);

            FireEffect flame = new(Scene, at + new Vector3(0f, 2f, 0f), new Vector3(0.34f, 0.8f, 0.34f), shader: _flameShader)
            {
                Style = FireStyle.Torch,
            };
            _torches.Add(flame);

            Node light = Scene.AddLight(Light.Point(Palette.Rgb(0xFF9440), 8f, 9f), name: $"torch-light-{i}");
            light.Position = at + new Vector3(0f, 2.3f, 0f);
        }
    }

    protected override void OnApplyParameters()
    {
        FireStyle style = I("style") switch
        {
            1 => FireStyle.Torch,
            2 => FireStyle.Candle,
            3 => FireStyle.Magic,
            _ => FireStyle.Campfire,
        };

        float detail = P("detail");
        _fire.Style = style with
        {
            Magnitude = P("magnitude"),
            Speed = P("speed"),
            NoiseScale = new Vector3(detail, detail * 2f, detail),
            Brightness = P("brightness"),
        };
        _fire.Size = new Vector3(P("width"), P("height"), P("width"));
        _fire.Node.Position = new Vector3(0f, 0.22f + (P("height") * 0.5f), 0f);

        // The embers take their colour from the flame they come off.
        Vector3 birth = style.Tint * new Vector3(1f, 0.62f, 0.18f);
        Vector3 death = style.Tint * new Vector3(0.55f, 0.06f, 0.01f);
        _emberMaterial.Update(options => options with
        {
            Custom0 = new Vector4(birth, 1.6f),
            Custom1 = new Vector4(death, 3.2f),
        });

        _embers.Rate = P("embers");
        _embers.Origin = new Vector3(0f, 0.3f, 0f);
        _embers.Shape = EmitterShape.Disc;
        _embers.EmitterRadius = P("width") * 0.4f;
        _embers.Lifetime = 1.6f + (P("height") * 0.4f);
        _embers.Size = 0.05f;
        _embers.Growth = 0.35f;
        _embers.Rise = 2.2f + (P("speed") * 4f);
        _embers.Spread = 0.5f;
        // Embers float: hot air carries them up before they cool and drop.
        _embers.Gravity = new Vector3(0f, 1.1f, 0f);
        _embers.Drag = 0.9f;
        _embers.Turbulence = 2.2f;
        _embers.TurbulenceScale = 0.8f;
        _embers.StretchBySpeed = 0.05f;

        _smoke.Rate = P("smoke");
        _smoke.Origin = new Vector3(0f, 0.3f + P("height"), 0f);
        _smoke.Shape = EmitterShape.Disc;
        _smoke.EmitterRadius = P("width") * 0.45f;
        _smoke.Lifetime = 5.5f;
        _smoke.Size = P("width") * 0.75f;
        _smoke.Growth = 3.6f;
        _smoke.Rise = 1.3f;
        _smoke.Spread = 0.3f;
        _smoke.Gravity = new Vector3(0f, 0.25f, 0f);
        _smoke.Drag = 1.2f;
        _smoke.Turbulence = 0.9f;
        _smoke.TurbulenceScale = 0.35f;
        _smoke.SpinRate = 0.4f;

        foreach (FireEffect torch in _torches)
        {
            torch.Node.Visible = B("torches");
        }

        Light light = _fireLight.Light!.Value;
        light.Enabled = B("light");
        light.Color = style.Tint.X > 0.8f ? Palette.Rgb(0xFF8A2E) : Vector3.Normalize(style.Tint) * 1.2f;
        _fireLight.Light = light;
    }

    protected override void OnApplyEnvironment()
    {
        Vector2 wind = World.WindVector;
        _embers.Wind = new Vector3(wind.X * 0.5f, 0f, wind.Y * 0.5f);
        _smoke.Wind = new Vector3(wind.X, 0f, wind.Y);

        Scene.Environment = Scene.Environment with
        {
            SunDirection = World.SunPosition,
            SkyClouds = World.CloudCover,
        };
    }

    public override void Update(float deltaSeconds, double totalSeconds)
    {
        _fire.Update(deltaSeconds);
        foreach (FireEffect torch in _torches)
        {
            torch.Update(deltaSeconds);
        }

        _embers.Update(deltaSeconds, totalSeconds);
        _smoke.Update(deltaSeconds, totalSeconds);

        Matrix4x4 cameraWorld = Viewer.WorldMatrix;
        _embers.Upload(_emberGeometry, cameraWorld);
        _smoke.Upload(_smokeGeometry, cameraWorld);

        if (B("light"))
        {
            // The light gutters with the flame, and leans about as it does.
            Light light = _fireLight.Light!.Value;
            light.Intensity = (8f + (P("height") * 7f)) * _fire.Flicker;
            _fireLight.Light = light;
            _fireLight.Position = new Vector3(
                (_fire.Flicker - 1f) * 0.3f,
                0.5f + (P("height") * 0.4f),
                (_fire.Flicker - 1f) * 0.22f);
        }
    }

    protected override void OnUnload()
    {
        _embers.Clear();
        _smoke.Clear();
        _torches.Clear();
        _fire = null!;
        _flameShader = null!;
    }
}
