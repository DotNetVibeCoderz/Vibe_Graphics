using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>
/// Bokeh depth of field focused on the middle row, and camera motion blur
/// that appears as soon as you drag the orbit camera.
/// </summary>
public sealed class CameraEffectsSample : GallerySample
{
    private readonly List<Node> _spinners = [];

    public override string Title => "Depth of field & motion blur";

    public override string Category => "Post-processing";

    public override string Summary =>
        "Bokeh depth of field keeps the middle row sharp; drag the camera quickly to see motion blur.";

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        DepthOfField = true,
        DofFocusDistance = 11f,
        DofFocusRange = 2.5f,
        DofMaxBlur = 16f,
        MotionBlur = true,
        MotionBlurStrength = 0.8f,
        MotionBlurSamples = 12,
        Bloom = true,
        BloomIntensity = 0.4f,
    };

    public override void Build(Scene scene)
    {
        _spinners.Clear();

        Material floorMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x3A3F4B), 0f, 0.8f));
        Node floor = scene.AddMesh(scene.CreatePlaneGeometry(80f, 80f), floorMaterial, name: "floor");
        floor.EulerAngles = new Vector3(-MathF.PI / 2f, 0f, 0f);

        uint[] palette = [0xF2A541, 0x4FB0C6, 0xE55934, 0x9BC53D, 0xC3B1E1];
        Geometry[] shapes =
        [
            scene.CreateSphereGeometry(0.7f, 32, 24),
            scene.CreateBoxGeometry(1.1f, 1.1f, 1.1f),
            scene.CreateTorusGeometry(0.55f, 0.2f, 16, 48),
        ];

        // Rows at 4, 11 and 20 metres from the camera.
        float[] rows = [-3f, 4f, 13f];
        for (int row = 0; row < rows.Length; row++)
        {
            for (int column = -3; column <= 3; column++)
            {
                Material material = scene.CreateMaterial(MaterialOptions.Pbr(
                    MathHelpers.FromHex(palette[(row * 3 + column + 9) % palette.Length]), 0.3f, 0.35f));
                Node node = scene.AddMesh(shapes[(column + 3 + row) % shapes.Length], material);
                node.Position = new Vector3(column * 1.9f, 0.8f, -rows[row]);
                _spinners.Add(node);
            }
        }

        Material lampMaterial = scene.CreateMaterial(MaterialOptions.Basic(Colors.White) with
        {
            Emissive = new Vector3(1f, 0.85f, 0.6f),
            EmissiveIntensity = 6f,
        });
        Geometry lamp = scene.CreateSphereGeometry(0.12f, 12, 8);
        for (int i = 0; i < 24; i++)
        {
            Node node = scene.AddMesh(lamp, lampMaterial);
            node.Position = new Vector3(-12f + i, 2.8f + (MathF.Sin(i * 0.8f) * 0.4f), -26f);
        }

        Node sun = scene.AddLight(Light.Directional(new Vector3(1f, 0.95f, 0.88f), 2.6f), name: "sun");
        sun.Position = new Vector3(-4f, 8f, 6f);
        sun.LookAt(Vector3.Zero);
        scene.Environment = scene.Environment with
        {
            Background = MathHelpers.FromHex(0x1B2030),
            AmbientIntensity = 0.3f,
        };
    }

    public override void Update(Scene scene, float deltaSeconds, double totalSeconds)
    {
        float t = (float)totalSeconds;
        for (int i = 0; i < _spinners.Count; i++)
        {
            _spinners[i].EulerAngles = new Vector3(t * 0.4f, (t * 0.7f) + i, 0f);
        }
    }

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 0.8f, -4f);
        orbit.Distance = 11f;
        orbit.Yaw = 0f;
        orbit.Pitch = 0.12f;
    }
}
