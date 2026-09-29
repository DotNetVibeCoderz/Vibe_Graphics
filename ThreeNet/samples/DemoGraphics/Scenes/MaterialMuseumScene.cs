using System.Numerics;
using DemoGraphics.Framework;
using ThreeNet;
using ThreeNet.Avalonia;

namespace DemoGraphics.Scenes;

/// <summary>
/// A studio wall of spheres: metallic across, roughness down, so the whole
/// parameter space is visible at once. The front row holds named materials, and
/// the sliders drive the sample on the plinth so a channel can be watched on its
/// own.
/// </summary>
public sealed class MaterialMuseumScene : DemoScene
{
    private static readonly (string Name, uint Color, float Metallic, float Roughness)[] Samples =
    [
        ("Gold", 0xFFD277, 1f, 0.16f),
        ("Copper", 0xD98B63, 1f, 0.28f),
        ("Aluminium", 0xD6DAE0, 1f, 0.34f),
        ("Rusted iron", 0x7A4A32, 0.6f, 0.82f),
        ("Car paint", 0x1F4FA8, 0.25f, 0.18f),
        ("Ceramic", 0xF2EFE6, 0f, 0.22f),
        ("Rubber", 0x22252A, 0f, 0.92f),
        ("Concrete", 0x9B9890, 0f, 0.86f),
    ];

    private readonly List<Material> _grid = [];
    private Material _feature = null!;
    private Material _floor = null!;
    private Texture _checker = null!;
    private Texture _grain = null!;
    private Texture _normals = null!;
    private Node _turntable = null!;
    private Node _keyLight = null!;

    public MaterialMuseumScene()
    {
        Declare(
            DemoParameter.Slider("metallic", "Metallic", 1f, 0f, 1f),
            DemoParameter.Slider("roughness", "Roughness", 0.25f, 0.02f, 1f),
            DemoParameter.Slider("reflectance", "Reflectance", 0.5f, 0f, 1f, "", 0f, "Dielectric F0; metals ignore it."),
            DemoParameter.Slider("emissive", "Emissive", 0f, 0f, 8f),
            DemoParameter.Slider("normalStrength", "Normal strength", 1f, 0f, 3f),
            DemoParameter.Slider("uvScale", "Texture scale", 2f, 0.25f, 12f),
            DemoParameter.Choice("model", "Shading model", 3, ["Basic", "Lambert", "Phong", "PBR"]),
            DemoParameter.Toggle("textures", "Textures on the sample", true),
            DemoParameter.Toggle("turntable", "Turntable", true),
            DemoParameter.Toggle("wire", "Wireframe sample", false));

        AddPreset("Polished metal", ("metallic", 1f), ("roughness", 0.08f), ("emissive", 0f), ("textures", 0f));
        AddPreset("Brushed metal", ("metallic", 1f), ("roughness", 0.38f), ("textures", 1f), ("normalStrength", 1.6f));
        AddPreset("Glossy paint", ("metallic", 0.2f), ("roughness", 0.12f), ("reflectance", 0.8f), ("textures", 0f));
        AddPreset("Chalk", ("metallic", 0f), ("roughness", 1f), ("reflectance", 0.2f), ("textures", 1f));
        AddPreset("Emissive panel", ("metallic", 0f), ("roughness", 0.5f), ("emissive", 5f), ("textures", 0f));
        AddPreset("Phong plastic", ("model", 2f), ("roughness", 0.3f), ("metallic", 0f));
    }

    public override string Id => "MAT";

    public override string Title => "Material museum";

    public override string Category => "Materials";

    public override string Summary =>
        "A metallic and roughness sweep beside named materials, with every channel of one sample on a slider.";

