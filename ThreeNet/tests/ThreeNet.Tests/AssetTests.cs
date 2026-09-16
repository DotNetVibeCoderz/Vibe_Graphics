using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

public class AssetTests
{
    private static string WritePng(string name, byte r, byte g, byte b)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"threenet-tests-{Environment.ProcessId}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, SolidPng(r, g, b));
        return path;
    }

    private static byte[] SolidPng(byte r, byte g, byte b)
    {
        using MemoryStream stream = new();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(stream, "IHDR", [0, 0, 0, 2, 0, 0, 0, 2, 8, 2, 0, 0, 0]);
        byte[] raw = [0, r, g, b, r, g, b, 0, r, g, b, r, g, b];
        using MemoryStream compressed = new();
        using (System.IO.Compression.ZLibStream zlib = new(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        WriteChunk(stream, "IDAT", compressed.ToArray());
        WriteChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        byte[] length = BitConverter.GetBytes(data.Length);
        Array.Reverse(length);
        stream.Write(length);
        stream.Write(typeBytes);
        stream.Write(data);
        uint crc = Crc32([.. typeBytes, .. data]);
        byte[] crcBytes = BitConverter.GetBytes(crc);
        Array.Reverse(crcBytes);
        stream.Write(crcBytes);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }

    [Fact]
    public void StreamedTexturesBecomeReady()
    {
        using Scene scene = new();
        string red = WritePng("red.png", 255, 0, 0);
        Texture texture = scene.LoadTextureAsync(red);
        Assert.Equal(texture, scene.LoadTextureAsync(red));
        Assert.Contains(texture.State, new[] { TextureState.Loading, TextureState.Ready });

        Assert.True(scene.FinishStreaming(TimeSpan.FromSeconds(20)));
        Assert.Equal(TextureState.Ready, texture.State);
        Assert.Null(texture.LoadError);

        string broken = Path.Combine(Path.GetDirectoryName(red)!, "broken.png");
        File.WriteAllText(broken, "not an image");
        Texture failed = scene.LoadTextureAsync(broken);
        Assert.True(scene.FinishStreaming(TimeSpan.FromSeconds(20)));
        Assert.Equal(TextureState.Failed, failed.State);
        Assert.NotNull(failed.LoadError);

        AssetStats stats = scene.AssetStats;
        Assert.Equal(0, stats.PendingTextures);
        Assert.Equal(1, stats.StreamedTextures); // failures are not counted
        Assert.Equal(1, stats.CacheHits);
        Assert.Throws<ThreeNetException>(() => scene.LoadTextureAsync("does-not-exist.png"));
    }

    [Fact]
    public void CachedModelsShareResources()
    {
        using Scene scene = new();
        string path = AnimationTests.TestAsset("phong_cube.fbx");
        ImportResult first = scene.LoadModelCached(path);
        ImportResult second = scene.LoadModelCached(path);
        Assert.NotEqual(first.Root, second.Root);
        Assert.True(first.GeometryCount > 0);
        Assert.Equal(0, second.GeometryCount);
        Assert.Equal(first.NodeCount, second.NodeCount);

        Texture checker = scene.LoadTextureCached(AnimationTests.TestAsset("checker-etc1s.basis"));
        Assert.Equal(checker, scene.LoadTextureCached(AnimationTests.TestAsset("checker-etc1s.basis")));

        AssetStats stats = scene.AssetStats;
        Assert.Equal(1, stats.CachedModels);
        Assert.Equal(1, stats.CachedTextures);
        Assert.Equal(2, stats.CacheHits);

        scene.ClearAssetCache();
        Assert.Equal(0, scene.AssetStats.CachedModels);
    }

    [Theory]
    [InlineData("checker-etc1s.basis")]
    [InlineData("checker-uastc.ktx2")]
    public void CompressedTexturesRender(string file)
    {
        Renderer renderer;
        try
        {
            renderer = Renderer.CreateOffscreen(RendererOptions.Default with { Width = 64, Height = 64, ToneMapping = ToneMapping.None });
        }
        catch (ThreeNetException)
        {
            return;
        }

        using (renderer)
        {
            using Scene scene = new() { Environment = SceneEnvironment.Default with { Background = new Vector4(0f, 0f, 0f, 1f) } };
            Texture texture = scene.LoadTextureAsync(AnimationTests.TestAsset(file));
            Material material = scene.CreateMaterial(MaterialOptions.Basic(Vector4.One) with { BaseColorMap = texture });
            Node plane = scene.AddMesh(scene.CreatePlaneGeometry(2f, 2f), material);
            plane.Position = new Vector3(0f, 0f, -1f);
            Node camera = scene.AddCamera(Camera.Perspective(1.2f), Vector3.Zero);

            Assert.True(scene.FinishStreaming(TimeSpan.FromSeconds(20)));
            renderer.Render(scene, camera);
            byte[] pixels = renderer.ReadPixels();

            // The test image is blue-ish everywhere with red and green ramps.
            int centre = ((32 * 64) + 32) * 4;
            Assert.True(pixels[centre + 2] > 120, $"expected the texture, got {pixels[centre]},{pixels[centre + 1]},{pixels[centre + 2]}");
            Assert.True(pixels[centre] > 40 && pixels[centre + 1] > 40, $"expected the ramps, got {pixels[centre]},{pixels[centre + 1]},{pixels[centre + 2]}");
        }
    }
}
