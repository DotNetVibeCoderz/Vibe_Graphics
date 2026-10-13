using System.Numerics;
using ThreeNet.Interop;

namespace ThreeNet.Effects;

/// <summary>
/// Point clouds and wide lines.
/// </summary>
/// <remarks>
/// <para>
/// WebGPU draws a point list one pixel wide and a line list one pixel wide, and
/// offers no equivalent of <c>gl_PointSize</c> or a line width. Anything thicker
/// has to be built out of triangles, which is what these do - the same answer
/// Three.js reaches with <c>Line2</c>.
/// </para>
/// <para>
/// The expansion happens in the vertex shader, not on the CPU: a point becomes
/// four vertices that all sit at the same place, and the shader pushes each
/// corner out along the camera axes. So the buffers are built once and a
/// rotating camera costs nothing.
/// </para>
/// </remarks>
public static class PointsAndLines
{
    /// <summary>
    /// Turns the quad corner in <c>uv</c> into an offset along the camera axes,
    /// in the object space the vertex hook works in.
    /// </summary>
    private const string Billboard = """
        /// The camera right and up axes, rotated into this node's object space.
        /// The view matrix holds them as its columns; the model's upper 3x3 is
        /// transposed to undo the node rotation, which is exact for a rotation
        /// and a uniform scale.
        fn tn_camera_axes_in_object_space() -> mat2x3<f32> {
            let world_right = vec3<f32>(frame.view[0][0], frame.view[1][0], frame.view[2][0]);
            let world_up = vec3<f32>(frame.view[0][1], frame.view[1][1], frame.view[2][1]);
            let bx = vec3<f32>(object.model[0][0], object.model[0][1], object.model[0][2]);
            let by = vec3<f32>(object.model[1][0], object.model[1][1], object.model[1][2]);
            let bz = vec3<f32>(object.model[2][0], object.model[2][1], object.model[2][2]);
            let inv_scale = 1.0 / max(dot(bx, bx), 1.0e-8);
            return mat2x3<f32>(
                vec3<f32>(dot(world_right, bx), dot(world_right, by), dot(world_right, bz)) * inv_scale,
                vec3<f32>(dot(world_up, bx), dot(world_up, by), dot(world_up, bz)) * inv_scale,
            );
        }
        """;

    /// <summary>
    /// A point cloud: every point becomes a camera facing square of
    /// <c>custom0.x</c> metres, rounded off by <c>custom0.y</c>.
    /// </summary>
    /// <remarks>
    /// <para>Material slots:</para>
    /// <list type="bullet">
    /// <item><c>custom0</c> = size in metres, roundness, 0, 0</item>
    /// <item><c>custom1</c> = colour (rgb), emissive strength</item>
    /// </list>
    /// <para>
    /// Build the geometry with <see cref="BuildPoints"/>, which writes the four
    /// corners each point needs.
    /// </para>
    /// </remarks>
    public const string Points = Billboard + """

        fn user_vertex(context: VertexContext) -> vec3<f32> {
            let axes = tn_camera_axes_in_object_space();
            // uv carries the corner as -1..1 on each axis.
            let corner = context.uv * 2.0 - vec2<f32>(1.0);
            let half = context.custom0.x * 0.5;
            return context.position + (axes[0] * corner.x + axes[1] * corner.y) * half;
        }

        fn user_surface(context: SurfaceContext, surface: Surface) -> Surface {
            var out = surface;
            let corner = context.uv * 2.0 - vec2<f32>(1.0);
            // Roundness 0 leaves a square, 1 cuts a disc out of it.
            let roundness = clamp(context.custom0.y, 0.0, 1.0);
            let radius = length(corner);
            if (radius > 1.0 && roundness > 0.5) {
                discard;
            }
            let edge = 1.0 - smoothstep(1.0 - 0.25 * roundness, 1.0, radius) * roundness;
            out.albedo = context.custom1.xyz;
            out.emissive = context.custom1.xyz * context.custom1.w;
            out.alpha = surface.alpha * edge;
            return out;
        }
        """;

    /// <summary>
    /// A wide line: every segment becomes a ribbon that always faces the camera,
    /// optionally broken into dashes.
    /// </summary>
    /// <remarks>
    /// <para>Material slots:</para>
    /// <list type="bullet">
    /// <item><c>custom0</c> = width in metres, dash length, gap length, 0</item>
    /// <item><c>custom1</c> = colour (rgb), emissive strength</item>
    /// </list>
    /// <para>
    /// <see cref="BuildLine"/> writes the four vertices each segment needs: the
    /// segment's far end rides in the normal, and <c>uv</c> carries which side
    /// of the line the vertex is on and how far along the whole line it sits.
    /// </para>
    /// </remarks>
    public const string Lines = Billboard + """

        fn user_vertex(context: VertexContext) -> vec3<f32> {
            let axes = tn_camera_axes_in_object_space();
            let view_normal = cross(axes[0], axes[1]);
            // The other end of this segment travels in the normal slot, which an
            // unlit ribbon has no other use for.
            let along = context.normal - context.position;
            // Flatten the segment onto the screen plane before taking the
            // perpendicular, so the ribbon keeps its width however it is angled.
            let flat = along - view_normal * dot(along, view_normal);
            var side = vec3<f32>(0.0);
            if (dot(flat, flat) > 1.0e-12) {
                side = normalize(cross(view_normal, flat));
            } else {
                side = axes[0];
            }
            // uv.x is -1 or 1: which edge of the ribbon this vertex is.
            return context.position + side * (context.uv.x * context.custom0.x * 0.5);
        }

        fn user_surface(context: SurfaceContext, surface: Surface) -> Surface {
            var out = surface;
            let dash = context.custom0.y;
            let gap = context.custom0.z;
            if (gap > 0.0 && dash > 0.0) {
                // uv.y is the distance along the whole line, in metres.
                let period = dash + gap;
                if (fract(context.uv.y / period) * period > dash) {
                    discard;
                }
            }
            out.albedo = context.custom1.xyz;
            out.emissive = context.custom1.xyz * context.custom1.w;
            return out;
        }
        """;

