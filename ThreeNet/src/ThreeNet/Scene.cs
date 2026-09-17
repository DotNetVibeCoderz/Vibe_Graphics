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
    }

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

    /// <summary>Imports a model, picking the loader from the file extension (.gltf, .glb, .fbx, .obj).</summary>
    public ImportResult LoadModel(string path, Node? parent = null) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".gltf" or ".glb" => LoadGltf(path, parent),
            ".fbx" => LoadFbx(path, parent),
            ".obj" => LoadObj(path, parent),
            var extension => throw new NotSupportedException($"no importer for '{extension}' files"),
        };

    public ImportResult LoadObj(string path, Node? parent = null)
    {
        NativeError.Check(NativeMethods.tn_load_obj(Handle, path, parent?.Id ?? 0, out NativeImportResult result));
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
                buffer[i].Barycentric);
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
