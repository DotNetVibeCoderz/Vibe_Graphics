using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>The classic metallic / roughness sweep.</summary>
public sealed class MaterialsSample : GallerySample
{
    private const int Steps = 7;

    public override string Title => "PBR sweep";

    public override string Category => "Materials";

    public override string Summary => "Metallic increases upwards, roughness to the right: the whole parameter space at a glance.";

    public override void Build(Scene scene)
    {
        Geometry sphere = scene.CreateSphereGeometry(0.42f, 48, 32);
        Vector4 baseColor = MathHelpers.FromHex(0xD8D8DC);

        for (int y = 0; y < Steps; y++)
        {
            float metallic = y / (float)(Steps - 1);
            for (int x = 0; x < Steps; x++)
            {
                float roughness = x / (float)(Steps - 1);
                Material material = scene.CreateMaterial(MaterialOptions.Pbr(baseColor, metallic, Math.Max(roughness, 0.05f)));
                Node node = scene.AddMesh(sphere, material, name: $"m{metallic:F2}-r{roughness:F2}");
                node.Position = new Vector3((x - ((Steps - 1) * 0.5f)) * 1.1f, (y - ((Steps - 1) * 0.5f)) * 1.1f, 0f);
            }
        }

        // Three lights from different directions so the highlights stay readable
        // across the whole grid.
        Node key = scene.AddLight(Light.Directional(Vector3.One, 2.6f));
        key.Position = new Vector3(4f, 4f, 8f);
        key.LookAt(Vector3.Zero);

        Node warm = scene.AddLight(Light.Point(MathHelpers.FromHex(0xFFB347).AsVector3(), 60f, range: 22f));
        warm.Position = new Vector3(-6f, 3f, 5f);

        Node cool = scene.AddLight(Light.Point(MathHelpers.FromHex(0x4FD1E5).AsVector3(), 60f, range: 22f));
        cool.Position = new Vector3(6f, -3f, 5f);

        scene.Environment = scene.Environment with { AmbientIntensity = 0.06f };
    }

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = Vector3.Zero;
        orbit.Distance = 12f;
        orbit.Yaw = 0f;
        orbit.Pitch = 0f;
    }
}

internal static class ColorExtensions
{
    /// <summary>Drops the alpha channel of a linear RGBA colour.</summary>
    public static Vector3 AsVector3(this Vector4 color) => new(color.X, color.Y, color.Z);
}