    public override IReadOnlyList<string> Features =>
        ["Metallic-roughness PBR", "Shading models", "Procedural textures", "Normal maps", "SSAO"];

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        Shadows = true,
        ShadowMapSize = 2048,
        ShadowCascades = 2,
        ShadowDistance = 40f,
        ShadowSoftness = 2,
        Ssao = true,
        SsaoRadius = 0.35f,
        SsaoIntensity = 1.8f,
        Bloom = true,
        BloomIntensity = 0.3f,
    };

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 2.1f, 0f);
        orbit.Distance = 14f;
        orbit.Yaw = 0.15f;
        orbit.Pitch = 0.12f;
    }

    public override IReadOnlyList<CameraKey> CameraPath =>
    [
        new(new Vector3(0f, 2.1f, 0f), 15f, 0.1f, 0.14f, 0f),
        new(new Vector3(0f, 1.2f, 2f), 5.5f, 0.9f, 0.05f, 4f),
        new(new Vector3(0f, 3f, 0f), 17f, -0.8f, 0.36f, 8f),
        new(new Vector3(0f, 2.1f, 0f), 15f, 0.1f, 0.14f, 12f),
    ];

    public override IEnumerable<(string Label, string Value)> Metrics()
    {
        yield return ("Sweep", $"{Samples.Length} x 5 spheres");
        yield return ("Sample", $"metallic {P("metallic"):F2}, roughness {P("roughness"):F2}");
        yield return ("Textures", B("textures") ? $"512 px, {P("uvScale"):F1}x tiling" : "off");
    }

    protected override void OnBuild()
    {
        Scene.Environment = SceneEnvironment.Default with
        {
            Background = Palette.Rgba(0x0A0C10),
            AmbientColor = new Vector3(0.6f, 0.65f, 0.75f),
            AmbientIntensity = 0.22f,
        };

        _checker = Procedural.Checker(Scene, 512, 8, 0xE8E4DA, 0x2A2D33);
        _grain = Procedural.NoiseTexture(Scene, 512, 6f, 0x6E6A62, 0xF0ECE2);
        _normals = Procedural.NoiseNormals(Scene, 512, 9f, 1.4f);

        // ------------------------------------------------------------- studio
        Material floorMaterial = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x15181D), 0f, 0.4f));
        _floor = floorMaterial;
        Node floor = Scene.AddMesh(Procedural.Ground(Scene, 60f, 1), floorMaterial, name: "floor");
        floor.ReceiveShadow = true;

        Material wall = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x1B1F26), 0f, 0.85f));
        Node backdrop = Scene.AddMesh(Scene.CreateBoxGeometry(30f, 12f, 0.4f), wall, name: "backdrop");
        backdrop.Position = new Vector3(0f, 6f, -7.5f);

        // ------------------------------------------------- metallic/roughness
        Geometry sphere = Scene.CreateSphereGeometry(0.42f, 40, 22);
        for (int row = 0; row < 5; row++)
        {
            float roughness = float.Lerp(0.05f, 1f, row / 4f);
            for (int column = 0; column < Samples.Length; column++)
            {
                float metallic = column / (float)(Samples.Length - 1);
                Material material = Scene.CreateMaterial(
                    MaterialOptions.Pbr(Palette.Rgba(0xC9CCD2), metallic, roughness));
                Node node = Scene.AddMesh(sphere, material, name: $"sweep-{column}-{row}");
                node.Position = new Vector3(
                    (column - ((Samples.Length - 1) * 0.5f)) * 1.1f,
                    4.9f - (row * 1.0f),
                    -6.9f);
                _grid.Add(material);
            }
        }

        // ------------------------------------------------------ named samples
        for (int i = 0; i < Samples.Length; i++)
        {
            (string name, uint color, float metallic, float roughness) = Samples[i];
            Material material = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(color), metallic, roughness));
            Node node = Scene.AddMesh(Scene.CreateSphereGeometry(0.5f, 40, 22), material, name: name);
            node.Position = new Vector3((i - ((Samples.Length - 1) * 0.5f)) * 1.35f, 0.5f, -2.4f);
        }

        // --------------------------------------------------- featured sample
        _feature = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0xD9DCE2), 1f, 0.25f));
        _turntable = Scene.CreateNode(name: "turntable");
        Node plinth = Scene.AddMesh(
            Scene.CreateCylinderGeometry(1.4f, 1.55f, 0.55f, 48),
            Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x23262C), 0f, 0.55f)),
            parent: _turntable,
            name: "plinth");
        plinth.Position = new Vector3(0f, 0.27f, 0f);

        Node feature = Scene.AddMesh(Scene.CreateSphereGeometry(1f, 64, 36), _feature, parent: _turntable, name: "sample");
        feature.Position = new Vector3(0f, 1.55f, 0f);
        _turntable.Position = new Vector3(0f, 0f, 1.6f);

        Node torus = Scene.AddMesh(Scene.CreateTorusGeometry(0.55f, 0.2f, 20, 60), _feature, parent: _turntable, name: "sample ring");
        torus.Position = new Vector3(2.1f, 0.9f, 0f);
        torus.EulerAngles = new Vector3(0.4f, 0f, 0.3f);

        Node cube = Scene.AddMesh(Scene.CreateBoxGeometry(0.9f, 0.9f, 0.9f, 4), _feature, parent: _turntable, name: "sample cube");
        cube.Position = new Vector3(-2.1f, 0.75f, 0f);
        cube.EulerAngles = new Vector3(0f, 0.6f, 0f);

        // ------------------------------------------------------------- lights
        _keyLight = Scene.AddLight(
            Light.Spot(Palette.Kelvin(5600f), 260f, 26f, 18f.ToRadians(), 34f.ToRadians()) with { CastShadow = true },
            name: "key");
        _keyLight.Position = new Vector3(5.5f, 8f, 6.5f);
        _keyLight.LookAt(new Vector3(0f, 1.4f, 1.2f));

        Node fill = Scene.AddLight(Light.Point(Palette.Kelvin(4200f), 45f, 22f), name: "fill");
        fill.Position = new Vector3(-6f, 3.4f, 5f);

        Node rim = Scene.AddLight(Light.Spot(Palette.Kelvin(8000f), 160f, 24f, 14f.ToRadians(), 30f.ToRadians()), name: "rim");
        rim.Position = new Vector3(-4f, 7f, -5.5f);
        rim.LookAt(new Vector3(0f, 1.6f, 0.5f));

        // The sweep is the point of the scene, so it gets a light of its own,
        // wide and soft, coming from where the viewer stands.
        Node wash = Scene.AddLight(
            Light.Spot(Palette.Kelvin(5200f), 900f, 30f, 26f.ToRadians(), 46f.ToRadians()),
            name: "sweep wash");
        wash.Position = new Vector3(0f, 3.4f, 4.5f);
        wash.LookAt(new Vector3(0f, 3.2f, -6.9f));
    }

    protected override void OnApplyParameters()
    {
        ShadingModel model = (ShadingModel)Math.Clamp(I("model"), 0, 3);
        bool textured = B("textures");
        _feature.Options = _feature.Options with
        {
            Shading = model,
            BaseColor = Palette.Rgba(0xD9DCE2),
            Metallic = P("metallic"),
            Roughness = P("roughness"),
            Reflectance = P("reflectance"),
            Emissive = Palette.Rgb(0x4FC3D4),
            EmissiveIntensity = P("emissive"),
            NormalScale = P("normalStrength"),
            UvScale = new Vector2(P("uvScale")),
            BaseColorMap = textured ? _checker : null,
            NormalMap = textured ? _normals : null,
            Wireframe = B("wire"),
            Shininess = 48f,
            Specular = new Vector3(0.35f),
        };

        _floor.Update(options => options with
        {
            BaseColorMap = textured ? _grain : null,
            UvScale = new Vector2(8f),
            Roughness = 0.45f,
        });
    }

    public override void Update(float deltaSeconds, double totalSeconds)
    {
        if (B("turntable"))
        {
            _turntable.EulerAngles = new Vector3(0f, (float)totalSeconds * 0.35f, 0f);
        }
    }
}
