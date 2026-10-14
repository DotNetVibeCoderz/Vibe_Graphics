using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// MagicaVoxel import. No GPU needed.
/// </summary>
public class VoxLoaderTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "threenet-vox-" + Guid.NewGuid().ToString("N"));

    public VoxLoaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    /// <summary>Writes a minimal VOX file holding the given voxels.</summary>
    private string WriteVox(string name, (byte X, byte Y, byte Z, byte Colour)[] voxels, byte size)
    {
        static byte[] Chunk(string id, byte[] content, byte[] children)
        {
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);
            writer.Write(System.Text.Encoding.ASCII.GetBytes(id));
            writer.Write(content.Length);
            writer.Write(children.Length);
            writer.Write(content);
            writer.Write(children);
            writer.Flush();
            return stream.ToArray();
        }

        using MemoryStream sizeContent = new();
        using (BinaryWriter writer = new(sizeContent, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write((uint)size);
            writer.Write((uint)size);
            writer.Write((uint)size);
        }

        using MemoryStream xyziContent = new();
        using (BinaryWriter writer = new(xyziContent, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write((uint)voxels.Length);
            foreach ((byte x, byte y, byte z, byte colour) in voxels)
            {
                writer.Write(x);
                writer.Write(y);
                writer.Write(z);
                writer.Write(colour);
            }
        }

        byte[] children = [
            .. Chunk("SIZE", sizeContent.ToArray(), []),
            .. Chunk("XYZI", xyziContent.ToArray(), []),
        ];

        byte[] file = [
            .. System.Text.Encoding.ASCII.GetBytes("VOX "),
            .. BitConverter.GetBytes(150u),
            .. Chunk("MAIN", [], children),
        ];

        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, file);
        return path;
    }

    [Fact]
    public void ALoneVoxelIsAWholeCube()
    {
        using Scene scene = new();
        ImportResult result = scene.LoadVox(WriteVox("one.vox", [(0, 0, 0, 1)], 1));

        Assert.Equal(1, result.GeometryCount);
        // The palette becomes a texture rather than a material per colour.
        Assert.Equal(1, result.MaterialCount);
        Assert.Equal(1, result.TextureCount);

        Geometry geometry = Mesh(result).Geometry!;
        (int vertices, int indices) = geometry.Counts;
        Assert.Equal(24, vertices);
        Assert.Equal(36, indices);
    }

    [Fact]
    public void TouchingVoxelsDropTheFacesBetweenThem()
    {
        using Scene scene = new();
        ImportResult result = scene.LoadVox(WriteVox("pair.vox", [(0, 0, 0, 1), (1, 0, 0, 1)], 2));

        // Two separate cubes would be twelve faces. The pair that touch can
        // never be seen, so ten survive.
        (_, int indices) = Mesh(result).Geometry!.Counts;
        Assert.Equal(10, indices / 6);
    }

    [Fact]
    public void ASolidBlockKeepsOnlyItsShell()
    {
        List<(byte, byte, byte, byte)> voxels = [];
        for (byte x = 0; x < 4; x++)
        {
            for (byte y = 0; y < 4; y++)
            {
                for (byte z = 0; z < 4; z++)
                {
                    voxels.Add((x, y, z, 1));
                }
            }
        }

        using Scene scene = new();
        ImportResult result = scene.LoadVox(WriteVox("block.vox", [.. voxels], 4));

        // 64 voxels is 384 faces, of which only the 96 outside ones are
        // visible. This is the whole reason the loader culls at all.
        (_, int indices) = Mesh(result).Geometry!.Counts;
        Assert.Equal(96, indices / 6);
    }

    [Fact]
    public void LoadModelPicksTheVoxLoader()
    {
        using Scene scene = new();
        ImportResult result = scene.LoadModel(WriteVox("pick.vox", [(0, 0, 0, 1)], 1));
        Assert.Equal(1, result.GeometryCount);
    }

    [Fact]
    public void AFileThatIsNotVoxReports()
    {
        string path = Path.Combine(_directory, "bad.vox");
        File.WriteAllText(path, "this is not a vox file");

        using Scene scene = new();
        Assert.Throws<ThreeNetException>(() => scene.LoadVox(path));
    }

    /// <summary>The mesh node the import put under its root.</summary>
    private static Node Mesh(ImportResult result)
    {
        foreach (Node child in result.Root.Children)
        {
            if (child.Geometry is not null)
            {
                return child;
            }
        }

        throw new InvalidOperationException("the import produced no mesh");
    }
}
