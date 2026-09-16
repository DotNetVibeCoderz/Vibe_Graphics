using System.Numerics;
using ThreeNet;
using ThreeNet.Interop;

namespace MotoCross.Game;

/// <summary>Small helper for the hand built meshes (terrain, track ribbon, banners).</summary>
public sealed class MeshBuilder
{
    private readonly List<Vertex> _vertices = [];
    private readonly List<uint> _indices = [];

    public int VertexCount => _vertices.Count;

    public uint Add(Vector3 position, Vector3 normal, Vector2 uv)
    {
        _vertices.Add(new Vertex(position, normal, uv));
        return (uint)(_vertices.Count - 1);
    }

    public void Triangle(uint a, uint b, uint c)
    {
        _indices.Add(a);
        _indices.Add(b);
        _indices.Add(c);
    }

    /// <summary>Counter clockwise quad (a, b, c, d in ring order).</summary>
    public void Quad(uint a, uint b, uint c, uint d)
    {
        Triangle(a, b, c);
        Triangle(a, c, d);
    }

    /// <summary>Adds a flat quad from four corners, with the normal computed from them.</summary>
    public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 uvScale)
    {
        Vector3 normal = Vector3.Normalize(Vector3.Cross(b - a, d - a));
        uint i0 = Add(a, normal, Vector2.Zero);
        uint i1 = Add(b, normal, new Vector2(uvScale.X, 0f));
        uint i2 = Add(c, normal, uvScale);
        uint i3 = Add(d, normal, new Vector2(0f, uvScale.Y));
        Quad(i0, i1, i2, i3);
    }

    public Geometry Build(Scene scene, bool computeNormals = false, bool computeTangents = true)
    {
        Geometry geometry = scene.CreateGeometry(_vertices.ToArray(), _indices.ToArray());
        if (computeNormals)
        {
            geometry.ComputeNormals();
        }

        if (computeTangents)
        {
            geometry.ComputeTangents();
        }

        return geometry;
    }
}
