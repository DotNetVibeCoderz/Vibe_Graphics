using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>
/// Texture streaming and the asset cache: large images load on background
/// threads behind grey placeholders, KTX2 / Basis Universal textures are
/// transcoded to whatever the GPU supports, and repeated requests are served
/// from the cache.
/// </summary>
public sealed class StreamingSample : GallerySample
{
    private const int Tiles = 16;
    private readonly List<(Node Node, Texture Texture)> _tiles = [];
    private string? _folder;

    public override string Title => "Texture streaming & KTX2";

    public override string Category => "Materials";

    public override string Summary =>
        "LoadTextureAsync with placeholders swapped in per frame, cached loads, and Basis Universal / KTX2 compressed textures.";

    public override void Build(Scene scene)
    {
        _tiles.Clear();
        _folder ??= WriteLargeImages();

        // Throttle to one texture per frame so the tiles visibly pop in.
        scene.SetStreamingBudget(1);
        Geometry tile = scene.CreateBoxGeometry(1.4f, 1.4f, 0.1f);
        for (int i = 0; i < Tiles; i++)
        {
            string path = Path.Combine(_folder, $"tile{i % 8}.bmp");
            // Tiles 8-15 request the same files again: the cache hands back the same texture.
            Texture texture = scene.LoadTextureAsync(path);
            Material material = scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.6f) with { BaseColorMap = texture });
            Node node = scene.AddMesh(tile, material, name: $"tile {i}");
            node.Position = new Vector3(((i % 8) - 3.5f) * 1.6f, 1.2f + ((i / 8) * 1.6f), 0f);
            _tiles.Add((node, texture));
        }

        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        string[] compressed = ["checker-uastc.ktx2", "checker-etc1s.basis"];
        for (int i = 0; i < compressed.Length; i++)
        {
            string path = Path.Combine(assets, compressed[i]);
            if (!File.Exists(path))
            {
                continue;
            }

            Material material = scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.5f) with
            {
                BaseColorMap = scene.LoadTextureCached(path),
            });
            Node sphere = scene.AddMesh(scene.CreateSphereGeometry(0.9f, 48, 32), material, name: compressed[i]);
            sphere.Position = new Vector3((i * 2.4f) - 1.2f, 0.9f, 2.5f);
        }

        Node key = scene.AddLight(Light.Directional(Vector3.One, 3f), name: "key");
        key.Position = new Vector3(2f, 5f, 6f);
        key.LookAt(Vector3.Zero);
        scene.Environment = scene.Environment with { AmbientIntensity = 0.35f };
    }

    public override void Update(Scene scene, float deltaSeconds, double totalSeconds)
    {
        foreach ((Node node, Texture texture) in _tiles)
        {
            // Loading tiles wobble until their texture arrives.
            node.EulerAngles = texture.State == TextureState.Loading
                ? new Vector3(0f, MathF.Sin((float)totalSeconds * 8f) * 0.3f, 0f)
                : Vector3.Zero;
        }
    }

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 1.6f, 0f);
        orbit.Distance = 12f;
        orbit.Yaw = 0f;
        orbit.Pitch = 0.1f;
    }

    /// <summary>Writes eight 2048x2048 BMP files so decoding takes long enough to see.</summary>
    private static string WriteLargeImages()
    {
        string folder = Path.Combine(Path.GetTempPath(), "threenet-gallery-streaming");
        Directory.CreateDirectory(folder);
        const int size = 2048;
        for (int index = 0; index < 8; index++)
        {
            string path = Path.Combine(folder, $"tile{index}.bmp");
            if (File.Exists(path))
            {
                continue;
            }

            int rowBytes = size * 3;
            byte[] file = new byte[54 + (rowBytes * size)];
            BitConverter.TryWriteBytes(file.AsSpan(0), (ushort)0x4D42);
            BitConverter.TryWriteBytes(file.AsSpan(2), file.Length);
            BitConverter.TryWriteBytes(file.AsSpan(10), 54);
            BitConverter.TryWriteBytes(file.AsSpan(14), 40);
            BitConverter.TryWriteBytes(file.AsSpan(18), size);
            BitConverter.TryWriteBytes(file.AsSpan(22), size);
            BitConverter.TryWriteBytes(file.AsSpan(26), (ushort)1);
            BitConverter.TryWriteBytes(file.AsSpan(28), (ushort)24);
            float hue = index / 8f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int offset = 54 + (y * rowBytes) + (x * 3);
                    bool ring = ((int)(MathF.Sqrt(((x - 1024) * (x - 1024)) + ((y - 1024) * (y - 1024))) / 96) % 2) == 0;
                    float shade = ring ? 1f : 0.35f;
                    file[offset + 2] = (byte)(255 * shade * (0.5f + (0.5f * MathF.Cos(MathF.Tau * hue))));
                    file[offset + 1] = (byte)(255 * shade * (0.5f + (0.5f * MathF.Cos(MathF.Tau * (hue + 0.33f)))));
                    file[offset] = (byte)(255 * shade * (0.5f + (0.5f * MathF.Cos(MathF.Tau * (hue + 0.66f)))));
                }
            }

            File.WriteAllBytes(path, file);
        }

        return folder;
    }
}
