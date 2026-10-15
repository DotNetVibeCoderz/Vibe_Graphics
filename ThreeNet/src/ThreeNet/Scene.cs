using System.Numerics;
using ThreeNet.Interop;

namespace ThreeNet;

/// <summary>
/// A scene graph plus every resource it owns (geometries, materials, textures).
/// Disposing the scene releases all of them.
/// </summary>
/// <example>
/// <code>
/// using var scene = new Scene();
/// var mesh = scene.AddMesh(scene.CreateBoxGeometry(), scene.CreateMaterial(Material.Pbr(Color.Red)));
/// var camera = scene.AddCamera(Camera.Perspective(60f.ToRadians()), new Vector3(0, 1, 5));
/// </code>
/// </example>
public sealed class Scene : IDisposable
{
    private nint _handle;
    private SceneEnvironment _environment = SceneEnvironment.Default;

    /// <summary>Creates an empty scene containing only the root node.</summary>
    public Scene()
    {
        _handle = NativeMethods.tn_scene_create();
        if (_handle == nint.Zero)
        {
            throw new ThreeNetException($"failed to create the scene: {NativeError.GetLastMessage()}");
        }

        Root = new Node(this, NativeMethods.tn_scene_root(_handle));
        Environment = _environment;
        Overlay = new Overlay(this);
        Physics = new PhysicsWorld(this);
    }

    /// <summary>Rigid body physics attached to this scene's nodes.</summary>
    public PhysicsWorld Physics { get; }

    /// <summary>Screen space HUD (panels, images, text) drawn on top of every frame of this scene.</summary>
    public Overlay Overlay { get; }

