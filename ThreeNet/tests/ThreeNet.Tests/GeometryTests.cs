using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// The geometry generators. None of these need a GPU: a generator either
/// produces a well formed mesh or it does not.
/// </summary>
public class GeometryTests
{
    /// <summary>Every index must address a vertex, and triangles come in threes.</summary>
    private static void AssertWellFormed(Geometry geometry, string what)
    {
        (int vertices, int indices) = geometry.Counts;
        Assert.True(vertices > 0, $"{what} produced no vertices");
        Assert.True(indices > 0, $"{what} produced no indices");
        Assert.True(indices % 3 == 0, $"{what} has {indices} indices, which is not whole triangles");
    }

    [Fact]
    public void ACircleIsAFanAroundItsCentre()
    {
        using Scene scene = new();
        Geometry circle = scene.CreateCircleGeometry(2f, 16);
        AssertWellFormed(circle, "circle");

        // The centre plus one vertex per segment boundary.
        (int vertices, int indices) = circle.Counts;
        Assert.Equal(1 + 16 + 1, vertices);
        Assert.Equal(16 * 3, indices);
    }

    [Fact]
    public void ACapsuleIsTallerThanItsStraightPart()
    {
        using Scene scene = new();
        Geometry capsule = scene.CreateCapsuleGeometry(0.5f, 2f, 16, 6);
        AssertWellFormed(capsule, "capsule");

        Node node = scene.AddMesh(capsule, scene.CreateMaterial(Vector4.One));
        BoundingBox bounds = scene.GetBounds(node);
        // Straight part plus a hemisphere at each end.
        Assert.Equal(3f, bounds.Max.Y - bounds.Min.Y, 2);
        Assert.Equal(1f, bounds.Max.X - bounds.Min.X, 2);
    }

    [Fact]
    public void ATorusKnotClosesOnItself()
    {
        using Scene scene = new();
        Geometry knot = scene.CreateTorusKnotGeometry(1f, 0.3f, 64, 8);
        AssertWellFormed(knot, "torus knot");

        Node node = scene.AddMesh(knot, scene.CreateMaterial(Vector4.One));
        BoundingBox bounds = scene.GetBounds(node);
        // The curve radius breathes between half and one and a half times the
        // radius as it winds, so the widest point is 1.5 * radius + tube.
        Assert.True(bounds.Max.X <= (1.5f * 1f) + 0.3f + 1e-3f, $"knot reaches {bounds.Max.X} on x");
        Assert.True(bounds.Max.X > 1f, $"knot collapsed to {bounds.Max.X}");
    }

    [Fact]
    public void ATubeFollowsItsPathWithoutTwisting()
    {
        using Scene scene = new();
        // A path with a straight run in the middle, which is exactly where a
        // Frenet frame would flip.
        Vector3[] path =
        [
            new(0f, 0f, 0f),
            new(1f, 0f, 0f),
            new(2f, 0f, 0f),
            new(3f, 1f, 0f),
            new(4f, 2f, 0f),
        ];

        Geometry tube = scene.CreateTubeGeometry(path, 0.2f, 8);
        AssertWellFormed(tube, "tube");

        Node node = scene.AddMesh(tube, scene.CreateMaterial(Vector4.One));
        BoundingBox bounds = scene.GetBounds(node);
        // A tube has no end caps, so it spans its path rather than overhanging
        // it. The first ring is square to +X and sits exactly on x = 0; the last
        // is tilted by the 45 degree tangent, so it leans out by part of a radius.
        Assert.Equal(0f, bounds.Min.X, 2);
        Assert.InRange(bounds.Max.X, 4f, 4.2f);
        // A twist would throw vertices well outside the radius around the path.
        Assert.True(bounds.Max.Z <= 0.21f && bounds.Min.Z >= -0.21f, "the tube twisted off its path");
    }

    [Fact]
    public void ATubeNeedsTwoPoints()
    {
        using Scene scene = new();
        Assert.Throws<ThreeNetException>(() => scene.CreateTubeGeometry([new Vector3(0f, 0f, 0f)], 0.2f));
    }

    [Fact]
    public void ALatheRevolvesItsProfile()
    {
        using Scene scene = new();
        // A straight profile one unit from the axis is a cylinder.
        Vector2[] profile = [new(1f, -1f), new(1f, 0f), new(1f, 1f)];
        Geometry lathe = scene.CreateLatheGeometry(profile, 24);
        AssertWellFormed(lathe, "lathe");

        Node node = scene.AddMesh(lathe, scene.CreateMaterial(Vector4.One));
        BoundingBox bounds = scene.GetBounds(node);
        Assert.Equal(2f, bounds.Max.Y - bounds.Min.Y, 2);
        Assert.Equal(2f, bounds.Max.X - bounds.Min.X, 1);
    }

