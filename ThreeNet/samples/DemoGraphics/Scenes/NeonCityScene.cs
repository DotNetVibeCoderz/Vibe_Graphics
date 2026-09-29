using System.Numerics;
using DemoGraphics.Framework;
using ThreeNet;
using ThreeNet.Avalonia;

namespace DemoGraphics.Scenes;

/// <summary>
/// A wet street at night, which is the honest stress test for a renderer: a
/// hundred small lights, emissive signage, low roughness everywhere and fog to
/// catch it all. It defaults to the deferred path, and the compare split is the
/// fastest way to see why - forward pays for every light on every pixel.
/// </summary>
public sealed class NeonCityScene : DemoScene
{
    private readonly List<Node> _lights = [];
    private readonly List<Node> _traffic = [];
    private Material _road = null!;
    private Material _neon = null!;
    private Node _moon = null!;

    public NeonCityScene()
    {
        Declare(
            DemoParameter.Slider("lights", "Street lights", 64f, 2f, 128f, "", 2f,
                "The renderer takes up to 128 at once.", requiresRebuild: true),
            DemoParameter.Slider("blocks", "City blocks", 6f, 2f, 10f, "", 1f, requiresRebuild: true),
            DemoParameter.Slider("flicker", "Sign flicker", 2.4f, 0f, 12f, "Hz"),
            DemoParameter.Slider("glow", "Sign brightness", 6f, 0.5f, 24f),
            DemoParameter.Slider("puddles", "Puddle scale", 0.12f, 0.02f, 0.6f),
            DemoParameter.Slider("traffic", "Traffic", 18f, 0f, 60f, "", 1f, requiresRebuild: true),
            DemoParameter.Toggle("moon", "Moonlight", true));

        AddPreset("Deferred showcase", "Rain", 22.5f, ("lights", 96f), ("glow", 8f), ("flicker", 2.4f));
        AddPreset("Light storm", "Storm", 23.5f, ("lights", 128f), ("glow", 14f), ("flicker", 6f));
        AddPreset("Quiet street", "Clear", 21f, ("lights", 16f), ("glow", 4f), ("traffic", 4f));
        AddPreset("Dry night", "Clear", 23f, ("lights", 64f), ("puddles", 0.02f));
        AddPreset("Fog", "Fog", 2f, ("lights", 64f), ("glow", 10f));
    }

    public override string Id => "NEO";

    public override string Title => "Neon night city";

    public override string Category => "Environment";

    public override string Summary =>
        "Up to 128 small lights on wet asphalt: the scene the deferred path was built for.";

