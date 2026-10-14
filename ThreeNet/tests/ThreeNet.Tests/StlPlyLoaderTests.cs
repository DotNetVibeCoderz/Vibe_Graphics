using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// STL and PLY import. No GPU needed: these build geometry on the CPU.
/// </summary>
public class StlPlyLoaderTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "threenet-loaders-" + Guid.NewGuid().ToString("N"));

    public StlPlyLoaderTests() => Directory.CreateDirectory(_directory);

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

    private string Write(string name, string text)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, text);
        return path;
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private const string AsciiStl =
        "solid one\n" +
        "facet normal 0 0 1\n" +
        "outer loop\n" +
        "vertex 0 0 0\n" +
        "vertex 1 0 0\n" +
        "vertex 0 1 0\n" +
        "endloop\n" +
        "endfacet\n" +
        "endsolid one\n";

    /// <summary>One binary STL facet, with whatever is written into the header.</summary>
    private static byte[] BinaryStl(string header)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        byte[] bytes = new byte[80];
        System.Text.Encoding.ASCII.GetBytes(header).CopyTo(bytes, 0);
        writer.Write(bytes);
        writer.Write(1u);
        foreach (float value in new[] { 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f })
        {
            writer.Write(value);
        }

        writer.Write((ushort)0);
        writer.Flush();
        return stream.ToArray();
    }

    [Fact]
    public void AnAsciiStlLoads()
    {
        using Scene scene = new();
        ImportResult result = scene.LoadStl(Write("one.stl", AsciiStl));

        Assert.Equal(1, result.GeometryCount);
        Assert.Equal((3, 3), Counts(result));
        Assert.Equal("one", result.Root.Name);
    }

    [Fact]
    public void ABinaryStlThatSaysSolidIsStillBinary()
    {
        // The trap: "solid" is the ASCII marker, but it also fits in the 80
        // byte binary header and exporters put it there. Reading this as text
        // would find no facets at all.
        using Scene scene = new();
        ImportResult result = scene.LoadStl(Write("two.stl", BinaryStl("solid exported by something")));
        Assert.Equal((3, 3), Counts(result));
    }

    [Fact]
    public void AnAsciiPlyLoads()
    {
        using Scene scene = new();
        ImportResult result = scene.LoadPly(Write("three.ply",
            "ply\n" +
            "format ascii 1.0\n" +
            "element vertex 3\n" +
            "property float x\n" +
            "property float y\n" +
            "property float z\n" +
            "element face 1\n" +
            "property list uchar int vertex_indices\n" +
            "end_header\n" +
            "0 0 0\n" +
            "1 0 0\n" +
            "0 1 0\n" +
            "3 0 1 2\n"));

        Assert.Equal((3, 3), Counts(result));
    }

    [Fact]
    public void APlyWithNoFacesLoadsAsAPointCloud()
    {
        using Scene scene = new();
        ImportResult result = scene.LoadPly(Write("cloud.ply",
            "ply\n" +
            "format ascii 1.0\n" +
            "element vertex 4\n" +
            "property float x\n" +
            "property float y\n" +
            "property float z\n" +
            "end_header\n" +
            "0 0 0\n1 0 0\n0 1 0\n1 1 0\n"));

        // Vertices and no indices, which is what a scanner produces and what
        // PointsAndLines wants to draw.
        (int vertices, int indices) = Counts(result);
        Assert.Equal(4, vertices);
        Assert.Equal(0, indices);
    }

    [Fact]
    public void LoadModelPicksTheLoaderFromTheExtension()
    {
        using Scene scene = new();
        Assert.Equal((3, 3), Counts(scene.LoadModel(Write("four.stl", AsciiStl))));
        Assert.Throws<NotSupportedException>(() => scene.LoadModel(Write("five.xyz", "nope")));
    }

    [Fact]
    public void AMalformedFileReportsRatherThanLoadingSomethingWrong()
    {
        using Scene scene = new();
        // A facet with two vertices is not a triangle. Quietly dropping it
        // would turn a corrupt file into a mesh with a hole in it.
        string path = Write("bad.stl",
            "solid bad\nfacet normal 0 0 1\nouter loop\n" +
            "vertex 0 0 0\nvertex 1 0 0\nendloop\nendfacet\nendsolid bad\n");
        Assert.Throws<ThreeNetException>(() => scene.LoadStl(path));

        Assert.Throws<ThreeNetException>(() => scene.LoadPly(Write("bad.ply", "not a ply at all\n")));
    }

    private static (int Vertices, int Indices) Counts(ImportResult result)
    {
        Geometry geometry = result.Root.Geometry
            ?? throw new InvalidOperationException("the imported node carries no geometry");
        return geometry.Counts;
    }
}
