using System.Numerics;
using DemoGraphics.Framework;
using ThreeNet;
using ThreeNet.Avalonia;

namespace DemoGraphics.Scenes;

/// <summary>
/// One studio, every light type the renderer has, each on its own switch with an
/// intensity and a colour temperature. The point light casts a cube shadow, the
/// spot a perspective one and the sun cascades, so the three shadow paths can be
/// compared in a single frame.
/// </summary>
public sealed class LightingLabScene : DemoScene
{
    private Node _sun = null!;
    private Node _point = null!;
    private Node _spot = null!;
    private Node _area = null!;
    private Node _ambient = null!;
    private Node _pointMarker = null!;
    private Node _spotMarker = null!;
    private Node _areaMarker = null!;
    private Material _pointGlass = null!;
    private Material _areaGlass = null!;
    private Node _rig = null!;

    public LightingLabScene()
    {
        Declare(
            DemoParameter.Toggle("sun", "Directional", true),
            DemoParameter.Slider("sunIntensity", "  intensity", 1.6f, 0f, 8f),
            DemoParameter.Slider("sunKelvin", "  temperature", 5600f, 1600f, 12000f, "K", 100f),
            DemoParameter.Toggle("point", "Point", true),
            DemoParameter.Slider("pointIntensity", "  intensity", 90f, 0f, 400f),
            DemoParameter.Slider("pointKelvin", "  temperature", 2700f, 1600f, 12000f, "K", 100f),
            DemoParameter.Slider("pointRange", "  range", 16f, 2f, 60f, "m"),
            DemoParameter.Toggle("spot", "Spot", true),
            DemoParameter.Slider("spotIntensity", "  intensity", 240f, 0f, 900f),
            DemoParameter.Slider("spotKelvin", "  temperature", 6500f, 1600f, 12000f, "K", 100f),
            DemoParameter.Slider("spotCone", "  cone", 26f, 4f, 70f, "deg"),
            DemoParameter.Slider("spotSoft", "  penumbra", 0.45f, 0f, 1f),
            DemoParameter.Toggle("area", "Area", true),
            DemoParameter.Slider("areaIntensity", "  intensity", 70f, 0f, 400f),
            DemoParameter.Slider("areaSize", "  size", 2.4f, 0.2f, 8f, "m"),
            DemoParameter.Slider("ambient", "Ambient", 0.08f, 0f, 0.6f),
            DemoParameter.Slider("shadowStrength", "Shadow strength", 1f, 0f, 1f),
            DemoParameter.Toggle("orbit", "Orbit the rig", true));

        AddPreset("Three point", ("sun", 0f), ("point", 1f), ("spot", 1f), ("area", 1f), ("ambient", 0.05f),
            ("pointIntensity", 70f), ("spotIntensity", 260f), ("areaIntensity", 90f));
        AddPreset("Sunlight only", ("sun", 1f), ("point", 0f), ("spot", 0f), ("area", 0f), ("ambient", 0.12f),
            ("sunIntensity", 3.2f), ("sunKelvin", 5200f));
        AddPreset("Candle", ("sun", 0f), ("point", 1f), ("spot", 0f), ("area", 0f), ("ambient", 0.01f),
            ("pointIntensity", 26f), ("pointKelvin", 1900f), ("pointRange", 9f));
        AddPreset("Stage spot", ("sun", 0f), ("point", 0f), ("spot", 1f), ("area", 0f), ("ambient", 0.02f),
            ("spotIntensity", 700f), ("spotCone", 14f), ("spotSoft", 0.15f));
        AddPreset("Softbox", ("sun", 0f), ("point", 0f), ("spot", 0f), ("area", 1f), ("ambient", 0.06f),
            ("areaIntensity", 220f), ("areaSize", 6f));
        AddPreset("No shadows", ("shadowStrength", 0f));
    }

    public override string Id => "LGT";

    public override string Title => "Lighting laboratory";

    public override string Category => "Lighting";

    public override string Summary =>
        "Directional, point, spot, area and ambient light on one set, with colour temperature in kelvin.";

