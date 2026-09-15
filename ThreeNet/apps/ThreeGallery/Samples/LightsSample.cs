using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>Directional, point and spot lights moving over a shared stage.</summary>
public sealed class LightsSample : GallerySample
{
    private Node _redPoint = null!;
    private Node _bluePoint = null!;
    private Node _spot = null!;
    private Node _redMarker = null!;
    private Node _blueMarker = null!;

    public override string Title => "Lights";

    public override string Category => "Lighting";

    public override string Summary => "Directional key light, two coloured point lights and a moving spot with a soft penumbra.";

    public override void Build(Scene scene)
    {
        Material floorMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x2B303B), 0f, 0.8f));
        Node floor = scene.AddMesh(scene.CreatePlaneGeometry(40f, 40f, 4, 4), floorMaterial, name: "floor");
        floor.EulerAngles = new Vector3(-MathF.PI / 2f, 0f, 0f);

        // A row of columns gives the lights something to fall off against.
        Geometry column = scene.CreateCylinderGeometry(0.35f, 0.35f, 3f, 24);
        Material columnMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0xC8CCD4), 0.15f, 0.4f));
        for (int i = 0; i < 5; i++)
        {
            Node node = scene.AddMesh(column, columnMaterial, name: $"column {i}");
            node.Position = new Vector3((i - 2) * 2.2f, 1.5f, 0f);
        }

        Node key = scene.AddLight(Light.Directional(new Vector3(1f, 0.97f, 0.9f), 1.2f), name: "key");
        key.Position = new Vector3(6f, 9f, 6f);
        key.LookAt(Vector3.Zero);

        _redPoint = scene.AddLight(Light.Point(MathHelpers.FromHex(0xFF4D4D).AsVector3(), 40f, range: 12f), name: "red point");
        _bluePoint = scene.AddLight(Light.Point(MathHelpers.FromHex(0x4D9BFF).AsVector3(), 40f, range: 12f), name: "blue point");

        // Small emissive spheres mark where the point lights actually are.
        Geometry marker = scene.CreateSphereGeometry(0.12f, 16, 12);
        _redMarker = scene.AddMesh(marker, scene.CreateMaterial(MaterialOptions.Basic(MathHelpers.FromHex(0xFF6B6B))), name: "red marker");
        _blueMarker = scene.AddMesh(marker, scene.CreateMaterial(MaterialOptions.Basic(MathHelpers.FromHex(0x6BB5FF))), name: "blue marker");

        _spot = scene.AddLight(
            Light.Spot(new Vector3(1f, 0.95f, 0.8f), 120f, range: 20f, innerAngle: 0.18f, outerAngle: 0.38f),
            name: "spot");
        _spot.Position = new Vector3(0f, 7f, 0f);
        _spot.LookAt(Vector3.Zero);

        scene.Environment = scene.Environment with { AmbientIntensity = 0.03f };
    }

    public override void Update(Scene scene, float deltaSeconds, double totalSeconds)
    {
        float t = (float)totalSeconds;

        Vector3 red = new(MathF.Sin(t * 0.9f) * 5f, 1.4f, MathF.Cos(t * 0.9f) * 3.5f);
        Vector3 blue = new(MathF.Sin((t * 0.7f) + MathF.PI) * 5f, 2.2f, MathF.Cos((t * 0.7f) + MathF.PI) * 3.5f);
        _redPoint.Position = red;
        _bluePoint.Position = blue;
        _redMarker.Position = red;
        _blueMarker.Position = blue;

        // Sweep the spot across the floor.
        _spot.Position = new Vector3(MathF.Sin(t * 0.35f) * 4f, 7f, MathF.Cos(t * 0.25f) * 2f);
        _spot.LookAt(new Vector3(MathF.Sin(t * 0.5f) * 3f, 0f, 0f));
    }

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 1.4f, 0f);
        orbit.Distance = 14f;
        orbit.Yaw = 0.5f;
        orbit.Pitch = 0.3f;
    }
}
