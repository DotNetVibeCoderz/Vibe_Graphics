using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>Exponential squared fog fading a long corridor of pillars.</summary>
public sealed class FogSample : GallerySample
{
    public override string Title => "Fog & depth";

    public override string Category => "Environment";

    public override string Summary => "Exponential squared fog blended into the background colour builds depth cheaply.";

    public override void Build(Scene scene)
    {
        Vector3 fogColor = new(0.06f, 0.08f, 0.12f);

        Material floorMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x232936), 0f, 0.9f));
        Node floor = scene.AddMesh(scene.CreatePlaneGeometry(200f, 200f), floorMaterial, name: "floor");
        floor.EulerAngles = new Vector3(-MathF.PI / 2f, 0f, 0f);

        Geometry pillar = scene.CreateBoxGeometry(0.8f, 5f, 0.8f);
        Material pillarMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x9BA6B8), 0.05f, 0.55f));
        Material lampMaterial = scene.CreateMaterial(MaterialOptions.Basic(MathHelpers.FromHex(0xFFB347)) with
        {
            Emissive = new Vector3(1f, 0.65f, 0.25f),
            EmissiveIntensity = 3f,
        });
        Geometry lamp = scene.CreateSphereGeometry(0.18f, 16, 12);

        // A receding corridor: the far end disappears into the fog.
        for (int i = 0; i < 30; i++)
        {
            float z = -i * 3.2f;
            foreach (float x in new[] { -3f, 3f })
            {
                Node node = scene.AddMesh(pillar, pillarMaterial, name: $"pillar {i}");
                node.Position = new Vector3(x, 2.5f, z);

                Node lampNode = scene.AddMesh(lamp, lampMaterial, name: $"lamp {i}");
                lampNode.Position = new Vector3(x, 5.1f, z);
            }
        }

        Node key = scene.AddLight(Light.Directional(new Vector3(0.7f, 0.8f, 1f), 1.4f));
        key.Position = new Vector3(5f, 10f, 5f);
        key.LookAt(Vector3.Zero);

        scene.Environment = scene.Environment with
        {
            Background = new Vector4(fogColor, 1f),
            FogColor = fogColor,
            // Density is per world unit; start the falloff a few units out.
            FogDensity = 0.035f,
            FogStart = 6f,
            AmbientIntensity = 0.12f,
        };
    }

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 2f, -14f);
        orbit.Distance = 22f;
        orbit.Yaw = 0.1f;
        orbit.Pitch = 0.18f;
    }
}