    public override IReadOnlyList<string> Features =>
        ["All light types", "Cascaded shadows", "Cube shadows", "Spot shadows", "Colour temperature"];

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        Shadows = true,
        ShadowMapSize = 2048,
        ShadowCascades = 2,
        ShadowDistance = 40f,
        ShadowSoftness = 2,
        Ssao = true,
        SsaoRadius = 0.4f,
        Bloom = true,
        BloomIntensity = 0.35f,
    };

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 1.4f, 0f);
        orbit.Distance = 13f;
        orbit.Yaw = 0.6f;
        orbit.Pitch = 0.22f;
    }

    public override IEnumerable<(string Label, string Value)> Metrics()
    {
        int lights = (B("sun") ? 1 : 0) + (B("point") ? 1 : 0) + (B("spot") ? 1 : 0) + (B("area") ? 1 : 0) + 1;
        yield return ("Lights on", lights.ToString());
        yield return ("Shadow casters", $"{(B("sun") ? 1 : 0) + (B("point") ? 1 : 0) + (B("spot") ? 1 : 0)} lights");
        yield return ("Cube faces", B("point") ? "6" : "0");
    }

    protected override void OnBuild()
    {
        Scene.Environment = SceneEnvironment.Default with
        {
            Background = Palette.Rgba(0x070A0E),
            AmbientColor = new Vector3(0.55f, 0.62f, 0.78f),
            AmbientIntensity = 0.08f,
        };

        // ---------------------------------------------------------------- set
        Material floor = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x6E6B65), 0f, 0.62f));
        Scene.AddMesh(Procedural.Ground(Scene, 48f, 1), floor, name: "floor");

        Material wall = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x7A766E), 0f, 0.8f));
        Node back = Scene.AddMesh(Scene.CreateBoxGeometry(18f, 8f, 0.3f), wall, name: "back wall");
        back.Position = new Vector3(0f, 4f, -5.5f);
        Node side = Scene.AddMesh(Scene.CreateBoxGeometry(0.3f, 8f, 11f), wall, name: "side wall");
        side.Position = new Vector3(-8.8f, 4f, 0f);

        // Subjects: a sphere for highlights, a column for shadow shape, a
        // helmet-ish cluster for creases, and a thin fin for contact shadows.
        Material plaster = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0xD9D4C8), 0f, 0.45f));
        Material metal = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0xB8BCC4), 1f, 0.24f));
        Material rubber = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x1C1F24), 0f, 0.95f));

        Node sphere = Scene.AddMesh(Scene.CreateSphereGeometry(1.1f, 48, 26), plaster, name: "sphere");
        sphere.Position = new Vector3(-2.4f, 1.1f, 0.4f);

        Node column = Scene.AddMesh(Scene.CreateCylinderGeometry(0.42f, 0.55f, 3.4f, 32), plaster, name: "column");
        column.Position = new Vector3(1.6f, 1.7f, -1.6f);

        Node knot = Scene.AddMesh(Scene.CreateTorusGeometry(0.8f, 0.3f, 24, 72), metal, name: "ring");
        knot.Position = new Vector3(2.6f, 1.2f, 1.6f);
        knot.EulerAngles = new Vector3(1.1f, 0.4f, 0f);

        Node fin = Scene.AddMesh(Scene.CreateBoxGeometry(2.6f, 1.4f, 0.06f), rubber, name: "fin");
        fin.Position = new Vector3(-0.4f, 0.7f, 2.4f);

        Node steps = Scene.CreateNode(name: "steps");
        for (int i = 0; i < 4; i++)
        {
            Node step = Scene.AddMesh(Scene.CreateBoxGeometry(2.2f - (i * 0.4f), 0.32f, 1.4f - (i * 0.2f)), plaster, parent: steps, name: $"step-{i}");
            step.Position = new Vector3(0f, 0.16f + (i * 0.32f), 0f);
        }

        steps.Position = new Vector3(-5.4f, 0f, -2.2f);

        // ------------------------------------------------------------ the rig
        _rig = Scene.CreateNode(name: "rig");

        _sun = Scene.AddLight(Light.Directional(Vector3.One, 1.6f) with { CastShadow = true, ShadowNormalBias = 1.8f }, name: "directional");
        _sun.Position = new Vector3(7f, 10f, 6f);
        _sun.LookAt(Vector3.Zero);

        _pointGlass = Scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.2f) with
        {
            Emissive = Vector3.One,
            EmissiveIntensity = 8f,
        });
        _pointMarker = Scene.AddMesh(Scene.CreateSphereGeometry(0.18f, 18, 12), _pointGlass, parent: _rig, name: "point marker");
        _pointMarker.Position = new Vector3(3.2f, 2.6f, 2.6f);
        _pointMarker.CastShadow = false;
        _point = Scene.AddLight(
            Light.Point(Palette.Kelvin(2700f), 90f, 16f) with { CastShadow = true, ShadowBias = 0.0018f },
            parent: _rig,
            name: "point");
        _point.Position = _pointMarker.Position;

        _spotMarker = Scene.AddMesh(
            Scene.CreateConeGeometry(0.34f, 0.7f, 20),
            Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x2B2F36), 0.7f, 0.4f)),
            parent: _rig,
            name: "spot marker");
        _spotMarker.Position = new Vector3(-3.4f, 5.4f, 3f);
        _spotMarker.EulerAngles = new Vector3(MathF.PI, 0f, 0f);
        _spot = Scene.AddLight(
            Light.Spot(Palette.Kelvin(6500f), 240f, 30f, 14f.ToRadians(), 26f.ToRadians()) with { CastShadow = true },
            parent: _rig,
            name: "spot");
        _spot.Position = _spotMarker.Position;
        _spot.LookAt(new Vector3(0f, 0.8f, 0f));

        _areaGlass = Scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.3f) with
        {
            Emissive = Palette.Rgb(0xFFF3DC),
            EmissiveIntensity = 4f,
        });
        _areaMarker = Scene.AddMesh(Scene.CreateBoxGeometry(2.4f, 2.4f, 0.06f), _areaGlass, parent: _rig, name: "area marker");
        _areaMarker.Position = new Vector3(4.6f, 3.2f, -3.2f);
        _areaMarker.CastShadow = false;
        _area = Scene.AddLight(Light.Default with
        {
            Type = LightType.Area,
            Color = Palette.Kelvin(5200f),
            Intensity = 70f,
            Range = 26f,
            Size = new Vector2(2.4f),
        }, parent: _rig, name: "area");
        _area.Position = _areaMarker.Position;
        _area.LookAt(new Vector3(0f, 1.2f, 0f));

        _ambient = Scene.AddLight(Light.Ambient(new Vector3(0.5f, 0.6f, 0.8f), 0.08f), name: "ambient");
    }

    protected override void OnApplyParameters()
    {
        float strength = P("shadowStrength");

        Light sun = _sun.Light!.Value;
        sun.Enabled = B("sun");
        sun.Intensity = P("sunIntensity");
        sun.Color = Palette.Kelvin(P("sunKelvin"));
        sun.ShadowStrength = strength;
        _sun.Light = sun;

        Light point = _point.Light!.Value;
        point.Enabled = B("point");
        point.Intensity = P("pointIntensity");
        point.Color = Palette.Kelvin(P("pointKelvin"));
        point.Range = P("pointRange");
        point.ShadowStrength = strength;
        _point.Light = point;
        _pointMarker.Visible = B("point");
        _pointGlass.Update(options => options with
        {
            Emissive = Palette.Kelvin(P("pointKelvin")),
            EmissiveIntensity = 3f + (P("pointIntensity") * 0.06f),
        });

        Light spot = _spot.Light!.Value;
        spot.Enabled = B("spot");
        spot.Intensity = P("spotIntensity");
        spot.Color = Palette.Kelvin(P("spotKelvin"));
        float outer = P("spotCone").ToRadians();
        spot.OuterConeAngle = outer;
        // The penumbra is the gap between the two cone angles.
        spot.InnerConeAngle = outer * (1f - (P("spotSoft") * 0.95f));
        spot.ShadowStrength = strength;
        _spot.Light = spot;
        _spotMarker.Visible = B("spot");

        Light area = _area.Light!.Value;
        area.Enabled = B("area");
        area.Intensity = P("areaIntensity");
        area.Size = new Vector2(P("areaSize"));
        _area.Light = area;
        _areaMarker.Visible = B("area");
        _areaMarker.Scale = new Vector3(P("areaSize") / 2.4f, P("areaSize") / 2.4f, 1f);
        _areaGlass.Update(options => options with { EmissiveIntensity = 1f + (P("areaIntensity") * 0.04f) });

        Light ambient = _ambient.Light!.Value;
        ambient.Intensity = P("ambient");
        _ambient.Light = ambient;
    }

    public override void Update(float deltaSeconds, double totalSeconds)
    {
        if (B("orbit"))
        {
            _rig.EulerAngles = new Vector3(0f, (float)totalSeconds * 0.22f, 0f);
        }
    }
}
