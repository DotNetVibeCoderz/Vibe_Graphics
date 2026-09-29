using System.Numerics;
using DemoGraphics.Framework;
using ThreeNet;
using ThreeNet.Avalonia;

namespace DemoGraphics.Scenes;

/// <summary>
/// Fire, smoke and sparks, built from dynamic geometry: three particle groups are
/// rewritten and re-uploaded every frame, blended additively and lit by a point
/// light that flickers with the flame. Bloom does the rest.
/// </summary>
public sealed class ParticleLabScene : DemoScene
{
    private readonly ParticleSystem _flame = new(3000, 11);
    private readonly ParticleSystem _smoke = new(2000, 23);
    private readonly ParticleSystem _sparks = new(1200, 37);

    private Geometry _flameGeometry = null!;
    private Geometry _smokeGeometry = null!;
    private Geometry _sparkGeometry = null!;
    private Material _flameMaterial = null!;
    private Material _smokeMaterial = null!;
    private Material _sparkMaterial = null!;
    private Node _fireLight = null!;
    private float _flicker;

    public ParticleLabScene()
    {
        Declare(
            DemoParameter.Choice("preset", "Effect", 0, ["Campfire", "Torch", "Smoke column", "Sparks", "Fireflies", "Snowfall"]),
            DemoParameter.Slider("rate", "Emission", 700f, 0f, 3000f, "/s", 10f),
            DemoParameter.Slider("life", "Lifetime", 1.8f, 0.2f, 8f, "s"),
            DemoParameter.Slider("size", "Particle size", 0.42f, 0.03f, 2f, "m"),
            DemoParameter.Slider("rise", "Rise", 2.6f, 0f, 12f, "m/s"),
            DemoParameter.Slider("gravity", "Gravity", -1.4f, -12f, 12f, "m/s2"),
            DemoParameter.Slider("wind", "Wind pickup", 0.6f, 0f, 2f),
            DemoParameter.Toggle("smoke", "Smoke", true),
            DemoParameter.Toggle("sparks", "Sparks", true),
            DemoParameter.Toggle("light", "Light from the fire", true));

        AddPreset("Campfire", ("preset", 0f), ("rate", 700f), ("life", 1.8f), ("size", 0.42f), ("rise", 2.6f), ("gravity", -1.4f), ("smoke", 1f), ("sparks", 1f));
        AddPreset("Torch", ("preset", 1f), ("rate", 420f), ("life", 0.9f), ("size", 0.22f), ("rise", 3.6f), ("gravity", -2.4f), ("sparks", 1f));
        AddPreset("Smoke column", ("preset", 2f), ("rate", 900f), ("life", 5.5f), ("size", 1.1f), ("rise", 1.6f), ("gravity", -0.4f), ("smoke", 1f), ("sparks", 0f));
        AddPreset("Sparks", ("preset", 3f), ("rate", 1500f), ("life", 1.1f), ("size", 0.07f), ("rise", 6f), ("gravity", 7f), ("smoke", 0f), ("sparks", 1f));
        AddPreset("Fireflies", ("preset", 4f), ("rate", 90f), ("life", 6f), ("size", 0.09f), ("rise", 0.3f), ("gravity", -0.05f), ("smoke", 0f), ("sparks", 0f));
        AddPreset("Snowfall", ("preset", 5f), ("rate", 1400f), ("life", 7f), ("size", 0.12f), ("rise", 0.1f), ("gravity", 0.6f), ("smoke", 0f), ("sparks", 0f), ("wind", 1.4f));
    }

    public override string Id => "VFX";

    public override string Title => "Particle laboratory";

    public override string Category => "Simulation";

    public override string Summary =>
        "Fire, smoke and sparks from geometry rewritten every frame, additively blended and lit.";

    public override IReadOnlyList<string> Features =>
        ["Dynamic geometry", "Additive blending", "Emissive materials", "Bloom", "Wind"];

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
        yield return ("Live particles", $"{_flame.Alive + _smoke.Alive + _sparks.Alive:N0}");
        yield return ("Quads rebuilt", $"{(_flame.Alive + _smoke.Alive + _sparks.Alive) * 2:N0} triangles/frame");
        yield return ("Groups", $"{1 + (B("smoke") ? 1 : 0) + (B("sparks") ? 1 : 0)} draw calls");
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
        Shader sprite = Scene.CreateShader(ShaderHooks.Particle, ShaderLanguage.Wgsl, "particle");

        _flameGeometry = _flame.CreateGeometry(Scene);
        _flameMaterial = SpriteMaterial(sprite, 0xFF7A1E, 9f, 1.6f);
        Node flame = Scene.AddMesh(_flameGeometry, _flameMaterial, name: "flame");
        flame.CastShadow = false;

