using System.Numerics;
using ThreeNet.Interop;

namespace ThreeNet;

/// <summary>
/// Line meshes that show what a scene is doing rather than what it looks like:
/// where an axis points, how big a thing is, where a light aims, what a skeleton
/// is made of.
/// </summary>
/// <remarks>
/// <para>
/// Every helper is ordinary geometry with <see cref="PrimitiveTopology.LineList"/>
/// and an unlit material, so it draws through the normal pipeline and needs
/// nothing from the core. Lines are one pixel wide, which is what a helper
/// wants; <see cref="Effects.PointsAndLines"/> is there when a line has to be
/// thicker than that.
/// </para>
/// <para>
/// Helpers do not cast or receive shadows, and they are built once - a helper
/// that has to follow something is rebuilt by calling the method again.
/// </para>
/// </remarks>
public static class Helpers
{
    /// <summary>Red X, green Y, blue Z, drawn from the origin of the node.</summary>
    public static Node AddAxes(Scene scene, float size = 1f, Node? parent = null)
    {
        // One segment per axis, each in its own colour, so the mesh needs vertex
        // colours - which there are none of. Three nodes, one per axis, is the
        // honest way and still only three draws.
        Node root = scene.CreateNode(parent, "axes");
        (Vector3 direction, uint color, string name)[] axes =
        [
            (Vector3.UnitX, 0xFF4D4Du, "x"),
            (Vector3.UnitY, 0x4DFF77u, "y"),
            (Vector3.UnitZ, 0x4D8BFFu, "z"),
        ];

        foreach ((Vector3 direction, uint color, string name) in axes)
        {
            Geometry geometry = LineSegments(scene, [Vector3.Zero, direction * size]);
            scene.AddMesh(geometry, UnlitLine(scene, color), root, $"axis-{name}").CastShadow = false;
        }

        return root;
    }

