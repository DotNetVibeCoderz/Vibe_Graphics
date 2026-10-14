using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Autodesk 3DS import. No GPU needed.
/// </summary>
public class TdsLoaderTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "threenet-3ds-" + Guid.NewGuid().ToString("N"));

    public TdsLoaderTests() => Directory.CreateDirectory(_directory);

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

    private const ushort Main = 0x4D4D;
    private const ushort Editor = 0x3D3D;
    private const ushort Object = 0x4000;
    private const ushort Trimesh = 0x4100;
    private const ushort Vertices = 0x4110;
    private const ushort Faces = 0x4120;

    private static byte[] Chunk(ushort id, byte[] body)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(id);
        writer.Write((uint)(body.Length + 6));
        writer.Write(body);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>A file holding one triangle in one named object.</summary>
    private string WriteTriangle(string name, string file = "one.3ds")
    {
        using MemoryStream vertexBody = new();
        using (BinaryWriter writer = new(vertexBody, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write((ushort)3);
            // A floor in 3DS space, which is Z up.
            foreach (float[] position in new[]
            {
                new[] { 0f, 0f, 0f },
                new[] { 1f, 0f, 0f },
                new[] { 0f, 1f, 0f },
            })
            {
                foreach (float value in position)
                {
                    writer.Write(value);
                }
            }
        }

        using MemoryStream faceBody = new();
        using (BinaryWriter writer = new(faceBody, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write((ushort)1);
            foreach (ushort value in new ushort[] { 0, 1, 2, 0 })
            {
                writer.Write(value);
            }
        }

        byte[] trimesh = [
            .. Chunk(Vertices, vertexBody.ToArray()),
            .. Chunk(Faces, faceBody.ToArray()),
        ];

        byte[] objectBody = [
            .. System.Text.Encoding.ASCII.GetBytes(name),
            0,
            .. Chunk(Trimesh, trimesh),
        ];

        byte[] bytes = Chunk(Main, Chunk(Editor, Chunk(Object, objectBody)));
        string path = Path.Combine(_directory, file);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void ATriangleLoads()
    {
        using Scene scene = new();
        ImportResult result = scene.Load3ds(WriteTriangle("box01"));

        Assert.Equal(1, result.GeometryCount);
        Node mesh = result.Root.Children.First();
        Assert.Equal("box01", mesh.Name);

        (int vertices, int indices) = mesh.Geometry!.Counts;
        Assert.Equal(3, vertices);
        Assert.Equal(3, indices);
    }

    [Fact]
    public void LoadModelPicksThe3dsLoader()
    {
        using Scene scene = new();
        ImportResult result = scene.LoadModel(WriteTriangle("mesh", "pick.3ds"));
        Assert.Equal(1, result.GeometryCount);
    }

    [Fact]
    public void AFileThatIsNot3dsReports()
    {
        string path = Path.Combine(_directory, "bad.3ds");
        File.WriteAllText(path, "this is not a 3ds file at all");

        using Scene scene = new();
        Assert.Throws<ThreeNetException>(() => scene.Load3ds(path));
    }

    [Fact]
    public void TheModelStandsTheRightWayUp()
    {
        using Scene scene = new();
        ImportResult result = scene.Load3ds(WriteTriangle("floor"));

        // A floor in the file, which is Z up, has to be a floor here, which is
        // Y up: flat in Y and spread over X and Z.
        BoundingBox bounds = scene.GetBounds(result.Root);
        Assert.True(bounds.Max.Y - bounds.Min.Y < 0.001f, $"the floor is not flat in Y: {bounds}");
        Assert.True(bounds.Max.X - bounds.Min.X > 0.5f, $"the floor has no width: {bounds}");
        Assert.True(bounds.Max.Z - bounds.Min.Z > 0.5f, $"the floor has no depth: {bounds}");
    }
}