        _smokeGeometry = _smoke.CreateGeometry(Scene);
        _smokeMaterial = SpriteMaterial(sprite, 0x1A1A1E, 0f, 2.4f, alpha: 0.30f);
        Node smoke = Scene.AddMesh(_smokeGeometry, _smokeMaterial, name: "smoke");
        smoke.CastShadow = false;

        _sparkGeometry = _sparks.CreateGeometry(Scene);
        _sparkMaterial = SpriteMaterial(sprite, 0xFFD27A, 22f, 3.5f);
        Node sparks = Scene.AddMesh(_sparkGeometry, _sparkMaterial, name: "sparks");
        sparks.CastShadow = false;

        _fireLight = Scene.AddLight(Light.Point(Palette.Rgb(0xFF8A2E), 26f, 14f), name: "fire light");
        _fireLight.Position = new Vector3(0f, 0.9f, 0f);

        Scene.AddLight(Light.Ambient(new Vector3(0.4f, 0.5f, 0.75f), 0.06f), name: "night sky");
        Node moon = Scene.AddLight(Light.Directional(Palette.Rgb(0x9FB6E8), 0.25f) with { CastShadow = true }, name: "moon");
        moon.Position = new Vector3(-8f, 12f, 6f);
        moon.LookAt(Vector3.Zero);
    }

    private Material SpriteMaterial(Shader shader, uint colour, float emissive, float hardness, float alpha = 1f)
    {
        Vector4 baseColor = Palette.Rgba(colour, alpha);
        return Scene.CreateMaterial(MaterialOptions.Pbr(baseColor, 0f, 0.6f) with
        {
            Shader = shader,
            Emissive = Palette.Rgb(colour),
            EmissiveIntensity = emissive,
            AlphaMode = AlphaMode.Blend,
            // Billboards must not write depth, or they cut each other out.
            DepthWrite = false,
            CullMode = CullMode.None,
            Custom0 = new Vector4(hardness, 0f, 0f, 0f),
            Shading = emissive > 0f ? ShadingModel.Basic : ShadingModel.Lambert,
        });
    }

    protected override void OnApplyParameters()
    {
        float rate = P("rate");
        float life = P("life");
        float size = P("size");
        float rise = P("rise");
        float gravity = P("gravity");
        float wind = P("wind");

        _flame.Rate = rate;
        _flame.Lifetime = life;
        _flame.Size = size;
        _flame.Rise = rise;
        _flame.Gravity = gravity;
        _flame.WindInfluence = wind;
        _flame.EmitterRadius = MathF.Max(0.08f, size * 0.7f);
        _flame.Growth = 1.8f;

        // Smoke is what the flame becomes: slower, bigger, longer lived.
        _smoke.Rate = B("smoke") ? rate * 0.35f : 0f;
        _smoke.Lifetime = life * 2.6f;
        _smoke.Size = size * 1.7f;
        _smoke.Rise = rise * 0.75f;
        _smoke.Gravity = gravity * 0.3f;
        _smoke.WindInfluence = wind * 1.6f;
        _smoke.EmitterRadius = MathF.Max(0.1f, size);
        _smoke.Growth = 3.4f;

        _sparks.Rate = B("sparks") ? rate * 0.12f : 0f;
        _sparks.Lifetime = life * 0.7f;
        _sparks.Size = MathF.Max(0.03f, size * 0.18f);
        _sparks.Rise = rise * 1.9f;
        _sparks.Gravity = gravity + 2.5f;
        _sparks.WindInfluence = wind * 0.8f;
        _sparks.EmitterRadius = 0.2f;
        _sparks.Growth = 0.4f;

        int preset = I("preset");
        bool cold = preset is 4 or 5;
        uint flameColour = preset switch
        {
            4 => 0xC8FF6Au,        // fireflies
            5 => 0xE8F2FFu,        // snow
            _ => 0xFF7A1Eu,
        };
        _flameMaterial.Update(options => options with
        {
            BaseColor = Palette.Rgba(flameColour, cold ? 0.8f : 1f),
            Emissive = Palette.Rgb(flameColour),
            EmissiveIntensity = cold ? (preset == 4 ? 6f : 1.4f) : 9f,
            Shading = ShadingModel.Basic,
        });

        Light light = _fireLight.Light!.Value;
        light.Enabled = B("light");
        light.Color = Palette.Rgb(flameColour);
        _fireLight.Light = light;
    }

    public override void Update(float deltaSeconds, double totalSeconds)
    {
        Vector2 wind = World.WindVector;
        _flame.Update(deltaSeconds, wind);
        _smoke.Update(deltaSeconds, wind);
        _sparks.Update(deltaSeconds, wind);

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
