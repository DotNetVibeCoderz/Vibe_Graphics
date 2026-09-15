using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>Procedural textures, UV tiling and an emissive map.</summary>
public sealed class TexturesSample : GallerySample
{
    private Node _cube = null!;
    private Node _globe = null!;

    public override string Title => "Textures";

    public override string Category => "Materials";

    public override string Summary => "Textures generated in code, UV tiling and offsets, plus an emissive channel.";

    public override void Build(Scene scene)
    {
        Texture checker = scene.CreateTexture(64, 64, BuildChecker(64, 0xFF2E3440, 0xFFD8DEE9));
        Texture gradient = scene.CreateTexture(64, 64, BuildGradient(64));
        checker.SetSampler(linearFilter: false);

        Material checkerMaterial = scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.6f) with
        {
            BaseColorMap = checker,
            UvScale = new Vector2(3f, 3f),
        });
        _cube = scene.AddMesh(scene.CreateBoxGeometry(1.8f, 1.8f, 1.8f), checkerMaterial, name: "checker cube");
        _cube.Position = new Vector3(-1.6f, 0f, 0f);

        Material globeMaterial = scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0.35f, 0.25f) with
        {
            BaseColorMap = gradient,
            EmissiveMap = gradient,
            Emissive = new Vector3(0.35f, 0.15f, 0.05f),
        });
        _globe = scene.AddMesh(scene.CreateSphereGeometry(1f, 64, 32), globeMaterial, name: "gradient sphere");
        _globe.Position = new Vector3(1.6f, 0f, 0f);

        Material floorMaterial = scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.85f) with
        {
            BaseColorMap = checker,
            UvScale = new Vector2(12f, 12f),
        });
        Node floor = scene.AddMesh(scene.CreatePlaneGeometry(30f, 30f), floorMaterial, name: "floor");
        floor.EulerAngles = new Vector3(-MathF.PI / 2f, 0f, 0f);
        floor.Position = new Vector3(0f, -1.4f, 0f);

        Node key = scene.AddLight(Light.Directional(Vector3.One, 3f));
        key.Position = new Vector3(4f, 6f, 4f);
        key.LookAt(Vector3.Zero);

        scene.Environment = scene.Environment with { AmbientIntensity = 0.15f };
    }

    public override void Update(Scene scene, float deltaSeconds, double totalSeconds)
    {
        _cube.EulerAngles = new Vector3((float)totalSeconds * 0.3f, (float)totalSeconds * 0.45f, 0f);
        _globe.EulerAngles = new Vector3(0f, (float)-totalSeconds * 0.3f, 0f);
    }

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = Vector3.Zero;
        orbit.Distance = 8f;
        orbit.Yaw = 0.5f;
        orbit.Pitch = 0.25f;
    }

    /// <summary>Builds an RGBA checkerboard; colours are 0xAARRGGBB.</summary>
    private static byte[] BuildChecker(int size, uint a, uint b)
    {
        byte[] pixels = new byte[size * size * 4];
        int cell = size / 8;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                uint color = ((x / cell) + (y / cell)) % 2 == 0 ? a : b;
                int index = ((y * size) + x) * 4;
                pixels[index + 0] = (byte)(color >> 16);
                pixels[index + 1] = (byte)(color >> 8);
                pixels[index + 2] = (byte)color;
                pixels[index + 3] = (byte)(color >> 24);
            }
        }

        return pixels;
    }

    /// <summary>Builds a warm to cool gradient with a soft vertical falloff.</summary>
    private static byte[] BuildGradient(int size)
    {
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            float v = y / (float)(size - 1);
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)(size - 1);
                int index = ((y * size) + x) * 4;
                pixels[index + 0] = (byte)(255 * (0.9f - (v * 0.5f)));
                pixels[index + 1] = (byte)(255 * (0.35f + (u * 0.3f)));
                pixels[index + 2] = (byte)(255 * (0.2f + (v * 0.7f)));
                pixels[index + 3] = 255;
            }
        }

        return pixels;
    }
}