    /// <summary>The twelve edges of a box, for showing bounds.</summary>
    public static Node AddBox(Scene scene, BoundingBox box, uint color = 0xFFD24Du, Node? parent = null)
    {
        Vector3 min = box.Min;
        Vector3 max = box.Max;
        Vector3[] corners =
        [
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z),
            new(max.X, min.Y, max.Z), new(min.X, min.Y, max.Z),
            new(min.X, max.Y, min.Z), new(max.X, max.Y, min.Z),
            new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z),
        ];

        // Bottom ring, top ring, then the four uprights.
        int[] edges =
        [
            0, 1, 1, 2, 2, 3, 3, 0,
            4, 5, 5, 6, 6, 7, 7, 4,
            0, 4, 1, 5, 2, 6, 3, 7,
        ];

        Vector3[] points = new Vector3[edges.Length];
        for (int i = 0; i < edges.Length; i++)
        {
            points[i] = corners[edges[i]];
        }

        Node node = scene.AddMesh(LineSegments(scene, points), UnlitLine(scene, color), parent, "box helper");
        node.CastShadow = false;
        return node;
    }

    /// <summary>A shaft with a head, for showing a direction.</summary>
    public static Node AddArrow(
        Scene scene,
        Vector3 origin,
        Vector3 direction,
        float length = 1f,
        uint color = 0xFFD24Du,
        Node? parent = null)
    {
        Vector3 along = direction.LengthSquared() > 1e-8f ? Vector3.Normalize(direction) : Vector3.UnitY;
        Vector3 tip = origin + (along * length);

        // Two barbs, each in the plane that holds the arrow and whichever world
        // axis the arrow is least parallel to.
        Vector3 reference = MathF.Abs(along.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 side = Vector3.Normalize(Vector3.Cross(along, reference));
        float head = length * 0.15f;

        Vector3[] points =
        [
            origin, tip,
            tip, tip - (along * head) + (side * head * 0.5f),
            tip, tip - (along * head) - (side * head * 0.5f),
        ];

        Node node = scene.AddMesh(LineSegments(scene, points), UnlitLine(scene, color), parent, "arrow");
        node.CastShadow = false;
        return node;
    }

    /// <summary>
    /// The frustum a camera sees, drawn in world space. Pass the camera node;
    /// the shape is read from the camera on it.
    /// </summary>
    public static Node AddCameraFrustum(
        Scene scene,
        Node cameraNode,
        float aspect = 16f / 9f,
        uint color = 0x9B8BFFu,
        Node? parent = null)
    {
        if (cameraNode.Camera is not { } camera)
        {
            throw new ArgumentException("the node carries no camera", nameof(cameraNode));
        }

        float near = camera.Near;
        float far = camera.Far;
        // A near plane at zero and a far plane at infinity both make a frustum
        // nothing can draw, so the helper shows a usable slice instead.
        near = MathF.Max(near, 0.01f);
        far = MathF.Min(far, near + 1000f);

        (float nearHeight, float farHeight) = camera.IsPerspective
            ? (MathF.Tan(camera.FieldOfView * 0.5f) * near * 2f, MathF.Tan(camera.FieldOfView * 0.5f) * far * 2f)
            : (camera.OrthographicHeight, camera.OrthographicHeight);

        Vector3[] Corners(float distance, float height)
        {
            float half = height * 0.5f;
            float halfWidth = half * aspect;
            // The camera looks down its own -Z.
            return
            [
                new(-halfWidth, -half, -distance),
                new(halfWidth, -half, -distance),
                new(halfWidth, half, -distance),
                new(-halfWidth, half, -distance),
            ];
        }

        Vector3[] nearPlane = Corners(near, nearHeight);
        Vector3[] farPlane = Corners(far, farHeight);
        List<Vector3> points = [];
        for (int i = 0; i < 4; i++)
        {
            int next = (i + 1) % 4;
            points.Add(nearPlane[i]);
            points.Add(nearPlane[next]);
            points.Add(farPlane[i]);
            points.Add(farPlane[next]);
            points.Add(nearPlane[i]);
            points.Add(farPlane[i]);
        }

        Node node = scene.AddMesh(
            LineSegments(scene, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(points)),
            UnlitLine(scene, color),
            parent ?? cameraNode,
            "frustum helper");
        node.CastShadow = false;
        return node;
    }

    /// <summary>
    /// A bone between every node of a hierarchy and its parent, in the space of
    /// <paramref name="root"/>. Rebuild it when the pose changes.
    /// </summary>
    public static Node AddSkeleton(Scene scene, Node root, uint color = 0xFF8BD2u, Node? parent = null)
    {
        List<Vector3> points = [];
        Matrix4x4 rootWorld = root.WorldMatrix;
        Matrix4x4.Invert(rootWorld, out Matrix4x4 toLocal);

        void Walk(Node node)
        {
            foreach (Node child in node.Children)
            {
                Vector3 from = Vector3.Transform(node.WorldPosition, toLocal);
                Vector3 to = Vector3.Transform(child.WorldPosition, toLocal);
                // A joint sitting exactly on its parent would be a zero length
                // line, which draws nothing and reads as a hole.
                if (Vector3.DistanceSquared(from, to) > 1e-8f)
                {
                    points.Add(from);
                    points.Add(to);
                }

                Walk(child);
            }
        }

        Walk(root);
        if (points.Count == 0)
        {
            throw new ArgumentException("the hierarchy has no bones to draw", nameof(root));
        }

        Node node = scene.AddMesh(
            LineSegments(scene, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(points)),
            UnlitLine(scene, color),
            parent ?? root,
            "skeleton helper");
        node.CastShadow = false;
        return node;
    }

    /// <summary>A line grid on the XZ plane.</summary>
    public static Node AddGrid(Scene scene, float size = 10f, int divisions = 10, uint color = 0x5A6472u, Node? parent = null)
    {
        Node node = scene.AddMesh(scene.CreateGridGeometry(size, divisions), UnlitLine(scene, color), parent, "grid helper");
        node.CastShadow = false;
        return node;
    }

    /// <summary>Pairs of points as an unindexed line list.</summary>
    private static Geometry LineSegments(Scene scene, ReadOnlySpan<Vector3> points)
    {
        Vertex[] vertices = new Vertex[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            vertices[i] = new Vertex(points[i], Vector3.UnitY, new Vector2(i & 1, 0f));
        }

        return scene.CreateGeometry(vertices, [], PrimitiveTopology.LineList);
    }

    private static Material UnlitLine(Scene scene, uint color) =>
        scene.CreateMaterial(MaterialOptions.Basic(MathHelpers.FromHex(color)) with
        {
            CullMode = CullMode.None,
            // A helper is a reference, not part of the scene: it should be
            // visible even where the scene would hide it.
            DepthWrite = false,
        });
}
