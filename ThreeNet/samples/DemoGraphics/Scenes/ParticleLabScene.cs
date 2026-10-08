using System.Numerics;
using DemoGraphics.Framework;
using ThreeNet;
using ThreeNet.Avalonia;
using ThreeNet.Effects;

namespace DemoGraphics.Scenes;

/// <summary>
/// The particle system with its lid off: emitter shapes, curl noise turbulence,
/// attractors, a floor to bounce off and colour over life, on three groups that
/// are rewritten and re-uploaded every frame.
/// </summary>
/// <remarks>
/// Everything here is <see cref="ParticleEffect"/> from the library. The flame,
/// the smoke and the sparks are three instances of the same class with different
/// numbers, which is the point: one draw call each, no assets, and a shape that
/// comes from how the particles move rather than from what they are painted
/// with. <see cref="FirePitScene"/> shows the other way of making fire.
/// </remarks>
public sealed class ParticleLabScene : DemoScene
{
    private static readonly string[] ShapeNames = ["Point", "Box", "Sphere", "Disc", "Cone", "Ring"];

    private readonly ParticleEffect _flame = new(4000, 11);
    private readonly ParticleEffect _smoke = new(2500, 23);
    private readonly ParticleEffect _sparks = new(1500, 37);

    private Geometry _flameGeometry = null!;
    private Geometry _smokeGeometry = null!;
    private Geometry _sparkGeometry = null!;
    private Material _flameMaterial = null!;
    private Material _sparkMaterial = null!;
    private Node _fireLight = null!;
    private float _flicker;

    public ParticleLabScene()
    {
        Declare(
            DemoParameter.Choice("preset", "Effect", 0, ["Campfire", "Torch", "Smoke column", "Sparks", "Fireflies", "Snowfall", "Explosion", "Vortex"]),
            DemoParameter.Choice("shape", "Emitter", 3, ShapeNames),
            DemoParameter.Slider("rate", "Emission", 700f, 0f, 3000f, "/s", 10f),
            DemoParameter.Slider("life", "Lifetime", 1.8f, 0.2f, 8f, "s"),
            DemoParameter.Slider("size", "Particle size", 0.42f, 0.03f, 2f, "m"),
            DemoParameter.Slider("rise", "Launch speed", 2.6f, 0f, 12f, "m/s"),
            DemoParameter.Slider("gravity", "Gravity", -1.4f, -12f, 12f, "m/s2"),
            DemoParameter.Slider("turbulence", "Turbulence", 0.8f, 0f, 8f,
                note: "Curl noise: divergence free, so particles swirl through it instead of piling up."),
            DemoParameter.Slider("attract", "Attractor", 0f, -6f, 6f,
                note: "A point above the fire that pulls particles in, or pushes them away."),
            DemoParameter.Slider("stretch", "Stretch by speed", 0f, 0f, 0.4f),
            DemoParameter.Slider("wind", "Wind pickup", 0.6f, 0f, 2f),
            DemoParameter.Toggle("radial", "Fire outwards", false, "Launch along the spawn offset: what an explosion does."),
            DemoParameter.Toggle("vortex", "Spin the attractor", false),
            DemoParameter.Toggle("floor", "Bounce off the ground", false),
            DemoParameter.Toggle("smoke", "Smoke", true),
            DemoParameter.Toggle("sparks", "Sparks", true),
            DemoParameter.Toggle("light", "Light from the fire", true));

        AddPreset("Campfire", ("preset", 0f), ("shape", 3f), ("rate", 700f), ("life", 1.8f), ("size", 0.42f), ("rise", 2.6f), ("gravity", -1.4f), ("turbulence", 0.8f), ("attract", 0f), ("radial", 0f), ("smoke", 1f), ("sparks", 1f), ("floor", 0f));
        AddPreset("Torch", ("preset", 1f), ("shape", 3f), ("rate", 420f), ("life", 0.9f), ("size", 0.22f), ("rise", 3.6f), ("gravity", -2.4f), ("turbulence", 1.2f), ("radial", 0f), ("sparks", 1f));
        AddPreset("Smoke column", ("preset", 2f), ("shape", 3f), ("rate", 900f), ("life", 5.5f), ("size", 1.1f), ("rise", 1.6f), ("gravity", -0.4f), ("turbulence", 0.6f), ("smoke", 1f), ("sparks", 0f));
        AddPreset("Sparks", ("preset", 3f), ("shape", 0f), ("rate", 1500f), ("life", 1.1f), ("size", 0.07f), ("rise", 6f), ("gravity", 7f), ("stretch", 0.12f), ("floor", 1f), ("smoke", 0f), ("sparks", 1f));
        AddPreset("Fireflies", ("preset", 4f), ("shape", 1f), ("rate", 90f), ("life", 6f), ("size", 0.09f), ("rise", 0.3f), ("gravity", -0.05f), ("turbulence", 1.8f), ("smoke", 0f), ("sparks", 0f));
        AddPreset("Snowfall", ("preset", 5f), ("shape", 1f), ("rate", 1400f), ("life", 7f), ("size", 0.12f), ("rise", 0.1f), ("gravity", 0.6f), ("turbulence", 0.5f), ("wind", 1.4f), ("smoke", 0f), ("sparks", 0f));
        AddPreset("Explosion", ("preset", 6f), ("shape", 2f), ("rate", 0f), ("life", 1.6f), ("size", 0.5f), ("rise", 9f), ("gravity", -3f), ("turbulence", 2.5f), ("radial", 1f), ("stretch", 0.08f), ("smoke", 1f), ("sparks", 1f));
        AddPreset("Vortex", ("preset", 7f), ("shape", 5f), ("rate", 1200f), ("life", 4f), ("size", 0.14f), ("rise", 1.2f), ("gravity", 0.4f), ("turbulence", 0.4f), ("attract", 3.5f), ("vortex", 1f), ("smoke", 0f), ("sparks", 0f));

        AddAction("Set it off", () =>
        {
            _flame.Burst(900);
            _sparks.Burst(400);
            _smoke.Burst(260);
        });
    }