    public override IReadOnlyList<string> Features =>
        ["Deferred shading", "Many dynamic lights", "Emissive materials", "Bloom", "Fog", "Wet surfaces"];

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        RenderPath = RenderPath.Deferred,
        Shadows = false,
        Ssao = true,
        SsaoRadius = 0.5f,
        SsaoIntensity = 1.6f,
        Bloom = true,
        BloomIntensity = 0.85f,
        BloomThreshold = 0.9f,
        Exposure = 1.1f,
        MsaaSamples = 1,
    };

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 3f, 0f);
        orbit.Distance = 34f;
        orbit.Yaw = 0.78f;
        orbit.Pitch = 0.18f;
        orbit.MaxDistance = 180f;
    }

    public override IReadOnlyList<CameraKey> CameraPath =>
    [
        new(new Vector3(0f, 3f, 0f), 34f, 0.8f, 0.16f, 0f),
        new(new Vector3(0f, 1.6f, 10f), 12f, 1.9f, 0.05f, 4f),
        new(new Vector3(0f, 8f, -8f), 52f, 3.3f, 0.36f, 8f),
        new(new Vector3(0f, 3f, 0f), 34f, 4.9f, 0.16f, 12f),
    ];

    public override IEnumerable<(string Label, string Value)> Metrics()
    {
        yield return ("Lights", _lights.Count.ToString());
        yield return ("Traffic", _traffic.Count.ToString());
        yield return ("Wetness", $"{MathF.Max(World.Wetness, 0.3f):P0}");
    }

    protected override void OnBuild()
    {
        Scene.Environment = SceneEnvironment.Default with
        {
            Background = Palette.Rgba(0x05070C),
            AmbientColor = new Vector3(0.28f, 0.34f, 0.55f),
            AmbientIntensity = 0.06f,
            FogColor = new Vector3(0.035f, 0.045f, 0.075f),
            FogDensity = 0.02f,
            FogStart = 8f,
        };

        // ---------------------------------------------------------- the street
        Shader wet = Scene.CreateShader(ShaderHooks.WetGround, ShaderLanguage.Wgsl, "wet ground");
        _road = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x15171B), 0.05f, 0.65f) with
        {
            Shader = wet,
            Reflectance = 0.6f,
        });
        Scene.AddMesh(Procedural.Ground(Scene, 220f, 1), _road, name: "street");

        // --------------------------------------------------------- the blocks
        int blocks = (int)P("blocks");
        Material concrete = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x23262C), 0f, 0.8f));
        Material glass = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x0E1720), 0.35f, 0.12f) with
        {
            Emissive = Palette.Rgb(0x2A4C66),
            EmissiveIntensity = 0.7f,
        });

        MeshBuilder walls = new();
        MeshBuilder windows = new();
        for (int bx = -blocks; bx <= blocks; bx++)
        {
            for (int bz = -blocks; bz <= blocks; bz++)
            {
                // Leave the middle two lanes clear: that is the street.
                if (Math.Abs(bx) < 1 || Math.Abs(bz) < 1)
                {
                    continue;
                }

                float x = bx * 15f;
                float z = bz * 15f;
                float height = 6f + (Noise.Hash(bx, bz) * 26f);
                float width = 8f + (Noise.Hash(bx + 40, bz) * 3f);
                walls.AddBox(new Vector3(x, height * 0.5f, z), new Vector3(width, height, width), Vector2.Zero);

                // Lit window bands, cheap and enough to read as a tower.
                int bands = Math.Max(1, (int)(height / 3.2f));
                for (int band = 1; band < bands; band++)
                {
                    float y = band * 3.2f;
                    if (Noise.Hash(bx * 31 + band, bz * 17) < 0.35f)
                    {
                        continue;
                    }

                    windows.AddBox(new Vector3(x, y, z), new Vector3(width + 0.12f, 1.1f, width + 0.12f), Vector2.Zero);
                }
            }
        }

        Scene.AddMesh(walls.Build(Scene), concrete, name: "blocks");
        Node windowNode = Scene.AddMesh(windows.Build(Scene), glass, name: "windows");
        windowNode.CastShadow = false;

        // ----------------------------------------------------------- signage
        Shader neonShader = Scene.CreateShader(ShaderHooks.Neon, ShaderLanguage.Wgsl, "neon");
        _neon = Scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.3f) with { Shader = neonShader });

        int count = (int)P("lights");
        uint[] hues = [0xFF3B6E, 0x2FE0C8, 0xFFB03A, 0x7A5CFF, 0x36C6FF, 0xFF6A2F];
        for (int i = 0; i < count; i++)
        {
            float angle = Noise.Hash(i, 211) * MathF.Tau;
            float radius = 10f + (Noise.Hash(i, 223) * blocks * 13f);
            Vector3 at = new(MathF.Sin(angle) * radius, 2.2f + (Noise.Hash(i, 227) * 12f), MathF.Cos(angle) * radius);
            Vector3 colour = Palette.Rgb(hues[i % hues.Length]);

            // The tube is emissive; the light beside it does the lighting.
            Material tube = Scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.3f) with
            {
                Shader = neonShader,
                Custom0 = new Vector4(colour, P("flicker")),
            });
            Node sign = Scene.AddMesh(
                Scene.CreateBoxGeometry(Noise.Hash(i, 229) > 0.5f ? 2.6f : 0.25f, Noise.Hash(i, 229) > 0.5f ? 0.25f : 2.6f, 0.12f),
                tube,
                name: $"sign-{i}");
            sign.Position = at;
            sign.CastShadow = false;

            Node light = Scene.AddLight(Light.Point(colour, 26f, 13f), name: $"neon-{i}");
            light.Position = at + new Vector3(0f, -0.4f, 0.6f);
            _lights.Add(light);
        }

        // ----------------------------------------------------------- traffic
        Material tail = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x35090C), 0f, 0.4f) with
        {
            Emissive = Palette.Rgb(0xFF2A1E),
            EmissiveIntensity = 8f,
        });
        int cars = (int)P("traffic");
        for (int i = 0; i < cars; i++)
        {
            Node car = Scene.AddMesh(Scene.CreateBoxGeometry(1.8f, 0.7f, 4.2f), tail, name: $"car-{i}");
            bool alongX = i % 2 == 0;
            float lane = ((i % 4) - 1.5f) * 3.4f;
            car.Position = alongX
                ? new Vector3((Noise.Hash(i, 301) - 0.5f) * 160f, 0.45f, lane)
                : new Vector3(lane, 0.45f, (Noise.Hash(i, 307) - 0.5f) * 160f);
            car.EulerAngles = new Vector3(0f, alongX ? MathF.PI / 2f : 0f, 0f);
            car.CastShadow = false;
            _traffic.Add(car);
        }

        _moon = Scene.AddLight(Light.Directional(Palette.Rgb(0x9FB6E8), 0.16f), name: "moon");
        _moon.Position = new Vector3(-40f, 60f, 30f);
        _moon.LookAt(Vector3.Zero);
    }

    protected override void OnApplyEnvironment()
    {
        // The street is always at least damp: this scene is about reflections.
        float wetness = MathF.Max(World.Wetness, 0.3f);
        _road.Update(options => options with { Custom0 = new Vector4(wetness, P("puddles"), 0f, 0f) });
        Scene.Environment = Scene.Environment with
        {
            FogDensity = MathF.Max(World.FogDensity, 0.012f),
            FogStart = 8f,
        };

        Light moon = _moon.Light!.Value;
        moon.Enabled = B("moon");
        moon.Intensity = 0.16f * MathF.Max(World.NightFactor, 0.35f);
        _moon.Light = moon;
    }

    protected override void OnApplyParameters()
    {
        OnApplyEnvironment();
        _neon.Update(options => options with { Custom0 = new Vector4(Palette.Rgb(0x2FE0C8), P("flicker")) });

        float glow = P("glow");
        foreach (Node light in _lights)
        {
            Light value = light.Light!.Value;
            value.Intensity = glow * 4.4f;
            light.Light = value;
        }
    }

    public override void Update(float deltaSeconds, double totalSeconds)
    {
        // Traffic drives down the lanes and wraps around.
        for (int i = 0; i < _traffic.Count; i++)
        {
            Node car = _traffic[i];
            bool alongX = i % 2 == 0;
            float speed = 9f + (Noise.Hash(i, 401) * 14f);
            Vector3 position = car.Position;
            if (alongX)
            {
                position.X += speed * deltaSeconds * (position.Z > 0f ? 1f : -1f);
                if (MathF.Abs(position.X) > 90f)
                {
                    position.X = -MathF.Sign(position.X) * 90f;
                }
            }
            else
            {
                position.Z += speed * deltaSeconds * (position.X > 0f ? -1f : 1f);
                if (MathF.Abs(position.Z) > 90f)
                {
                    position.Z = -MathF.Sign(position.Z) * 90f;
                }
            }

            car.Position = position;
        }
    }

    protected override void OnUnload()
    {
        _lights.Clear();
        _traffic.Clear();
    }
}
