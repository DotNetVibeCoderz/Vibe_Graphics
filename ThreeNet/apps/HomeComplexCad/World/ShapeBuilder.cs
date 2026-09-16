using System.Numerics;
using ThreeNet;
using ThreeNet.Interop;

namespace HomeComplexCad.World;

/// <summary>Builds custom meshes: gable roofs, triangular gable walls, ramps.</summary>
public sealed class ShapeBuilder
{
    private readonly List<Vertex> _vertices = [];
    private readonly List<uint> _indices = [];

    /// <summary>Adds a flat polygon (triangle fan) with a normal from its winding.</summary>
    public void Polygon(IReadOnlyList<Vector3> points, float uvScale = 1f)
    {
        Vector3 normal = Vector3.Normalize(Vector3.Cross(points[1] - points[0], points[2] - points[0]));
        // Planar UVs projected on the dominant plane of the polygon.
        Vector3 tangent = Vector3.Normalize(points[1] - points[0]);
        Vector3 bitangent = Vector3.Cross(normal, tangent);
        uint start = (uint)_vertices.Count;
        foreach (Vector3 point in points)
        {
            Vector3 local = point - points[0];
            _vertices.Add(new Vertex(point, normal, new Vector2(Vector3.Dot(local, tangent), Vector3.Dot(local, bitangent)) * uvScale));
        }

        for (int i = 1; i < points.Count - 1; i++)
        {
            _indices.Add(start);
            _indices.Add(start + (uint)i);
            _indices.Add(start + (uint)i + 1);
        }
    }

    /// <summary>A quad given counter clockwise (seen from the side it faces).</summary>
    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float uvScale = 1f) => Polygon([a, b, c, d], uvScale);

    public Geometry Build(Scene scene)
    {
        Geometry geometry = scene.CreateGeometry(_vertices.ToArray(), _indices.ToArray());
        geometry.ComputeTangents();
        return geometry;
    }

    /// <summary>
    /// Gable roof over a <paramref name="width"/> x <paramref name="depth"/> footprint
    /// (ridge along X), with eaves overhang, both slopes and the two gable ends.
    /// Local origin is the centre of the eaves line at height 0.
    /// </summary>
    public static (Geometry Slopes, Geometry Gables) GableRoof(Scene scene, float width, float depth, float rise, float overhang)
    {
        float hw = (width * 0.5f) + overhang;
        float hd = (depth * 0.5f) + overhang;
        float gw = width * 0.5f;
        float gd = depth * 0.5f;

        ShapeBuilder slopes = new();
        // Front slope (faces +Z and up), back slope (faces -Z and up).
        slopes.Quad(new(-hw, 0f, hd), new(hw, 0f, hd), new(hw, rise, 0f), new(-hw, rise, 0f), 0.5f);
        slopes.Quad(new(hw, 0f, -hd), new(-hw, 0f, -hd), new(-hw, rise, 0f), new(hw, rise, 0f), 0.5f);
        // Undersides, so the eaves read correctly from below.
        slopes.Quad(new(-hw, -0.02f, hd), new(-hw, rise - 0.02f, 0f), new(hw, rise - 0.02f, 0f), new(hw, -0.02f, hd), 0.5f);
        slopes.Quad(new(hw, -0.02f, -hd), new(hw, rise - 0.02f, 0f), new(-hw, rise - 0.02f, 0f), new(-hw, -0.02f, -hd), 0.5f);

        ShapeBuilder gables = new();
        float slope = rise / hd;
        float gableRise = slope * gd;
        float lift = rise - gableRise;
        // Gable walls sit on the wall line, under the slopes.
        gables.Polygon([new(gw, 0f, gd), new(gw, 0f, -gd), new(gw, lift + gableRise, 0f)], 0.5f);
        gables.Polygon([new(-gw, 0f, -gd), new(-gw, 0f, gd), new(-gw, lift + gableRise, 0f)], 0.5f);
        gables.Polygon([new(gw, 0f, -gd), new(gw, 0f, gd), new(gw, lift + gableRise, 0f)], 0.5f);
        gables.Polygon([new(-gw, 0f, gd), new(-gw, 0f, -gd), new(-gw, lift + gableRise, 0f)], 0.5f);
        return (slopes.Build(scene), gables.Build(scene));
    }
}
