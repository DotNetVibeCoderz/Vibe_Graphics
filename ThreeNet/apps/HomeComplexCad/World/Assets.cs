using System.Numerics;
using ThreeNet;

namespace HomeComplexCad.World;

/// <summary>
/// Shared geometry and material library for the whole estate. Geometry is
/// cached by its parameters, so the hundreds of walls, slabs and furniture
/// pieces reuse a handful of GPU buffers instead of uploading their own.
/// </summary>
public sealed class Assets(Scene scene)
{
    private readonly Scene _scene = scene;
    private readonly Dictionary<(float, float, float), Geometry> _boxes = [];
    private readonly Dictionary<(float, float, float, int), Geometry> _cylinders = [];
    private readonly Dictionary<(float, int), Geometry> _spheres = [];
    private readonly Dictionary<(float, float), Geometry> _planes = [];

    public Scene Scene => _scene;

    public Geometry Box(float width, float height, float depth)
    {
        (float, float, float) key = (Round(width), Round(height), Round(depth));
        if (!_boxes.TryGetValue(key, out Geometry? geometry))
        {
            geometry = _scene.CreateBoxGeometry(key.Item1, key.Item2, key.Item3);
            _boxes[key] = geometry;
        }

        return geometry;
    }

    public Geometry Cylinder(float radius, float height, int segments = 16)
    {
        (float, float, float, int) key = (Round(radius), Round(radius), Round(height), segments);
        if (!_cylinders.TryGetValue(key, out Geometry? geometry))
        {
            geometry = _scene.CreateCylinderGeometry(key.Item1, key.Item2, key.Item3, segments);
            _cylinders[key] = geometry;
        }

        return geometry;
    }

    public Geometry Cone(float radius, float height, int segments = 12) =>
        _scene.CreateConeGeometry(radius, height, segments);

    public Geometry Sphere(float radius, int segments = 16)
    {
        (float, int) key = (Round(radius), segments);
        if (!_spheres.TryGetValue(key, out Geometry? geometry))
        {
            geometry = _scene.CreateSphereGeometry(key.Item1, segments, segments / 2);
            _spheres[key] = geometry;
        }

        return geometry;
    }

    public Geometry Plane(float width, float depth)
    {
        (float, float) key = (Round(width), Round(depth));
        if (!_planes.TryGetValue(key, out Geometry? geometry))
        {
            geometry = _scene.CreatePlaneGeometry(key.Item1, key.Item2);
            _planes[key] = geometry;
        }

        return geometry;
    }

    private static float Round(float value) => MathF.Round(value, 3);

    // ---------------------------------------------------------------- helpers

    /// <summary>Adds a box whose centre sits at <paramref name="centre"/>.</summary>
    public Node AddBox(Node? parent, Vector3 centre, Vector3 size, Material material, string name = "box")
    {
        Node node = _scene.AddMesh(Box(size.X, size.Y, size.Z), material, parent, name);
        node.Position = centre;
        return node;
    }

    /// <summary>Adds a box resting on the floor: <paramref name="footprint"/> is the
    /// XZ size, <paramref name="height"/> grows upwards from <paramref name="baseY"/>.</summary>
    public Node AddSlab(Node? parent, Vector3 centreXZ, Vector2 footprint, float height, float baseY, Material material, string name = "slab")
    {
        Node node = _scene.AddMesh(Box(footprint.X, height, footprint.Y), material, parent, name);
        node.Position = new Vector3(centreXZ.X, baseY + (height * 0.5f), centreXZ.Z);
        return node;
    }

    /// <summary>Horizontal quad (floor, ceiling, lawn) facing up or down.</summary>
    public Node AddFloor(Node? parent, Vector3 centre, Vector2 size, Material material, bool faceUp = true, string name = "floor")
    {
        Node node = _scene.AddMesh(Plane(size.X, size.Y), material, parent, name);
        node.Position = centre;
        node.EulerAngles = new Vector3(faceUp ? -MathF.PI / 2f : MathF.PI / 2f, 0f, 0f);
        node.CastShadow = false;
        return node;
    }
}
