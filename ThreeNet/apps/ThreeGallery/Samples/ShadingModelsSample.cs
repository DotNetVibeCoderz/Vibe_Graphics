using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>The four shading models compared on identical geometry.</summary>
public sealed class ShadingModelsSample : GallerySample
{
    public override string Title => "Shading models";

    public override string Category => "Materials";

    public override string Summary => "Basic (unlit), Lambert, Blinn-Phong and metallic-roughness PBR on the same sphere.";

    public override void Build(Scene scene)
    {
        Geometry sphere = scene.CreateSphereGeometry(0.9f, 48, 32);
        Vector4 color = MathHelpers.FromHex(0xE0723A);

        MaterialOptions[] models =
        [
            MaterialOptions.Basic(color),
            MaterialOptions.Lambert(color),
            MaterialOptions.Phong(color, shininess: 64f) with { Specular = new Vector3(0.6f) },
            MaterialOptions.Pbr(color, metallic: 0.2f, roughness: 0.3f),
        ];

        float spacing = 2.3f;
        float start = -(models.Length - 1) * spacing * 0.5f;
        for (int i = 0; i < models.Length; i++)
        {
            Node node = scene.AddMesh(sphere, scene.CreateMaterial(models[i]), name: models[i].Shading.ToString());
            node.Position = new Vector3(start + (i * spacing), 0f, 0f);
        }

        // One strong key light makes the differences between the models obvious.
        Node key = scene.AddLight(Light.Directional(Vector3.One, 3.5f));
        key.Position = new Vector3(3f, 4f, 6f);
        key.LookAt(Vector3.Zero);

        Node fill = scene.AddLight(Light.Point(new Vector3(0.4f, 0.5f, 1f), 18f, range: 14f));
        fill.Position = new Vector3(-4f, 1f, 3f);

        scene.Environment = scene.Environment with
        {
            Background = MathHelpers.FromHex(0x0A0C12),
            AmbientIntensity = 0.08f,
        };
    }

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = Vector3.Zero;
        orbit.Distance = 9f;
        orbit.Yaw = 0f;
        orbit.Pitch = 0.12f;
    }
}
