using System.IO.Compression;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Collada, KMZ, 3MF, VRML and SVG. No GPU needed.
/// </summary>
public class InterchangeLoaderTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "threenet-interchange-" + Guid.NewGuid().ToString("N"));

    public InterchangeLoaderTests() => Directory.CreateDirectory(_directory);

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

    /// <summary>Packs one named entry into a zip, which is what KMZ and 3MF are.</summary>
    private string WriteZip(string name, string entry, string contents)
    {
        string path = Path.Combine(_directory, name);
        using FileStream stream = File.Create(path);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        using StreamWriter writer = new(archive.CreateEntry(entry).Open());
        writer.Write(contents);
        return path;
    }

    private const string Dae = """
        <?xml version="1.0"?>
        <COLLADA>
          <library_geometries>
            <geometry id="g1" name="Triangle">
              <mesh>
                <source id="pos">
                  <float_array id="pos-array" count="9">0 0 0 1 0 0 0 1 0</float_array>
                  <technique_common><accessor source="#pos-array" count="3" stride="3"/></technique_common>
                </source>
                <vertices id="verts"><input semantic="POSITION" source="#pos"/></vertices>
                <triangles count="1">
                  <input semantic="VERTEX" source="#verts" offset="0"/>
                  <p>0 1 2</p>
                </triangles>
              </mesh>
            </geometry>
          </library_geometries>
        </COLLADA>
        """;

    private const string Model3mf = """
        <?xml version="1.0"?>
        <model unit="millimeter">
          <resources>
            <object id="1" name="Part" type="model">
              <mesh>
                <vertices>
                  <vertex x="0" y="0" z="0"/>
                  <vertex x="1" y="0" z="0"/>
                  <vertex x="0" y="1" z="0"/>
                </vertices>
                <triangles><triangle v1="0" v2="1" v3="2"/></triangles>
              </mesh>
            </object>
          </resources>
        </model>
        """;

    private const string Wrl = """
        #VRML V2.0 utf8
        DEF Shape1 Shape {
          geometry IndexedFaceSet {
            coord Coordinate { point [ 0 0 0, 1 0 0, 0 1 0 ] }
            coordIndex [ 0, 1, 2, -1 ]
          }
        }
        """;

    [Fact]
    public void ColladaLoads()
    {
        using Scene scene = new();
        ImportResult result = scene.LoadCollada(Write("one.dae", Dae));

        Assert.Equal(1, result.GeometryCount);
        Node mesh = result.Root.Children.First();
        Assert.Equal("Triangle", mesh.Name);
        Assert.Equal((3, 3), mesh.Geometry!.Counts);
    }

    [Fact]
    public void KmzUnwrapsToItsCollada()
    {
        using Scene scene = new();
        // A KMZ is a zip with a .dae inside, which is the whole difference.
        ImportResult result = scene.LoadKmz(WriteZip("earth.kmz", "models/model.dae", Dae));
        Assert.Equal(1, result.GeometryCount);
        Assert.Equal((3, 3), result.Root.Children.First().Geometry!.Counts);
    }

    [Fact]
    public void ThreeMfLoads()
    {
        using Scene scene = new();
        ImportResult result = scene.Load3mf(WriteZip("part.3mf", "3D/3dmodel.model", Model3mf));

        Assert.Equal(1, result.GeometryCount);
        Assert.Equal("Part", result.Root.Children.First().Name);
    }

    [Fact]
    public void VrmlLoads()
    {
        using Scene scene = new();
        ImportResult result = scene.LoadVrml(Write("one.wrl", Wrl));

        Assert.Equal(1, result.GeometryCount);
        Assert.Equal("Shape1", result.Root.Children.First().Name);
    }

    [Fact]
    public void SvgExtrudesIntoASolid()
    {
        using Scene scene = new();
        ImportResult result = scene.LoadSvg(
            Write("box.svg", """<svg><rect x="0" y="0" width="10" height="5"/></svg>"""),
            depth: 3f);

        Assert.Equal(1, result.GeometryCount);

        // Extruded, not flat: the solid has to be three deep in z.
        BoundingBox bounds = scene.GetBounds(result.Root);
        Assert.True(bounds.Max.Z - bounds.Min.Z > 2.9f, $"the outline was not extruded: {bounds}");
        Assert.True(bounds.Max.X - bounds.Min.X > 9f, $"the outline lost its width: {bounds}");
    }

    [Fact]
    public void AnSvgOfNothingButArcsReportsWhy()
    {
        using Scene scene = new();
        string path = Write("arc.svg", """<svg><path d="M 0 0 A 5 5 0 0 1 10 10 Z"/></svg>""");

        // Drawing a straight line where an arc belongs would be worse than
        // saying the shape cannot be read.
        ThreeNetException error = Assert.Throws<ThreeNetException>(() => scene.LoadSvg(path));
        Assert.Contains("arc", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadModelPicksEachOfThem()
    {
        using Scene scene = new();
        Assert.Equal(1, scene.LoadModel(Write("pick.dae", Dae)).GeometryCount);
        Assert.Equal(1, scene.LoadModel(Write("pick.wrl", Wrl)).GeometryCount);
        Assert.Equal(1, scene.LoadModel(WriteZip("pick.kmz", "m.dae", Dae)).GeometryCount);
        Assert.Equal(1, scene.LoadModel(WriteZip("pick.3mf", "3D/m.model", Model3mf)).GeometryCount);
        Assert.Equal(
            1,
            scene.LoadModel(Write("pick.svg", """<svg><rect x="0" y="0" width="4" height="4"/></svg>"""))
                .GeometryCount);
    }

    [Fact]
    public void MalformedFilesReport()
    {
        using Scene scene = new();
        Assert.Throws<ThreeNetException>(() => scene.LoadCollada(Write("bad.dae", "<COLLADA/>")));
        Assert.Throws<ThreeNetException>(() => scene.LoadVrml(Write("bad.wrl", "not vrml")));
        Assert.Throws<ThreeNetException>(() => scene.Load3mf(Write("bad.3mf", "not a zip")));
        Assert.Throws<ThreeNetException>(() => scene.LoadKmz(WriteZip("empty.kmz", "readme.txt", "hi")));
    }
}