    [Fact]
    public void AnExtrudedSquareIsABox()
    {
        using Scene scene = new();
        Vector2[] square = [new(-1f, -1f), new(1f, -1f), new(1f, 1f), new(-1f, 1f)];
        Geometry extruded = scene.CreateExtrudeGeometry(square, 2f);
        AssertWellFormed(extruded, "extrusion");

        Node node = scene.AddMesh(extruded, scene.CreateMaterial(Vector4.One));
        BoundingBox bounds = scene.GetBounds(node);
        Assert.Equal(2f, bounds.Max.X - bounds.Min.X, 3);
        Assert.Equal(2f, bounds.Max.Y - bounds.Min.Y, 3);
        Assert.Equal(2f, bounds.Max.Z - bounds.Min.Z, 3);

        // Two caps of two triangles each, plus a quad per edge.
        (_, int indices) = extruded.Counts;
        Assert.Equal(((2 * 2) + (4 * 2)) * 3, indices);
    }

    [Fact]
    public void AnExtrudedConcaveShapeStillTriangulates()
    {
        using Scene scene = new();
        // An L, which no fan triangulation would get right.
        Vector2[] shape =
        [
            new(0f, 0f), new(2f, 0f), new(2f, 1f), new(1f, 1f), new(1f, 2f), new(0f, 2f),
        ];
        Geometry extruded = scene.CreateExtrudeGeometry(shape, 1f);
        AssertWellFormed(extruded, "concave extrusion");

        // Six corners means four cap triangles a side, however it clips them.
        (_, int indices) = extruded.Counts;
        Assert.Equal(((4 * 2) + (6 * 2)) * 3, indices);
    }

    [Theory]
    [InlineData(Polyhedron.Tetrahedron, 4)]
    [InlineData(Polyhedron.Octahedron, 8)]
    [InlineData(Polyhedron.Icosahedron, 20)]
    [InlineData(Polyhedron.Dodecahedron, 36)]
    public void APolyhedronHasTheFaceCountItShould(Polyhedron kind, int triangles)
    {
        using Scene scene = new();
        Geometry solid = scene.CreatePolyhedronGeometry(kind, 1f);
        AssertWellFormed(solid, kind.ToString());
        (_, int indices) = solid.Counts;
        Assert.Equal(triangles * 3, indices);
    }

    [Fact]
    public void SubdividingAPolyhedronQuadruplesItsFaces()
    {
        using Scene scene = new();
        (_, int flat) = scene.CreatePolyhedronGeometry(Polyhedron.Icosahedron, 1f, 0).Counts;
        (_, int once) = scene.CreatePolyhedronGeometry(Polyhedron.Icosahedron, 1f, 1).Counts;
        (_, int twice) = scene.CreatePolyhedronGeometry(Polyhedron.Icosahedron, 1f, 2).Counts;

        Assert.Equal(flat * 4, once);
        Assert.Equal(once * 4, twice);
    }

    [Fact]
    public void APolyhedronSitsOnItsSphere()
    {
        using Scene scene = new();
        Geometry sphere = scene.CreatePolyhedronGeometry(Polyhedron.Icosahedron, 2f, 2);
        Node node = scene.AddMesh(sphere, scene.CreateMaterial(Vector4.One));
        BoundingBox bounds = scene.GetBounds(node);

        // Every vertex is projected onto the sphere, so the box is the sphere's.
        Assert.Equal(4f, bounds.Max.X - bounds.Min.X, 2);
        Assert.Equal(4f, bounds.Max.Y - bounds.Min.Y, 2);
        Assert.Equal(4f, bounds.Max.Z - bounds.Min.Z, 2);
    }

    [Fact]
    public void AHemisphereLightCarriesBothColours()
    {
        using Scene scene = new();
        Node node = scene.AddLight(Light.Hemisphere(
            sky: new Vector3(0.4f, 0.6f, 1f),
            ground: new Vector3(0.3f, 0.25f, 0.2f),
            intensity: 1.5f));

        Light light = node.Light!.Value;
        Assert.Equal(LightType.Hemisphere, light.Type);
        Assert.Equal(1f, light.Color.Z, 3);
        Assert.Equal(0.2f, light.GroundColor.Z, 3);
        Assert.Equal(1.5f, light.Intensity, 3);
    }
}
