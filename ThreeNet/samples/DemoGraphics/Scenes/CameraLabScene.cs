using System.Numerics;
using DemoGraphics.Framework;
using ThreeNet;
using ThreeNet.Avalonia;

namespace DemoGraphics.Scenes;

/// <summary>
/// A physical camera. The controls are the ones on a real body - focal length,
/// aperture, ISO, shutter - and each one is converted to what the renderer
/// actually takes: field of view, depth of field, exposure. Posts stand at
/// measured distances so the focus plane can be read off the floor.
/// </summary>
public sealed class CameraLabScene : DemoScene
{
    private static readonly float[] PostDistances = [1f, 2f, 3f, 5f, 8f, 13f, 21f, 34f];

    private Node _subject = null!;
    private Material _chart = null!;

    public CameraLabScene()
    {
        Declare(
            DemoParameter.Slider("focal", "Focal length", 50f, 14f, 200f, "mm", 1f),
            DemoParameter.Slider("aperture", "Aperture", 2.8f, 1.2f, 22f, "f/", 0f,
                "Wide open blurs the background; stopped down brings it back."),
            DemoParameter.Slider("focus", "Focus distance", 5f, 0.5f, 40f, "m"),
            DemoParameter.Slider("iso", "ISO", 200f, 50f, 6400f, "", 50f),
            DemoParameter.Slider("shutter", "Shutter", 125f, 8f, 1000f, "1/s", 1f),
            DemoParameter.Slider("blur", "Max blur", 16f, 2f, 40f, "px"),
            DemoParameter.Toggle("dof", "Depth of field", true),
            DemoParameter.Toggle("motion", "Motion blur", true),
            DemoParameter.Toggle("orbitSubject", "Turn the subject", true),
            DemoParameter.Choice("curve", "Tone curve", 2, ["None", "Reinhard", "ACES", "Filmic"]));

        AddPreset("Portrait 85mm f/1.8", ("focal", 85f), ("aperture", 1.8f), ("focus", 3f), ("dof", 1f), ("blur", 22f));
        AddPreset("Landscape 24mm f/11", ("focal", 24f), ("aperture", 11f), ("focus", 20f), ("blur", 8f));
        AddPreset("Macro 100mm f/2.8", ("focal", 100f), ("aperture", 2.8f), ("focus", 1.2f), ("blur", 30f));
        AddPreset("Night 35mm f/1.4", ("focal", 35f), ("aperture", 1.4f), ("focus", 5f), ("iso", 3200f), ("shutter", 30f));
        AddPreset("Bright day f/16", ("aperture", 16f), ("iso", 100f), ("shutter", 500f), ("blur", 6f));
        AddPreset("Cinematic pan", ("focal", 40f), ("aperture", 2f), ("motion", 1f), ("shutter", 24f), ("curve", 3f));
    }

    public override string Id => "CAM";

    public override string Title => "Physical camera";

    public override string Category => "Camera and post";

    public override string Summary =>
        "Focal length, aperture, ISO and shutter, converted to field of view, depth of field and exposure.";