    public override string Id => "VFX";

    public override string Title => "Particle laboratory";

    public override string Category => "Simulation";

    public override string Summary =>
        "Emitter shapes, curl noise, attractors and colour over life, on geometry rewritten every frame.";

    public override IReadOnlyList<string> Features =>
        ["Dynamic geometry", "Emitter shapes", "Curl noise", "Attractors", "Additive blending", "Bloom"];

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        Shadows = true,
        ShadowMapSize = 1024,
        ShadowCascades = 2,
        ShadowDistance = 30f,
        Ssao = true,
        Bloom = true,
        BloomIntensity = 0.9f,
        BloomThreshold = 0.8f,
        Exposure = 1f,
    };

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 1.6f, 0f);
        orbit.Distance = 9f;
        orbit.Yaw = 0.5f;
        orbit.Pitch = 0.1f;
    }

    public override IEnumerable<(string Label, string Value)> Metrics()
    {
        int alive = _flame.Alive + _smoke.Alive + _sparks.Alive;
        yield return ("Live particles", $"{alive:N0}");
        yield return ("Quads rebuilt", $"{alive * 2:N0} triangles/frame");
        yield return ("Groups", $"{1 + (B("smoke") ? 1 : 0) + (B("sparks") ? 1 : 0)} draw calls");
        yield return ("Emitter", ShapeNames[Math.Clamp(I("shape"), 0, ShapeNames.Length - 1)]);
    }

    protected override void OnBuild()
    {
        Scene.Environment = SceneEnvironment.Default with
        {
            Background = Palette.Rgba(0x07090D),
            AmbientColor = new Vector3(0.35f, 0.42f, 0.6f),
            AmbientIntensity = 0.07f,
            FogColor = new Vector3(0.04f, 0.05f, 0.07f),
            FogDensity = 0.01f,
            FogStart = 10f,
        };

        Material ground = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x2C2A26), 0f, 0.85f));
        Scene.AddMesh(Procedural.Ground(Scene, 60f, 1), ground, name: "ground");

        // A fire ring and some logs, so the flame has somewhere to be.
        Material stone = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x4E4A44), 0f, 0.8f));
        Geometry rock = Scene.CreateSphereGeometry(1f, 12, 8);
        for (int i = 0; i < 12; i++)
        {
            float angle = MathF.Tau * i / 12f;
            Node node = Scene.AddMesh(rock, stone, name: $"ring-{i}");
            node.Position = new Vector3(MathF.Sin(angle) * 0.95f, 0.12f, MathF.Cos(angle) * 0.95f);
            float scale = 0.2f + (Noise.Hash(i, 91) * 0.12f);
            node.Scale = new Vector3(scale, scale * 0.8f, scale);
        }

        Material wood = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x3A2A1C), 0f, 0.9f));
        for (int i = 0; i < 4; i++)
        {
            Node log = Scene.AddMesh(Scene.CreateCylinderGeometry(0.1f, 0.12f, 1.5f, 12), wood, name: $"log-{i}");
            log.Position = new Vector3(0f, 0.18f, 0f);
            log.EulerAngles = new Vector3(1.35f, MathF.Tau * i / 4f, 0f);
        }

        // ------------------------------------------------------------- groups
        Shader sprite = Scene.CreateShader(EffectShaders.Particle, ShaderLanguage.Wgsl, "particle");
        Shader smokeShader = Scene.CreateShader(EffectShaders.Smoke, ShaderLanguage.Wgsl, "smoke");

        _flameGeometry = _flame.CreateGeometry(Scene);
        _flameMaterial = Scene.CreateMaterial(ParticleEffect.GlowMaterial(
            sprite,
            Palette.Rgb(0xFFB45A),
            Palette.Rgb(0xC2340A),
            brightness: 3.4f));
        Scene.AddMesh(_flameGeometry, _flameMaterial, name: "flame").CastShadow = false;

        _smokeGeometry = _smoke.CreateGeometry(Scene);
        Material smokeMaterial = Scene.CreateMaterial(
            ParticleEffect.SmokeMaterial(smokeShader, new Vector3(0.09f, 0.09f, 0.11f), 0.28f) with
            {
                Custom1 = new Vector4(0.32f, 0f, 0f, 0f),
            });
        Scene.AddMesh(_smokeGeometry, smokeMaterial, name: "smoke").CastShadow = false;

        _sparkGeometry = _sparks.CreateGeometry(Scene);
        _sparkMaterial = Scene.CreateMaterial(ParticleEffect.GlowMaterial(
            sprite,
            Palette.Rgb(0xFFF0C0),
            Palette.Rgb(0xFF6A14),
            brightness: 5f) with
        {
            // Sparks wink as they tumble.
            Custom2 = new Vector4(1f, 0.45f, 0f, 0f),
        });
        Scene.AddMesh(_sparkGeometry, _sparkMaterial, name: "sparks").CastShadow = false;

        _fireLight = Scene.AddLight(Light.Point(Palette.Rgb(0xFF8A2E), 26f, 14f), name: "fire light");
        _fireLight.Position = new Vector3(0f, 0.9f, 0f);

        Scene.AddLight(Light.Ambient(new Vector3(0.4f, 0.5f, 0.75f), 0.06f), name: "night sky");
        Node moon = Scene.AddLight(Light.Directional(Palette.Rgb(0x9FB6E8), 0.25f) with { CastShadow = true }, name: "moon");
        moon.Position = new Vector3(-8f, 12f, 6f);
        moon.LookAt(Vector3.Zero);
    }

    protected override void OnApplyParameters()
    {
        float rate = P("rate");
        float life = P("life");
        float size = P("size");
        float rise = P("rise");
        float gravity = P("gravity");
        float turbulence = P("turbulence");
        EmitterShape shape = (EmitterShape)Math.Clamp(I("shape"), 0, 5);
        bool radial = B("radial");

        Configure(_flame, rate, life, size, rise, gravity, turbulence, shape, radial);
        _flame.EmitterRadius = MathF.Max(0.08f, size * 0.7f);
        _flame.Growth = 1.8f;
        _flame.StretchBySpeed = P("stretch");

        // Smoke is what the flame becomes: slower, bigger, longer lived.
        Configure(_smoke, B("smoke") ? rate * 0.35f : 0f, life * 2.6f, size * 1.7f, rise * 0.75f, gravity * 0.3f, turbulence * 0.6f, shape, radial);
        _smoke.EmitterRadius = MathF.Max(0.1f, size);
        _smoke.Growth = 3.4f;
        _smoke.SpinRate = 0.5f;

        Configure(_sparks, B("sparks") ? rate * 0.12f : 0f, life * 0.7f, MathF.Max(0.03f, size * 0.18f), rise * 1.9f, gravity + 2.5f, turbulence * 1.4f, shape, radial);
        _sparks.EmitterRadius = 0.2f;
        _sparks.Growth = 0.4f;
        _sparks.StretchBySpeed = MathF.Max(P("stretch"), 0.04f);

        int preset = I("preset");
        bool cold = preset is 4 or 5;
        (uint birth, uint death) = preset switch
        {
            4 => (0xD8FF8Au, 0x3C8C2Au),   // fireflies
            5 => (0xFFFFFFu, 0xBFD4EEu),   // snow
            7 => (0x9ADBFFu, 0x2A49B0u),   // vortex
            _ => (0xFFB45Au, 0xC2340Au),
        };

        _flameMaterial.Update(options => options with
        {
            Custom0 = new Vector4(Palette.Rgb(birth), 1.6f),
            Custom1 = new Vector4(Palette.Rgb(death), cold ? 1.6f : 3.4f),
            AlphaMode = cold && preset == 5 ? AlphaMode.Blend : AlphaMode.Additive,
            BaseColor = new Vector4(1f, 1f, 1f, cold && preset == 5 ? 0.75f : 1f),
        });

        Light light = _fireLight.Light!.Value;
        light.Enabled = B("light");
        light.Color = Palette.Rgb(birth);
        _fireLight.Light = light;
    }

    /// <summary>The knobs every group shares, so the three stay in step.</summary>
    private void Configure(
        ParticleEffect particles,
        float rate,
        float life,
        float size,
        float rise,
        float gravity,
        float turbulence,
        EmitterShape shape,
        bool radial)
    {
        particles.Rate = rate;
        particles.Lifetime = life;
        particles.Size = size;
        particles.Rise = rise;
        particles.Gravity = new Vector3(0f, gravity, 0f);
        particles.Shape = shape;
        particles.Radial = radial;
        particles.Turbulence = turbulence;
        particles.TurbulenceScale = 0.7f;
        particles.Origin = new Vector3(0f, 0.3f, 0f);
        particles.EmitterSize = new Vector3(3f, 2.5f, 3f);
        particles.ConeAngle = 0.5f;
        particles.FloorHeight = B("floor") ? 0.02f : null;

        particles.Attractors.Clear();
        float strength = P("attract");
        if (MathF.Abs(strength) > 0.01f)
        {
            particles.Attractors.Add(new Attractor(new Vector3(0f, 3.2f, 0f), strength, 12f, B("vortex")));
        }
    }

    protected override void OnApplyEnvironment()
    {
        Vector2 wind = World.WindVector;
        float pickup = P("wind");
        _flame.Wind = new Vector3(wind.X, 0f, wind.Y);
        _flame.WindInfluence = pickup;
        _smoke.Wind = _flame.Wind;
        _smoke.WindInfluence = pickup * 1.6f;
        _sparks.Wind = _flame.Wind;
        _sparks.WindInfluence = pickup * 0.8f;
    }

    public override void Update(float deltaSeconds, double totalSeconds)
    {
        _flame.Update(deltaSeconds, totalSeconds);
        _smoke.Update(deltaSeconds, totalSeconds);
        _sparks.Update(deltaSeconds, totalSeconds);

        Matrix4x4 cameraWorld = Viewer.WorldMatrix;
        _flame.Upload(_flameGeometry, cameraWorld);
        _smoke.Upload(_smokeGeometry, cameraWorld);
        _sparks.Upload(_sparkGeometry, cameraWorld);

        if (B("light"))
        {
            // The fire light follows the flame: brighter with more particles,
            // with a flicker that never quite repeats.
            _flicker += deltaSeconds;
            float noise = (Noise.Value(_flicker * 7f, 0.5f) - 0.5f) + ((Noise.Value(_flicker * 19f, 3.5f) - 0.5f) * 0.5f);
            Light light = _fireLight.Light!.Value;
            light.Intensity = MathF.Max(0f, (6f + (_flame.Alive * 0.012f)) * (1f + (noise * 0.55f)));
            _fireLight.Light = light;
            _fireLight.Position = new Vector3(noise * 0.15f, 0.8f + (P("size") * 0.6f), noise * 0.12f);
        }
    }

    protected override void OnUnload()
    {
        _flame.Clear();
        _smoke.Clear();
        _sparks.Clear();
    }
}
