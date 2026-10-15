using System.IO.Compression;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// USD, IFC and Draco. No GPU needed.
/// </summary>
public class UsdIfcDracoTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "threenet-usd-ifc-" + Guid.NewGuid().ToString("N"));

    public UsdIfcDracoTests() => Directory.CreateDirectory(_directory);

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

    private const string Usda = """
        #usda 1.0
        def Xform "root"
        {
            def Mesh "Plane"
            {
                int[] faceVertexCounts = [4]
                int[] faceVertexIndices = [0, 1, 2, 3]
                point3f[] points = [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)]
            }
        }
        """;

    private const string Ifc = """
        ISO-10303-21;
        HEADER;
        ENDSEC;
        DATA;
        #1=IFCCARTESIANPOINTLIST3D(((0.,0.,0.),(1.,0.,0.),(0.,1.,0.)));
        #2=IFCTRIANGULATEDFACESET(#1,$,.T.,((1,2,3)),$);
        ENDSEC;
        END-ISO-10303-21;
        """;

    [Fact]
    public void UsdaLoads()
    {
        using Scene scene = new();
        ImportResult result = scene.LoadUsd(Write("plane.usda", Usda));

        Assert.Equal(1, result.GeometryCount);
        Node mesh = result.Root.Children.First();
        Assert.Equal("Plane", mesh.Name);
        // A quad is fanned into two triangles.
        Assert.Equal(6, mesh.Geometry!.Counts.Indices);
    }

    [Fact]
    public void AUsdzHoldingATextLayerLoads()
    {
        string path = Path.Combine(_directory, "wrapped.usdz");
        using (FileStream stream = File.Create(path))
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
        using (StreamWriter writer = new(archive.CreateEntry("model.usda").Open()))
        {
            writer.Write(Usda);
        }

        using Scene scene = new();
        Assert.Equal(1, scene.LoadUsd(path).GeometryCount);
    }

    [Fact]
    public void AUsdzHoldingOnlyBinaryUsdSaysSoAndSaysWhatToDo()
    {
        string path = Path.Combine(_directory, "binary.usdz");
        using (FileStream stream = File.Create(path))
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
        using (StreamWriter writer = new(archive.CreateEntry("model.usdc").Open()))
        {
            writer.Write("not really usdc, but the extension is what is checked");
        }

        using Scene scene = new();
        // Several Apple tools write binary by default, so this is the common
        // failure and it has to point somewhere useful rather than just refuse.
        ThreeNetException error = Assert.Throws<ThreeNetException>(() => scene.LoadUsd(path));
        Assert.Contains("usdc", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("usdcat", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IfcTessellatedGeometryLoads()
    {
        using Scene scene = new();
        ImportResult result = scene.LoadIfc(Write("part.ifc", Ifc));

        Assert.Equal(1, result.GeometryCount);
        Assert.Equal(3, result.Root.Children.First().Geometry!.Counts.Indices);
    }

    [Fact]
    public void AParametricOnlyIfcExplainsItself()
    {
        using Scene scene = new();
        string path = Write("walls.ifc", """
            ISO-10303-21;
            DATA;
            #1=IFCEXTRUDEDAREASOLID(#2,#3,#4,1000.);
            ENDSEC;
            """);

        // A model written as extrusions imports as nothing, and that has to
        // look like a limitation rather than a broken file.
        ThreeNetException error = Assert.Throws<ThreeNetException>(() => scene.LoadIfc(path));
        Assert.Contains("parametric", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tessellation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadModelPicksUsdAndIfc()
    {
        using Scene scene = new();
        Assert.Equal(1, scene.LoadModel(Write("pick.usda", Usda)).GeometryCount);
        Assert.Equal(1, scene.LoadModel(Write("pick.ifc", Ifc)).GeometryCount);
    }

    [Fact]
    public void ADracoBufferThatIsNotOneReports()
    {
        using Scene scene = new();
        // The magic is checked first, so a wrong buffer says so rather than
        // failing somewhere inside the bitstream.
        Assert.Throws<ThreeNetException>(
            () => scene.CreateGeometryFromDraco("not a draco buffer"u8));
        Assert.Throws<ThreeNetException>(() => scene.CreateGeometryFromDraco([]));
    }
}