    internal nint Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);
            return _handle;
        }
    }

    /// <summary>The implicit root node; every other node descends from it.</summary>
    public Node Root { get; }

    /// <summary>Number of live nodes, including the root.</summary>
    public int NodeCount => (int)NativeMethods.tn_scene_node_count(Handle);

    /// <summary>Background, ambient, fog and image based lighting settings.</summary>
    public SceneEnvironment Environment
    {
        get => _environment;
        set
        {
            _environment = value;
            NativeEnvironmentDesc desc = value.ToNative();
            NativeError.Check(NativeMethods.tn_scene_set_environment(Handle, in desc));
        }
    }

    /// <summary>Camera used by <see cref="Renderer.Render(Scene)"/> when none is given.</summary>
    public Node? ActiveCamera
    {
        get;
        set
        {
            field = value;
            NativeError.Check(NativeMethods.tn_scene_set_active_camera(Handle, value?.Id ?? 0));
        }
    }

    // ---------------------------------------------------------------- nodes

    /// <summary>Creates an empty node under <paramref name="parent"/> (the root by default).</summary>
    public Node CreateNode(Node? parent = null, string? name = null)
    {
        uint id = NativeError.CheckHandle(NativeMethods.tn_scene_create_node(Handle, parent?.Id ?? 0));
        Node node = new(this, id);
        if (name is not null)
        {
            node.Name = name;
        }

        return node;
    }

    /// <summary>Removes a node and its whole subtree.</summary>
    public void Remove(Node node) =>
        NativeError.Check(NativeMethods.tn_scene_remove_node(Handle, node.Id));

    /// <summary>Finds the first node with the given name, depth first from the root.</summary>
    public Node? FindByName(string name)
    {
        uint id = NativeMethods.tn_scene_find_by_name(Handle, name);
        return id == 0 ? null : new Node(this, id);
    }

    /// <summary>World space bounds of a subtree (the whole scene when omitted).</summary>
    public BoundingBox GetBounds(Node? node = null)
    {
        NativeError.Check(NativeMethods.tn_scene_compute_bounds(Handle, node?.Id ?? 0, out Vector3 min, out Vector3 max));
        return new BoundingBox(min, max);
    }

    // ------------------------------------------------------------ factories

    /// <summary>Creates a mesh node from a geometry and a material.</summary>
    public Node AddMesh(Geometry geometry, Material material, Node? parent = null, string? name = null)
    {
        Node node = CreateNode(parent, name);
        node.AttachMesh(geometry, material);
        return node;
    }

    /// <summary>Creates a node carrying a light.</summary>
    public Node AddLight(Light light, Node? parent = null, string? name = null)
    {
        Node node = CreateNode(parent, name);
        node.Light = light;
        return node;
    }

    /// <summary>
    /// Creates a camera node. When <paramref name="makeActive"/> is true the
    /// camera also becomes the scene default.
    /// </summary>
    public Node AddCamera(Camera camera, Vector3 position = default, Node? parent = null, bool makeActive = true)
    {
        Node node = CreateNode(parent, "camera");
        node.Camera = camera;
        node.Position = position;
        if (makeActive)
        {
            ActiveCamera = node;
        }

        return node;
    }

    // ----------------------------------------------------------- geometries

    /// <summary>Uploads custom geometry. The data is copied into the scene.</summary>
    public unsafe Geometry CreateGeometry(
        ReadOnlySpan<Vertex> vertices,
        ReadOnlySpan<uint> indices = default,
        PrimitiveTopology topology = PrimitiveTopology.TriangleList)
    {
        if (vertices.IsEmpty)
        {
            throw new ArgumentException("a geometry needs at least one vertex", nameof(vertices));
        }

        fixed (Vertex* vertexPointer = vertices)
        fixed (uint* indexPointer = indices)
        {
            uint id = NativeMethods.tn_geometry_create(
                Handle, vertexPointer, (uint)vertices.Length, indexPointer, (uint)indices.Length, (uint)topology);
            return new Geometry(this, NativeError.CheckHandle(id));
        }
    }

    /// <summary>
    /// A cube map from six square faces, in the order +X, -X, +Y, -Y, +Z, -Z.
    /// </summary>
    /// <remarks>
    /// Set as <see cref="SceneEnvironment.EnvironmentMap"/> it is sampled by
    /// direction outright, which is even everywhere - an equirectangular image
    /// has to go through angles and crowds its samples at the poles.
    /// </remarks>
    public unsafe Texture CreateCubeTexture(
        int size,
        ReadOnlySpan<byte> positiveX,
        ReadOnlySpan<byte> negativeX,
        ReadOnlySpan<byte> positiveY,
        ReadOnlySpan<byte> negativeY,
        ReadOnlySpan<byte> positiveZ,
        ReadOnlySpan<byte> negativeZ,
        TextureFormat format = TextureFormat.Rgba8UnormSrgb)
    {
        // The native side takes one length for all six faces, so it cannot
        // tell a short face from a full one - it would read past the end of the
        // array. The lengths are only knowable here, so they are checked here.
        int bytesPerPixel = format == TextureFormat.Rgba32Float ? 16 : 4;
        int expected = size * size * bytesPerPixel;
        ReadOnlySpan<int> lengths =
        [
            positiveX.Length, negativeX.Length, positiveY.Length,
            negativeY.Length, positiveZ.Length, negativeZ.Length,
        ];
        for (int i = 0; i < lengths.Length; i++)
        {
            if (lengths[i] != expected)
            {
                throw new ArgumentException(
                    $"cube face {i} is {lengths[i]} bytes, but a {size}x{size} face needs {expected}",
                    nameof(size));
            }
        }

        fixed (byte* px = positiveX)
        fixed (byte* nx = negativeX)
        fixed (byte* py = positiveY)
        fixed (byte* ny = negativeY)
        fixed (byte* pz = positiveZ)
        fixed (byte* nz = negativeZ)
        {
            byte*[] faces = [px, nx, py, ny, pz, nz];
            fixed (byte** pointers = faces)
            {
                return new(this, NativeError.CheckHandle(NativeMethods.tn_texture_create_cube(
                    Handle, (uint)size, (uint)format, pointers, (uint)positiveX.Length)));
            }
        }
    }

    /// <summary>
    /// A cube the renderer draws into, six faces of <paramref name="size"/>
    /// square. Capture into it with <see cref="Renderer.RenderToCube"/>, then
    /// bind it as <see cref="SceneEnvironment.EnvironmentMap"/> or sample it
    /// from a material.
    /// </summary>
    public Texture CreateCubeRenderTarget(int size) =>
        new(this, NativeError.CheckHandle(NativeMethods.tn_texture_create_target_cube(Handle, (uint)size)));

    /// <summary>
    /// A texture the renderer draws into rather than one uploaded from pixels.
    /// Pass it to <see cref="Renderer.RenderToTexture"/> and bind it to a
    /// material like any other texture to sample what was drawn.
    /// </summary>
    public Texture CreateRenderTarget(int width, int height) =>
        new(this, NativeError.CheckHandle(NativeMethods.tn_texture_create_target(
            Handle, (uint)width, (uint)height)));

    /// <summary>Subdivided plane on the XY plane facing +Z.</summary>
    public Geometry CreatePlaneGeometry(float width = 1f, float height = 1f, int widthSegments = 1, int heightSegments = 1) =>
        new(this, NativeError.CheckHandle(NativeMethods.tn_geometry_plane(
            Handle, width, height, (uint)widthSegments, (uint)heightSegments)));

    /// <summary>Box centred on the origin.</summary>
    public Geometry CreateBoxGeometry(float width = 1f, float height = 1f, float depth = 1f, int segments = 1) =>
        new(this, NativeError.CheckHandle(NativeMethods.tn_geometry_box(Handle, width, height, depth, (uint)segments)));

    /// <summary>UV sphere.</summary>
    public Geometry CreateSphereGeometry(float radius = 1f, int widthSegments = 32, int heightSegments = 16) =>
        new(this, NativeError.CheckHandle(NativeMethods.tn_geometry_sphere(
            Handle, radius, (uint)widthSegments, (uint)heightSegments)));

    /// <summary>Cylinder, or a truncated cone when the radii differ.</summary>
    public Geometry CreateCylinderGeometry(
        float radiusTop = 0.5f,
        float radiusBottom = 0.5f,
        float height = 1f,
        int radialSegments = 32,
        int heightSegments = 1,
        bool capped = true) =>
        new(this, NativeError.CheckHandle(NativeMethods.tn_geometry_cylinder(
            Handle, radiusTop, radiusBottom, height, (uint)radialSegments, (uint)heightSegments, capped ? 1 : 0)));

    /// <summary>Cone with the apex pointing towards +Y.</summary>
    public Geometry CreateConeGeometry(float radius = 0.5f, float height = 1f, int radialSegments = 32) =>
        new(this, NativeError.CheckHandle(NativeMethods.tn_geometry_cone(Handle, radius, height, (uint)radialSegments)));

    /// <summary>Torus on the XY plane.</summary>
    public Geometry CreateTorusGeometry(
        float radius = 1f,
        float tube = 0.4f,
        int radialSegments = 16,
        int tubularSegments = 48,
        float arc = MathF.Tau) =>
        new(this, NativeError.CheckHandle(NativeMethods.tn_geometry_torus(
            Handle, radius, tube, (uint)radialSegments, (uint)tubularSegments, arc)));

    /// <summary>Line grid on the XZ plane, handy as an editor helper.</summary>
    public Geometry CreateGridGeometry(float size = 10f, int divisions = 10) =>
        new(this, NativeError.CheckHandle(NativeMethods.tn_geometry_grid(Handle, size, (uint)divisions)));

    /// <summary>Filled disc on the XY plane facing +Z. A partial arc makes a pie slice.</summary>
    public Geometry CreateCircleGeometry(
        float radius = 1f,
        int segments = 32,
        float thetaStart = 0f,
        float thetaLength = MathF.Tau) =>
        new(this, NativeError.CheckHandle(NativeMethods.tn_geometry_circle(
            Handle, radius, (uint)segments, thetaStart, thetaLength)));

    /// <summary>
    /// A cylinder closed by a hemisphere at each end, standing on Y.
    /// <paramref name="height"/> is the straight part only, so the capsule is
    /// <c>height + 2 * radius</c> tall - the same convention the physics capsule uses.
    /// </summary>
    public Geometry CreateCapsuleGeometry(
        float radius = 0.5f,
        float height = 1f,
        int radialSegments = 24,
        int capRings = 8) =>
        new(this, NativeError.CheckHandle(NativeMethods.tn_geometry_capsule(
            Handle, radius, height, (uint)radialSegments, (uint)capRings)));

    /// <summary>
    /// A (p, q) torus knot: a curve winding <paramref name="q"/> times around the
    /// torus while passing <paramref name="p"/> times through its hole. The
    /// default (2, 3) is the trefoil.
    /// </summary>
    public Geometry CreateTorusKnotGeometry(
        float radius = 1f,
        float tube = 0.4f,
        int tubularSegments = 128,
        int radialSegments = 16,
        int p = 2,
        int q = 3) =>
        new(this, NativeError.CheckHandle(NativeMethods.tn_geometry_torus_knot(
            Handle, radius, tube, (uint)tubularSegments, (uint)radialSegments, (uint)p, (uint)q)));

    /// <summary>
    /// A tube of constant radius swept along a path. The frame is carried by
    /// parallel transport, so a straight run in the middle does not twist it.
    /// </summary>
    public unsafe Geometry CreateTubeGeometry(
        ReadOnlySpan<Vector3> path,
        float radius = 0.2f,
        int radialSegments = 16,
        bool closed = false)
    {
        fixed (Vector3* points = path)
        {
            return new(this, NativeError.CheckHandle(NativeMethods.tn_geometry_tube(
                Handle, points, (uint)path.Length, radius, (uint)radialSegments, closed ? 1 : 0)));
        }
    }

    /// <summary>
    /// A profile revolved around the Y axis - a vase, a bowl, a wine glass. The
    /// X of each profile point is its distance from the axis.
    /// </summary>
    public unsafe Geometry CreateLatheGeometry(
        ReadOnlySpan<Vector2> profile,
        int segments = 32,
        float phiStart = 0f,
        float phiLength = MathF.Tau)
    {
        fixed (Vector2* points = profile)
        {
            return new(this, NativeError.CheckHandle(NativeMethods.tn_geometry_lathe(
                Handle, points, (uint)profile.Length, (uint)segments, phiStart, phiLength)));
        }
    }

    /// <summary>
    /// A closed 2D outline given depth along Z, with both caps filled. The
    /// outline may wind either way; it is triangulated by ear clipping, so it
    /// must be simple (no self intersections).
    /// </summary>
    public unsafe Geometry CreateExtrudeGeometry(ReadOnlySpan<Vector2> shape, float depth = 1f)
    {
        fixed (Vector2* points = shape)
        {
            return new(this, NativeError.CheckHandle(NativeMethods.tn_geometry_extrude(
                Handle, points, (uint)shape.Length, depth)));
        }
    }

    /// <summary>
    /// A platonic solid projected onto a sphere. <paramref name="detail"/>
    /// subdivides every face that many times, so a high detail icosahedron is a
    /// geodesic sphere with far more even triangles than a UV sphere.
    /// </summary>
    public Geometry CreatePolyhedronGeometry(Polyhedron kind, float radius = 1f, int detail = 0) =>
        new(this, NativeError.CheckHandle(NativeMethods.tn_geometry_polyhedron(
            Handle, (uint)kind, radius, (uint)detail)));

    // ------------------------------------------------------------ materials

    /// <summary>Registers a material and returns its handle.</summary>
    public Material CreateMaterial(in MaterialOptions options)
    {
        NativeMaterialDesc desc = options.ToNative();
        return new Material(this, NativeError.CheckHandle(NativeMethods.tn_material_create(Handle, in desc)));
    }

    /// <summary>Registers a default physically based material with the given colour.</summary>
    public Material CreateMaterial(Vector4 baseColor, float metallic = 0f, float roughness = 0.5f) =>
        CreateMaterial(new MaterialOptions
        {
            BaseColor = baseColor,
            Metallic = metallic,
            Roughness = roughness,
        });

    // ------------------------------------------------------------ animation

    /// <summary>Every animation clip in the scene (imported or created), oldest first.</summary>
    public unsafe IReadOnlyList<AnimationClip> Animations
    {
        get
        {
            int count = NativeMethods.tn_scene_get_animations(Handle, null, 0);
            NativeError.Check(count);
            uint[] ids = new uint[count];
            fixed (uint* pointer = ids)
            {
                NativeMethods.tn_scene_get_animations(Handle, pointer, (uint)ids.Length);
            }

            return ids.Select(id => new AnimationClip(this, id)).ToArray();
        }
    }

    /// <summary>Creates an empty clip; add keys with <see cref="AnimationClip.AddTranslation"/> and friends.</summary>
    public AnimationClip CreateAnimation(string? name = null) =>
        new(this, NativeError.CheckHandle(NativeMethods.tn_animation_create(Handle, name)));

    /// <summary>
    /// Advances every playing animation by <paramref name="deltaSeconds"/>, applies the
    /// pose and deforms skinned meshes. Call once per frame before rendering.
    /// </summary>
    public void UpdateAnimations(float deltaSeconds) =>
        NativeError.Check(NativeMethods.tn_scene_update_animations(Handle, deltaSeconds));

    // -------------------------------------------------------------- shaders

    /// <summary>
    /// Compiles custom shader hooks (see <see cref="Shader"/>). Throws a
    /// <see cref="ThreeNetException"/> carrying the compiler message when the
    /// source does not translate or validate.
    /// </summary>
    public Shader CreateShader(string source, ShaderLanguage language = ShaderLanguage.Wgsl, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        uint id = NativeMethods.tn_shader_create(Handle, (uint)language, source, name);
        return new Shader(this, NativeError.CheckHandle(id));
    }

    // ------------------------------------------------------------- textures

    /// <summary>Loads a PNG / JPEG / BMP / TGA / HDR image from disk.</summary>
    public Texture LoadTexture(string path, bool srgb = true) =>
        new(this, NativeError.CheckHandle(NativeMethods.tn_texture_load_file(Handle, path, srgb ? 1 : 0)));

    /// <summary>Decodes an encoded image held in memory.</summary>
    public unsafe Texture LoadTexture(ReadOnlySpan<byte> encodedBytes, bool srgb = true)
    {
        fixed (byte* pointer = encodedBytes)
        {
            uint id = NativeMethods.tn_texture_load_memory(Handle, pointer, (uint)encodedBytes.Length, srgb ? 1 : 0);
            return new Texture(this, NativeError.CheckHandle(id));
        }
    }

    /// <summary>
    /// Loads an image (PNG, JPEG, HDR, KTX2, Basis, ...) on background threads and
    /// returns immediately. The texture shows a grey placeholder until the
    /// renderer swaps the decoded image in at the start of a frame. Requests
    /// for the same path and colour space return the same texture.
    /// </summary>
    public Texture LoadTextureAsync(string path, bool srgb = true) =>
        new(this, NativeError.CheckHandle(NativeMethods.tn_texture_load_async(Handle, path, srgb ? 1 : 0)));

    /// <summary>Loads an image once; later calls with the same path and colour space return the same texture.</summary>
    public Texture LoadTextureCached(string path, bool srgb = true) =>
        new(this, NativeError.CheckHandle(NativeMethods.tn_texture_load_cached(Handle, path, srgb ? 1 : 0)));

    /// <summary>Applies finished background loads now (rendering does this automatically). Returns how many were applied.</summary>
    public int PollStreaming() => NativeMethods.tn_scene_poll_streaming(Handle);

    /// <summary>Waits until every streamed texture is applied. Returns <see langword="false"/> on timeout.</summary>
    public bool FinishStreaming(TimeSpan timeout) =>
        NativeMethods.tn_scene_finish_streaming(Handle, (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue)) == 1;

    /// <summary>How many finished textures are swapped in per frame (default 4).</summary>
    public void SetStreamingBudget(int uploadsPerFrame) =>
        NativeError.Check(NativeMethods.tn_scene_set_streaming_budget(Handle, (uint)Math.Max(1, uploadsPerFrame)));

    /// <summary>
    /// Places a model, importing the file only the first time. Later calls clone
    /// a hidden prototype and share its geometry, materials and textures, so
    /// the counts in the result are only non-zero for the first import. Models
    /// with animations or skins are imported every time.
    /// </summary>
    public ImportResult LoadModelCached(string path, Node? parent = null)
    {
        NativeError.Check(NativeMethods.tn_load_model_cached(Handle, path, parent?.Id ?? 0, out NativeImportResult result));
        return ImportResult.From(this, result);
    }

    public AssetStats AssetStats
    {
        get
        {
            NativeError.Check(NativeMethods.tn_scene_get_asset_stats(Handle, out NativeAssetStats stats));
            return new AssetStats(
                (int)stats.PendingTextures,
                (int)stats.CachedTextures,
                (int)stats.CachedModels,
                (long)stats.CacheHits,
                (long)stats.CacheMisses,
                (long)stats.StreamedTextures);
        }
    }

    /// <summary>
    /// Compiles a compute kernel from WGSL.
    /// </summary>
    /// <remarks>
    /// The storage buffers a dispatch is given are bound in order, at
    /// <c>@group(0) @binding(0)</c> upwards, each declared
    /// <c>var&lt;storage, read_write&gt;</c>. Run it with
    /// <see cref="Renderer.Dispatch"/>.
    /// </remarks>
    public ComputeKernel CreateComputeKernel(string source, string entryPoint = "main", string name = "kernel")
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(entryPoint);
        uint id = NativeMethods.tn_compute_kernel_create(Handle, name, source, entryPoint);
        return new ComputeKernel(this, NativeError.CheckHandle(id));
    }

    /// <summary>A block of GPU memory for compute kernels to work on.</summary>
    public StorageBuffer CreateStorageBuffer(long sizeInBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeInBytes);
        uint id = NativeMethods.tn_storage_buffer_create(Handle, (ulong)sizeInBytes);
        return new StorageBuffer(this, NativeError.CheckHandle(id));
    }

    /// <summary>
    /// Directional ambient light for the whole scene, baked with
    /// <see cref="Renderer.BakeLightProbe"/>. Null clears it.
    /// </summary>
    /// <remarks>
    /// It adds to <see cref="SceneEnvironment.AmbientColor"/> rather than
    /// replacing it, so a scene can have a floor of constant ambient and a
    /// probe for the shape of the light on top.
    /// </remarks>
    public unsafe LightProbe? LightProbe
    {
        get
        {
            Vector3[] coefficients = new Vector3[ThreeNet.LightProbe.CoefficientCount];
            float intensity;
            fixed (Vector3* pointer = coefficients)
            {
                NativeError.Check(NativeMethods.tn_scene_get_light_probe(Handle, pointer, &intensity));
            }

            LightProbe probe = new(coefficients);
            return probe.IsDark ? null : probe;
        }

        set
        {
            if (value is null)
            {
                NativeError.Check(NativeMethods.tn_scene_set_light_probe(Handle, null, 0f));
                return;
            }

            fixed (Vector3* pointer = value.Storage)
            {
                NativeError.Check(NativeMethods.tn_scene_set_light_probe(Handle, pointer, _lightProbeIntensity));
            }
        }
    }

    private float _lightProbeIntensity = 1f;

    /// <summary>Scales the light probe; 0 turns it off without discarding it.</summary>
    public unsafe float LightProbeIntensity
    {
        get
        {
            Vector3[] coefficients = new Vector3[ThreeNet.LightProbe.CoefficientCount];
            float intensity;
            fixed (Vector3* pointer = coefficients)
            {
                NativeError.Check(NativeMethods.tn_scene_get_light_probe(Handle, pointer, &intensity));
            }

            return intensity;
        }

        set
        {
            _lightProbeIntensity = value;
            LightProbe? probe = LightProbe;
            if (probe is not null)
            {
                // Writing the probe back is what carries the new intensity
                // across, since the two live together on the native side.
                LightProbe = probe;
            }
        }
    }

    /// <summary>Forgets cached paths and removes hidden model prototypes; placed instances stay.</summary>
    public void ClearAssetCache() => NativeError.Check(NativeMethods.tn_scene_clear_asset_cache(Handle));

    /// <summary>Uploads raw pixels; the buffer must be tightly packed.</summary>
    public unsafe Texture CreateTexture(int width, int height, ReadOnlySpan<byte> pixels, TextureFormat format = TextureFormat.Rgba8UnormSrgb)
    {
        fixed (byte* pointer = pixels)
        {
            uint id = NativeMethods.tn_texture_create(
                Handle, (uint)width, (uint)height, (uint)format, pointer, (uint)pixels.Length);
            return new Texture(this, NativeError.CheckHandle(id));
        }
    }

    /// <summary>
    /// A colour lookup table that changes nothing: every entry is the colour
    /// that looks it up. Useful as the starting point for a look, and as the
    /// control when checking that a table is being applied at all.
    /// </summary>
    /// <remarks>
    /// The layout is the one <see cref="RendererOptions.ColorLut"/> expects: a
    /// strip <paramref name="size"/> slices wide, each slice
    /// <paramref name="size"/> by <paramref name="size"/>, with red across a
    /// slice, green down it and blue choosing the slice. Edit the pixels and
    /// upload them again with <see cref="Texture.Update(ReadOnlySpan{byte})"/>.
    /// </remarks>
    public Texture CreateNeutralColorLut(int size = 32)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 128);

        int width = size * size;
        byte[] pixels = new byte[width * size * 4];
        float last = size - 1;
        for (int blue = 0; blue < size; blue++)
        {
            byte b = (byte)Math.Round(blue / last * 255f);
            for (int green = 0; green < size; green++)
            {
                byte g = (byte)Math.Round(green / last * 255f);
                int row = green * width;
                for (int red = 0; red < size; red++)
                {
                    int i = (row + (blue * size) + red) * 4;
                    pixels[i] = (byte)Math.Round(red / last * 255f);
                    pixels[i + 1] = g;
                    pixels[i + 2] = b;
                    pixels[i + 3] = 255;
                }
            }
        }

        // Unorm rather than sRGB: a table holds output values, and letting the
        // hardware decode them would apply the transfer curve a second time.
        Texture lut = CreateTexture(width, size, pixels, TextureFormat.Rgba8Unorm);
        // Linear filtering across a slice, and no mipmaps - a lower mip would
        // blend neighbouring slices into each other.
        lut.SetSampler(WrapMode.ClampToEdge, WrapMode.ClampToEdge, linearFilter: true, mipmaps: false);
        return lut;
    }

    // -------------------------------------------------------------- loading

    /// <summary>Imports a glTF 2.0 or GLB file into the scene.</summary>
    public ImportResult LoadGltf(string path, Node? parent = null)
    {
        NativeError.Check(NativeMethods.tn_load_gltf(Handle, path, parent?.Id ?? 0, out NativeImportResult result));
        return ImportResult.From(this, result);
    }

    /// <summary>Imports glTF / GLB from memory.</summary>
    public unsafe ImportResult LoadGltf(ReadOnlySpan<byte> bytes, Node? parent = null)
    {
        fixed (byte* pointer = bytes)
        {
            NativeError.Check(NativeMethods.tn_load_gltf_memory(
                Handle, pointer, (uint)bytes.Length, parent?.Id ?? 0, out NativeImportResult result));
            return ImportResult.From(this, result);
        }
    }

    /// <summary>Imports a Wavefront OBJ file.</summary>
    /// <summary>
    /// Imports a binary or ASCII FBX file: meshes, Lambert/Phong materials with
    /// textures, the node hierarchy, skeletons and animation stacks. Units and
    /// up axis are converted to Y-up metres.
    /// </summary>
    public ImportResult LoadFbx(string path, Node? parent = null)
    {
        NativeError.Check(NativeMethods.tn_load_fbx(Handle, path, parent?.Id ?? 0, out NativeImportResult result));
        return ImportResult.From(this, result);
    }

    /// <summary>Imports FBX from memory (embedded textures only).</summary>
    public unsafe ImportResult LoadFbx(ReadOnlySpan<byte> bytes, Node? parent = null)
    {
        fixed (byte* pointer = bytes)
        {
            NativeError.Check(NativeMethods.tn_load_fbx_memory(Handle, pointer, (uint)bytes.Length, parent?.Id ?? 0, out NativeImportResult result));
            return ImportResult.From(this, result);
        }
    }

    /// <summary>
    /// Imports a model, picking the loader from the file extension (.gltf,
    /// .glb, .fbx, .obj, .stl, .ply, .vox, .3ds, .dae, .kmz, .3mf, .wrl, .svg,
    /// .usda, .usdz, .ifc).
    /// </summary>
    /// <remarks>
    /// An SVG is extruded by a default depth; call <see cref="LoadSvg"/>
    /// directly to choose it.
    /// </remarks>
    public ImportResult LoadModel(string path, Node? parent = null) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".gltf" or ".glb" => LoadGltf(path, parent),
            ".fbx" => LoadFbx(path, parent),
            ".obj" => LoadObj(path, parent),
            ".stl" => LoadStl(path, parent),
            ".ply" => LoadPly(path, parent),
            ".vox" => LoadVox(path, parent),
            ".3ds" => Load3ds(path, parent),
            ".dae" => LoadCollada(path, parent),
            ".kmz" => LoadKmz(path, parent),
            ".3mf" => Load3mf(path, parent),
            ".wrl" => LoadVrml(path, parent),
            ".svg" => LoadSvg(path, parent),
            ".usda" or ".usdz" => LoadUsd(path, parent),
            ".ifc" => LoadIfc(path, parent),
            var extension => throw new NotSupportedException($"no importer for '{extension}' files"),
        };

    public ImportResult LoadObj(string path, Node? parent = null)
    {
        NativeError.Check(NativeMethods.tn_load_obj(Handle, path, parent?.Id ?? 0, out NativeImportResult result));
        return ImportResult.From(this, result);
    }

    /// <summary>Imports an STL file, binary or ASCII.</summary>
    /// <remarks>
    /// STL is a triangle soup: no indices, no UVs, no materials and no names,
    /// so every triangle carries its own three vertices and the mesh is given a
    /// default material. Binary and ASCII are told apart by the file length
    /// rather than by the leading word, because exporters write "solid" into
    /// the binary header too. A facet whose stored normal is zero - which is
    /// common - takes its normal from the winding instead.
    /// </remarks>
    public ImportResult LoadStl(string path, Node? parent = null)
    {
        NativeError.Check(NativeMethods.tn_load_stl(Handle, path, parent?.Id ?? 0, out NativeImportResult result));
        return ImportResult.From(this, result);
    }

    /// <summary>
    /// Decodes a Draco compressed mesh into geometry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Draco quantises attributes and codes the connectivity with edgebreaker,
    /// which is why a buffer is often a tenth the size of the triangles it
    /// holds. Pass a whole Draco buffer: the one inside a glTF
    /// <c>KHR_draco_mesh_compression</c> buffer view, or what
    /// <c>draco_encoder</c> writes.
    /// </para>
    /// <para>
    /// The loss is the format's choice and not the reader's - positions come
    /// back on whatever quantisation grid the encoder picked, so a mesh encoded
    /// at eleven bits has eleven bits of position. Decoding only; nothing here
    /// writes Draco.
    /// </para>
    /// </remarks>
    public unsafe Geometry CreateGeometryFromDraco(ReadOnlySpan<byte> data)
    {
        fixed (byte* pointer = data)
        {
            uint id = NativeMethods.tn_geometry_from_draco(Handle, pointer, (ulong)data.Length);
            return new Geometry(this, NativeError.CheckHandle(id));
        }
    }

    /// <summary>Imports a USD <c>.usda</c> file, or a <c>.usdz</c> holding one.</summary>
    /// <remarks>
    /// <para>
    /// USD is a scene description language with layers, references, variants
    /// and composition arcs. What this reads is the shape of it a loader can
    /// support without becoming an implementation of USD: the <c>def Mesh</c>
    /// prims in a text layer, which is what most AR exporters write after
    /// flattening a scene.
    /// </para>
    /// <para>
    /// Binary <c>.usdc</c> is not read. It is USD's own memory mappable crystal
    /// format, and guessing at it would produce wrong geometry rather than an
    /// error - so a USDZ holding only <c>.usdc</c> says so and points at
    /// <c>usdcat</c>, which converts one to text. Several Apple tools write
    /// binary by default.
    /// </para>
    /// </remarks>
    public ImportResult LoadUsd(string path, Node? parent = null)
    {
        NativeError.Check(NativeMethods.tn_load_usd(Handle, path, parent?.Id ?? 0, out NativeImportResult result));
        return ImportResult.From(this, result);
    }

    /// <summary>Imports the tessellated geometry out of an <c>.ifc</c> file.</summary>
    /// <remarks>
    /// <para>
    /// IFC is a STEP physical file: numbered entities referring to each other.
    /// This reads the geometry that is already polygons -
    /// <c>IfcTriangulatedFaceSet</c>, which is how IFC4 exporters write meshes,
    /// and the older <c>IfcPolyLoop</c> faceted form.
    /// </para>
    /// <para>
    /// The parametric half of IFC is not read, and it is a large half: swept
    /// solids, extrusions along a directrix, constructive solid geometry with
    /// booleans, and the placement tree that positions them. Evaluating those
    /// is a geometry kernel rather than a loader. A file written entirely that
    /// way reports it, and says to re-export with tessellation turned on,
    /// rather than importing an empty model that looks like a bug.
    /// </para>
    /// </remarks>
    public ImportResult LoadIfc(string path, Node? parent = null)
    {
        NativeError.Check(NativeMethods.tn_load_ifc(Handle, path, parent?.Id ?? 0, out NativeImportResult result));
        return ImportResult.From(this, result);
    }

    /// <summary>Imports a Collada <c>.dae</c> file.</summary>
    /// <remarks>
    /// Collada describes geometry indirectly - a source of numbers, an accessor
    /// saying how wide a stride they come in, and one index per input per
    /// corner - and the loader resolves all of that, welding corners that agree
    /// on every index so a cube is eight vertices rather than thirty six.
    /// Materials and the node hierarchy are not read, so the file flattens to
    /// one node per geometry.
    /// </remarks>
    public ImportResult LoadCollada(string path, Node? parent = null)
    {
        NativeError.Check(NativeMethods.tn_load_collada(Handle, path, parent?.Id ?? 0, out NativeImportResult result));
        return ImportResult.From(this, result);
    }

    /// <summary>Imports a Google Earth <c>.kmz</c> file.</summary>
    /// <remarks>
    /// A KMZ is a zip holding a Collada document, so this unwraps the archive
    /// and hands the model to the Collada loader. The placemark and overlay
    /// parts of KML are not read; what comes out is the geometry.
    /// </remarks>
    public ImportResult LoadKmz(string path, Node? parent = null)
    {
        NativeError.Check(NativeMethods.tn_load_kmz(Handle, path, parent?.Id ?? 0, out NativeImportResult result));
        return ImportResult.From(this, result);
    }

    /// <summary>Imports a 3D Manufacturing Format <c>.3mf</c> file.</summary>
    /// <remarks>
    /// A zip holding XML that lists vertices and triangles directly, with none
    /// of Collada's indirection. Normals are computed, because a printed solid
    /// carries none. Triangles pointing past the vertex list are dropped.
    /// </remarks>
    public ImportResult Load3mf(string path, Node? parent = null)
    {
        NativeError.Check(NativeMethods.tn_load_3mf(Handle, path, parent?.Id ?? 0, out NativeImportResult result));
        return ImportResult.From(this, result);
    }

    /// <summary>Imports a VRML 2.0 <c>.wrl</c> file.</summary>
    /// <remarks>
    /// VRML is its own curly brace syntax rather than XML. Only
    /// <c>IndexedFaceSet</c> is read - the coordinates and the face list -
    /// because the rest of the language is sensors, scripts and interpolators,
    /// which want a browser and not a loader.
    /// </remarks>
    public ImportResult LoadVrml(string path, Node? parent = null)
    {
        NativeError.Check(NativeMethods.tn_load_vrml(Handle, path, parent?.Id ?? 0, out NativeImportResult result));
        return ImportResult.From(this, result);
    }

    /// <summary>
    /// Imports an <c>.svg</c> file, extruding every outline it can flatten into
    /// a solid <paramref name="depth"/> deep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An SVG is a drawing rather than a model, so extruding is the step that
    /// makes it one: a logo becomes a solid. Paths, polygons and rectangles are
    /// read; curves are flattened into segments because the triangulator works
    /// on polygons.
    /// </para>
    /// <para>
    /// Arcs are not flattened, and a path using one is skipped rather than
    /// closed with a straight line through where the curve should be. If every
    /// path in a file is an arc the import reports that rather than returning
    /// an empty model. SVG measures y down the page, so outlines are flipped on
    /// the way in.
    /// </para>
    /// </remarks>
    public ImportResult LoadSvg(string path, Node? parent = null, float depth = 1f)
    {
        NativeError.Check(NativeMethods.tn_load_svg(Handle, path, parent?.Id ?? 0, depth, out NativeImportResult result));
        return ImportResult.From(this, result);
    }

    /// <summary>Imports an Autodesk <c>.3ds</c> file.</summary>
    /// <remarks>
    /// <para>
    /// Each object in the file becomes a mesh under one root node. The format
    /// carries no normals, so they are computed from the faces, and it is Z up,
    /// so the geometry is turned Y up on the way in.
    /// </para>
    /// <para>
    /// Materials are not read; every mesh gets a default one. The format's own
    /// limits apply: vertex and face counts are 16 bit, so no single mesh can
    /// hold more than 65535 of either. Faces pointing past the end of the
    /// vertex list are dropped rather than trusted.
    /// </para>
    /// </remarks>
    public ImportResult Load3ds(string path, Node? parent = null)
    {
        NativeError.Check(NativeMethods.tn_load_3ds(Handle, path, parent?.Id ?? 0, out NativeImportResult result));
        return ImportResult.From(this, result);
    }

    /// <summary>Imports a MagicaVoxel <c>.vox</c> file.</summary>
    /// <remarks>
    /// <para>
    /// Only the visible shell is built: a voxel face with another voxel against
    /// it can never be seen, so a solid 32 cube becomes a few thousand
    /// triangles instead of nearly two hundred thousand.
    /// </para>
    /// <para>
    /// The palette becomes a 256 by 1 texture and every voxel points at it
    /// through its UV, so a model of any number of colours is one mesh with one
    /// material rather than one mesh per colour. Voxel space is Z up and the
    /// mesh is built Y up, so models stand the right way round.
    /// </para>
    /// </remarks>
    public ImportResult LoadVox(string path, Node? parent = null)
    {
        NativeError.Check(NativeMethods.tn_load_vox(Handle, path, parent?.Id ?? 0, out NativeImportResult result));
        return ImportResult.From(this, result);
    }

    /// <summary>Imports a PLY file, ASCII or binary, either endianness.</summary>
    /// <remarks>
    /// PLY describes its own layout in the header, so properties are read in
    /// whatever order and spelling a given exporter chose, and elements the
    /// reader has no use for are stepped over rather than ending the parse.
    /// Normals are used when the file has them and computed from the faces when
    /// it does not. A file with no faces loads as a point cloud - vertices and
    /// no indices - which is what a scanner usually produces.
    /// </remarks>
    public ImportResult LoadPly(string path, Node? parent = null)
    {
        NativeError.Check(NativeMethods.tn_load_ply(Handle, path, parent?.Id ?? 0, out NativeImportResult result));
        return ImportResult.From(this, result);
    }

    // ----------------------------------------------------------- raycasting

    /// <summary>Casts a ray through the scene and returns the hits, nearest first.</summary>
    public unsafe IReadOnlyList<RayHit> Raycast(Ray ray, RaycastOptions? options = null, int maxHits = 32)
    {
        NativeRaycastOptions native = (options ?? RaycastOptions.Default).ToNative();
        NativeRayHit* buffer = stackalloc NativeRayHit[maxHits];
        int count = NativeMethods.tn_raycast(Handle, ray.Origin, ray.Direction, in native, buffer, (uint)maxHits);
        NativeError.Check(count);

        RayHit[] hits = new RayHit[count];
        for (int i = 0; i < count; i++)
        {
            hits[i] = new RayHit(
                new Node(this, buffer[i].Node),
                buffer[i].Distance,
                buffer[i].Point,
                buffer[i].Normal,
                (int)buffer[i].Triangle,
                buffer[i].Barycentric,
                (int)buffer[i].Instance);
        }

        return hits;
    }

    /// <summary>
    /// Builds a picking ray from a camera node and a normalised device
    /// coordinate (-1..1, Y up).
    /// </summary>
    public Ray CreateCameraRay(Node cameraNode, float ndcX, float ndcY, float aspectRatio)
    {
        NativeError.Check(NativeMethods.tn_camera_ray(
            Handle, cameraNode.Id, ndcX, ndcY, aspectRatio, out Vector3 origin, out Vector3 direction));
        return new Ray(origin, direction);
    }

    public void Dispose()
    {
        if (_handle == nint.Zero)
        {
            return;
        }

        NativeMethods.tn_scene_destroy(_handle);
        _handle = nint.Zero;
        GC.SuppressFinalize(this);
    }

    ~Scene() => Dispose();
}