    /// <summary>
    /// Builds the four corners each point needs. The node holding the result
    /// should carry the points in its own space; the shader orients them.
    /// </summary>
    public static Geometry BuildPoints(Scene scene, ReadOnlySpan<Vector3> points)
    {
        Vertex[] vertices = new Vertex[points.Length * 4];
        uint[] indices = new uint[points.Length * 6];
        for (int i = 0; i < points.Length; i++)
        {
            int v = i * 4;
            // All four corners sit at the point; uv tells the shader which is which.
            vertices[v] = new Vertex(points[i], Vector3.UnitZ, new Vector2(0f, 0f));
            vertices[v + 1] = new Vertex(points[i], Vector3.UnitZ, new Vector2(1f, 0f));
            vertices[v + 2] = new Vertex(points[i], Vector3.UnitZ, new Vector2(1f, 1f));
            vertices[v + 3] = new Vertex(points[i], Vector3.UnitZ, new Vector2(0f, 1f));

            int n = i * 6;
            uint quad = (uint)v;
            indices[n] = quad;
            indices[n + 1] = quad + 1;
            indices[n + 2] = quad + 2;
            indices[n + 3] = quad;
            indices[n + 4] = quad + 2;
            indices[n + 5] = quad + 3;
        }

        return scene.CreateGeometry(vertices, indices);
    }

    /// <summary>
    /// Builds a ribbon along a polyline. Each segment is its own quad, so a
    /// corner shows a notch rather than a mitre - which is what a one metre wide
    /// line would need, and never what a thin one does.
    /// </summary>
    public static Geometry BuildLine(Scene scene, ReadOnlySpan<Vector3> path, bool closed = false)
    {
        int segments = path.Length < 2 ? 0 : closed ? path.Length : path.Length - 1;
        if (segments == 0)
        {
            throw new ArgumentException("a line needs at least two points", nameof(path));
        }

        Vertex[] vertices = new Vertex[segments * 4];
        uint[] indices = new uint[segments * 6];
        float travelled = 0f;
        for (int i = 0; i < segments; i++)
        {
            Vector3 a = path[i];
            Vector3 b = path[(i + 1) % path.Length];
            float length = Vector3.Distance(a, b);

            // Each vertex carries its own end in the position and the far end in
            // the normal, so the shader can work out which way the ribbon runs.
            int v = i * 4;
            vertices[v] = new Vertex(a, b, new Vector2(-1f, travelled));
            vertices[v + 1] = new Vertex(a, b, new Vector2(1f, travelled));
            vertices[v + 2] = new Vertex(b, a, new Vector2(1f, travelled + length));
            vertices[v + 3] = new Vertex(b, a, new Vector2(-1f, travelled + length));
            travelled += length;

            int n = i * 6;
            uint quad = (uint)v;
            indices[n] = quad;
            indices[n + 1] = quad + 1;
            indices[n + 2] = quad + 2;
            indices[n + 3] = quad;
            indices[n + 4] = quad + 2;
            indices[n + 5] = quad + 3;
        }

        return scene.CreateGeometry(vertices, indices);
    }

    /// <summary>A material for <see cref="BuildPoints"/>.</summary>
    /// <param name="size">Width of a point in metres.</param>
    /// <param name="round">Cut the square down to a disc.</param>
    /// <param name="emissive">Above 0 the points glow and reach the bloom pass.</param>
    public static MaterialOptions PointsMaterial(
        Shader shader,
        Vector3 color,
        float size = 0.05f,
        bool round = true,
        float emissive = 0f) =>
        MaterialOptions.Default with
        {
            Shading = ShadingModel.Basic,
            CullMode = CullMode.None,
            AlphaMode = round ? AlphaMode.Mask : AlphaMode.Opaque,
            BaseColor = new Vector4(color, 1f),
            Shader = shader,
            Custom0 = new Vector4(size, round ? 1f : 0f, 0f, 0f),
            Custom1 = new Vector4(color, emissive),
        };

    /// <summary>A material for <see cref="BuildLine"/>.</summary>
    /// <param name="width">Width of the ribbon in metres.</param>
    /// <param name="dash">Length of a dash in metres; 0 draws a solid line.</param>
    /// <param name="gap">Length of the gap between dashes in metres.</param>
    public static MaterialOptions LineMaterial(
        Shader shader,
        Vector3 color,
        float width = 0.02f,
        float dash = 0f,
        float gap = 0f,
        float emissive = 0f) =>
        MaterialOptions.Default with
        {
            Shading = ShadingModel.Basic,
            CullMode = CullMode.None,
            AlphaMode = dash > 0f ? AlphaMode.Mask : AlphaMode.Opaque,
            BaseColor = new Vector4(color, 1f),
            Shader = shader,
            Custom0 = new Vector4(width, dash, gap, 0f),
            Custom1 = new Vector4(color, emissive),
        };
}