    public override IReadOnlyList<string> Features =>
        ["Depth of field", "Motion blur", "Exposure", "Tone mapping", "Bloom"];

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        Shadows = true,
        ShadowMapSize = 2048,
        ShadowCascades = 2,
        ShadowDistance = 60f,
        Ssao = true,
        Bloom = true,
        BloomIntensity = 0.4f,
        DepthOfField = true,
        DofFocusDistance = 5f,
        DofFocusRange = 1.2f,
        DofMaxBlur = 16f,
        MotionBlur = true,
        MotionBlurStrength = 0.6f,
    };

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 1.1f, 0f);
        orbit.Distance = 5f;
        orbit.Yaw = 0.05f;
        orbit.Pitch = 0.08f;
        orbit.MinDistance = 0.6f;
    }

    public override IReadOnlyList<CameraKey> CameraPath =>
    [
        new(new Vector3(0f, 1.1f, 0f), 5f, 0.05f, 0.08f, 0f),
        new(new Vector3(0f, 1.1f, 0f), 3f, 0.9f, 0.02f, 4f),
        new(new Vector3(0f, 1.4f, -6f), 9f, -0.6f, 0.2f, 8f),
        new(new Vector3(0f, 1.1f, 0f), 5f, 0.05f, 0.08f, 12f),
    ];

    public override IEnumerable<(string Label, string Value)> Metrics()
    {
        yield return ("Field of view", $"{FieldOfViewDegrees():F1} deg");
        yield return ("Exposure", $"{Exposure():F2}");
        yield return ("Focus", $"{P("focus"):F2} m, depth {FocusRange():F2} m");
    }

    protected override void OnBuild()
    {
        Scene.Environment = SceneEnvironment.Default with
        {
            Background = Palette.Rgba(0x0A0C11),
            AmbientColor = new Vector3(0.55f, 0.62f, 0.78f),
            AmbientIntensity = 0.1f,
            FogColor = new Vector3(0.06f, 0.07f, 0.1f),
            FogDensity = 0.006f,
            FogStart = 12f,
        };

        Material floor = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x1A1D23), 0f, 0.5f));
        Scene.AddMesh(Procedural.Ground(Scene, 120f, 1), floor, name: "floor");

        // A ruler on the floor: one marker per metre for the first ten metres.
        Material markPaint = Scene.CreateMaterial(MaterialOptions.Basic(Palette.Rgba(0x39414D)));
        MeshBuilder marks = new();
        for (int i = 1; i <= 40; i++)
        {
            float width = i % 5 == 0 ? 0.5f : 0.22f;
            marks.AddBox(new Vector3(0f, 0.005f, -i), new Vector3(width, 0.01f, 0.05f), Vector2.Zero);
        }

        Node ruler = Scene.AddMesh(marks.Build(Scene), markPaint, name: "ruler");
        ruler.CastShadow = false;

        // Posts at Fibonacci distances, each a different colour, so the focus
        // plane can be named rather than guessed.
        uint[] colours = [0xE0564E, 0xE8A93C, 0x8FCB6B, 0x45C4CF, 0x5B93D6, 0x9A86F0, 0xD96CB0, 0xD9D4C8];
        for (int i = 0; i < PostDistances.Length; i++)
        {
            Material paint = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(colours[i]), 0.1f, 0.35f));
            Node post = Scene.AddMesh(Scene.CreateCylinderGeometry(0.06f, 0.06f, 1.4f, 16), paint, name: $"post-{PostDistances[i]}m");
            post.Position = new Vector3(i % 2 == 0 ? -0.75f : 0.75f, 0.7f, -PostDistances[i]);

            Node cap = Scene.AddMesh(Scene.CreateSphereGeometry(0.12f, 20, 12), paint, parent: post, name: "cap");
            cap.Position = new Vector3(0f, 0.78f, 0f);
        }

        // The subject: a turning group with fine detail to judge sharpness.
        _subject = Scene.CreateNode(name: "subject");
        _subject.Position = new Vector3(0f, 0f, -5f);
        _chart = Scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.45f) with
        {
            BaseColorMap = Procedural.Checker(Scene, 512, 32, 0xF2EFE6, 0x1B1E24),
            UvScale = new Vector2(3f),
        });
        Node board = Scene.AddMesh(Scene.CreateBoxGeometry(1.1f, 1.1f, 0.05f), _chart, parent: _subject, name: "chart");
        board.Position = new Vector3(0f, 1.3f, 0f);

        Material chrome = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0xC6CAD2), 1f, 0.12f));
        Node ball = Scene.AddMesh(Scene.CreateSphereGeometry(0.42f, 48, 26), chrome, parent: _subject, name: "ball");
        ball.Position = new Vector3(0.62f, 0.42f, 0.4f);

        Material bulb = Scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.2f) with
        {
            Emissive = Palette.Rgb(0xFFD9A0),
            EmissiveIntensity = 22f,
        });
        Node lamp = Scene.AddMesh(Scene.CreateSphereGeometry(0.1f, 18, 12), bulb, parent: _subject, name: "practical light");
        lamp.Position = new Vector3(-0.7f, 0.9f, 0.5f);
        lamp.CastShadow = false;
        Node practical = Scene.AddLight(Light.Point(Palette.Kelvin(2600f), 12f, 6f), parent: _subject, name: "practical");
        practical.Position = lamp.Position;

        Node key = Scene.AddLight(
            Light.Spot(Palette.Kelvin(5600f), 300f, 30f, 20f.ToRadians(), 38f.ToRadians()) with { CastShadow = true },
            name: "key");
        key.Position = new Vector3(3.4f, 4.6f, -1.6f);
        key.LookAt(new Vector3(0f, 1f, -5f));

        Scene.AddLight(Light.Ambient(new Vector3(0.5f, 0.58f, 0.75f), 0.07f), name: "fill");
    }

    protected override void OnApplyParameters()
    {
        // Focal length to vertical field of view on a 36 x 24 mm frame.
        Camera camera = Viewer.Camera ?? Camera.Perspective();
        camera.FieldOfView = FieldOfViewDegrees().ToRadians();
        Viewer.Camera = camera;

        RequestRender(controls =>
        {
            controls.DepthOfField = B("dof");
            controls.DofFocusDistance = P("focus");
            controls.DofFocusRange = FocusRange();
            // A wide aperture is a shallow depth of field and a big circle of
            // confusion, so both follow the f-number.
            controls.DofMaxBlur = P("blur") * (8f / MathF.Max(P("aperture"), 1.2f));
            controls.MotionBlur = B("motion");
            controls.MotionBlurStrength = Math.Clamp(60f / MathF.Max(P("shutter"), 8f), 0.05f, 1f);
            controls.Exposure = Exposure();
            controls.ToneMapping = (ToneMapping)Math.Clamp(I("curve"), 0, 3);
        });
    }

    public override void Update(float deltaSeconds, double totalSeconds)
    {
        if (B("orbitSubject"))
        {
            _subject.EulerAngles = new Vector3(0f, (float)totalSeconds * 0.5f, 0f);
        }
    }

    private float FieldOfViewDegrees() =>
        2f * MathF.Atan(12f / MathF.Max(P("focal"), 8f)).ToDegrees();

    /// <summary>Sharp band around the focus plane, from the f-number and distance.</summary>
    private float FocusRange()
    {
        float aperture = MathF.Max(P("aperture"), 1.2f);
        float distance = MathF.Max(P("focus"), 0.4f);
        return Math.Clamp(distance * distance * aperture / 900f, 0.05f, 30f);
    }

    /// <summary>
    /// A stand-in for an exposure triangle: ISO and shutter open the image up,
    /// the f-number closes it down, normalised so ISO 200, 1/125, f/2.8 is 1.
    /// </summary>
    private float Exposure()
    {
        float iso = MathF.Max(P("iso"), 25f);
        float shutter = MathF.Max(P("shutter"), 4f);
        float aperture = MathF.Max(P("aperture"), 1.2f);
        float value = iso / 200f * (125f / shutter) * (2.8f * 2.8f / (aperture * aperture));
        return Math.Clamp(value, 0.05f, 8f);
    }
}
