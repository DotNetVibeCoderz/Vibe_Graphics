using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

public class SceneTests
{
    [Fact]
    public void NativeAbiMatchesTheBindings()
    {
        Assert.Equal(ThreeNetRuntime.ExpectedAbiVersion, ThreeNetRuntime.NativeAbiVersion);
        Assert.False(string.IsNullOrWhiteSpace(ThreeNetRuntime.NativeVersion));
    }

    [Fact]
    public void ANewSceneOnlyContainsTheRoot()
    {
        using Scene scene = new();

        Assert.Equal(1, scene.NodeCount);
        Assert.False(scene.Root.IsNull);
        Assert.Null(scene.Root.Parent);
    }

    [Fact]
    public void ChildNodesInheritTheParentTransform()
    {
        using Scene scene = new();
        Node parent = scene.CreateNode(name: "parent");
        Node child = parent.CreateChild("child");

        parent.Position = new Vector3(1f, 0f, 0f);
        child.Position = new Vector3(0f, 2f, 0f);

        Assert.Equal(new Vector3(1f, 2f, 0f), child.WorldPosition);
        Assert.Equal(parent, child.Parent);
        Assert.Equal("child", child.Name);
    }

    [Fact]
    public void RemovingANodeRemovesItsSubtree()
    {
        using Scene scene = new();
        Node parent = scene.CreateNode();
        parent.CreateChild();

        Assert.Equal(3, scene.NodeCount);
        parent.Remove();
        Assert.Equal(1, scene.NodeCount);
    }

    [Fact]
    public void FindByNameWalksTheWholeGraph()
    {
        using Scene scene = new();
        Node branch = scene.CreateNode(name: "branch");
        branch.CreateChild("leaf");

        Assert.Equal("leaf", scene.FindByName("leaf")?.Name);
        Assert.Null(scene.FindByName("missing"));
    }

    [Fact]
    public void MaterialOptionsSurviveARoundTrip()
    {
        using Scene scene = new();
        Texture texture = scene.CreateTexture(1, 1, [255, 128, 0, 255]);
        Material material = scene.CreateMaterial(MaterialOptions.Pbr(Colors.Blue, metallic: 0.25f, roughness: 0.75f) with
        {
            BaseColorMap = texture,
            Wireframe = true,
        });

        MaterialOptions options = material.Options;

        Assert.Equal(ShadingModel.Pbr, options.Shading);
        Assert.Equal(0.25f, options.Metallic, 3);
        Assert.Equal(0.75f, options.Roughness, 3);
        Assert.True(options.Wireframe);
        Assert.Equal(texture, options.BaseColorMap);

        // A read-modify-write must not drop the texture bindings.
        material.BaseColor = Colors.Red;
        Assert.Equal(texture, material.Options.BaseColorMap);
    }

    [Fact]
    public void GeometryPrimitivesReportTheirCounts()
    {
        using Scene scene = new();

        (int vertices, int indices) = scene.CreateBoxGeometry(segments: 1).Counts;
        Assert.Equal(24, vertices);
        Assert.Equal(36, indices);

        (int planeVertices, int planeIndices) = scene.CreatePlaneGeometry(1f, 1f, 2, 2).Counts;
        Assert.Equal(9, planeVertices);
        Assert.Equal(24, planeIndices);
    }

    [Fact]
    public void SceneBoundsCoverTheMeshes()
    {
        using Scene scene = new();
        Material material = scene.CreateMaterial(Colors.White);
        Node node = scene.AddMesh(scene.CreateBoxGeometry(2f, 2f, 2f), material);
        node.Position = new Vector3(5f, 0f, 0f);

        BoundingBox bounds = scene.GetBounds();

        Assert.False(bounds.IsEmpty);
        Assert.Equal(new Vector3(5f, 0f, 0f), bounds.Center);
        Assert.Equal(new Vector3(2f, 2f, 2f), bounds.Size);
    }

    [Fact]
    public void RaycastHitsTheNearestMesh()
    {
        using Scene scene = new();
        Material material = scene.CreateMaterial(Colors.White);
        Node near = scene.AddMesh(scene.CreateBoxGeometry(), material, name: "near");
        Node far = scene.AddMesh(scene.CreateBoxGeometry(), material, name: "far");
        far.Position = new Vector3(0f, 0f, -5f);

        IReadOnlyList<RayHit> hits = scene.Raycast(new Ray(new Vector3(0f, 0f, 10f), -Vector3.UnitZ));

        Assert.NotEmpty(hits);
        Assert.Equal(near, hits[0].Node);
        Assert.Equal(9.5f, hits[0].Distance, 2);
        Assert.Contains(hits, hit => hit.Node == far);
    }

    [Fact]
    public void RaycastMissesWhenPointingAway()
    {
        using Scene scene = new();
        scene.AddMesh(scene.CreateBoxGeometry(), scene.CreateMaterial(Colors.White));

        Assert.Empty(scene.Raycast(new Ray(new Vector3(0f, 0f, 10f), Vector3.UnitZ)));
    }

    [Fact]
    public void ObjFilesAreImported()
    {
        string path = Path.Combine(Path.GetTempPath(), $"threenet-{Guid.NewGuid():N}.obj");
        File.WriteAllText(path, "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
        try
        {
            using Scene scene = new();
            ImportResult result = scene.LoadObj(path);

            Assert.False(result.Root.IsNull);
            Assert.Equal(1, result.GeometryCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InvalidHandlesRaiseAThreeNetException()
    {
        using Scene scene = new();
        ThreeNetException exception = Assert.Throws<ThreeNetException>(() => scene.Remove(scene.Root));

        Assert.NotEqual(0, exception.Status);
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }
}
