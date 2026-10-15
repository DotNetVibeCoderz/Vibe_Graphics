using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Mesh optimisation and simplification. No GPU needed.
/// </summary>
public class MeshoptTests
{
    [Fact]
    public void OptimisingKeepsEveryTriangle()
    {
        using Scene scene = new();
        Geometry sphere = scene.CreateSphereGeometry(1f, 32, 24);
        (int vertices, int indices) = sphere.Counts;

        sphere.Optimize();

        (int afterVertices, int afterIndices) = sphere.Counts;
        // Reordering is not removal.
        Assert.Equal(indices, afterIndices);
        // Vertices can go: the fetch pass drops any no index refers to, which
        // is a saving rather than a loss.
        Assert.True(afterVertices <= vertices, $"vertices grew: {vertices} then {afterVertices}");
    }

    [Fact]
    public void SimplifyingRemovesTriangles()
    {
        using Scene scene = new();
        Geometry sphere = scene.CreateSphereGeometry(1f, 48, 32);

        (Geometry simplified, int before, int after, _) = sphere.Simplify(0.25f, 0.05f);

        Assert.True(after < before / 2, $"barely simplified: {before} then {after}");
        Assert.Equal(after, simplified.Counts.Indices / 3);
    }

    [Fact]
    public void SimplifyingLeavesTheOriginalAlone()
    {
        using Scene scene = new();
        Geometry sphere = scene.CreateSphereGeometry(1f, 32, 24);
        (_, int indices) = sphere.Counts;

        sphere.Simplify(0.3f, 0.05f);

        // Returning a new mesh rather than editing in place is what lets the
        // original stay as the highest level of detail.
        Assert.Equal(indices, sphere.Counts.Indices);
    }

    [Fact]
    public void SimplifiedLevelsDriveLevelOfDetail()
    {
        using Scene scene = new();
        Geometry sphere = scene.CreateSphereGeometry(1f, 48, 32);
        (Geometry medium, _, int mediumCount, _) = sphere.Simplify(0.4f, 0.05f);
        (Geometry low, _, int lowCount, _) = sphere.Simplify(0.1f, 0.1f);

        Assert.True(lowCount < mediumCount, $"the levels are not ordered: {mediumCount} then {lowCount}");

        // The point of generating them: feeding the level of detail support
        // without modelling anything by hand.
        Node node = scene.AddMesh(sphere, scene.CreateMaterial(MaterialOptions.Pbr(System.Numerics.Vector4.One)));
        node.SetLevelsOfDetail((sphere, 0f), (medium, 10f), (low, 30f));
    }

    [Fact]
    public void ARatioOutsideZeroToOneIsRefused()
    {
        using Scene scene = new();
        Geometry sphere = scene.CreateSphereGeometry(1f, 16, 12);

        Assert.Throws<ThreeNetException>(() => sphere.Simplify(-0.5f));
        Assert.Throws<ThreeNetException>(() => sphere.Simplify(2f));
    }

    [Fact]
    public void AMeshWithNoIndicesCannotBeSimplified()
    {
        using Scene scene = new();
        // Points have no shared triangles to collapse, so saying so beats
        // returning something empty.
        Geometry loose = scene.CreateGeometry(
            [new Interop.Vertex(), new Interop.Vertex(), new Interop.Vertex()]);

        Assert.Throws<ThreeNetException>(() => loose.Simplify(0.5f));
        Assert.Throws<ThreeNetException>(loose.Optimize);
    }
}
