using System.Numerics;
using ThreeNet.Interop;

namespace ThreeNet;

/// <summary>Handle to a geometry owned by a <see cref="Scene"/>.</summary>
public sealed class Geometry : IEquatable<Geometry>
{
    internal Geometry(Scene scene, uint id)
    {
        Scene = scene;
        Id = id;
    }

    public Scene Scene { get; }

    public uint Id { get; }

    public bool IsNull => Id == 0;

    /// <summary>Number of vertices and indices currently stored.</summary>
    /// <summary>
    /// Reorders this mesh for the GPU without changing what it draws.
    /// </summary>
    /// <remarks>
    /// Indices are reordered so triangles sharing vertices are drawn near each
    /// other and the post-transform cache keeps hitting, then the vertices are
    /// reordered to match, so fetching them walks memory forwards. Same
    /// triangles, same picture. Vertices no index refers to are dropped, so the
    /// buffer can come back slightly smaller.
    /// </remarks>
    public void Optimize() => NativeError.Check(NativeMethods.tn_geometry_optimize(Scene.Handle, Id));

    /// <summary>
    /// Collapses this mesh towards <paramref name="ratio"/> of its triangles,
    /// holding the silhouette. The original is left alone.
    /// </summary>
    /// <param name="ratio">The fraction of triangles to keep, 0 to 1.</param>
    /// <param name="errorLimit">
    /// How far the result may stray, as a fraction of the mesh size. 0.01 is a
    /// tight budget, 0.1 a loose one; simplification stops at whichever limit
    /// it reaches first.
    /// </param>
    /// <remarks>
    /// This is how the levels for <see cref="Node.SetLevelsOfDetail"/> get made
    /// without modelling them by hand. The ratio is a target rather than a
    /// promise: a mesh with nothing redundant in it comes back larger than
    /// asked for, because the alternative is to destroy its shape. The returned
    /// counts say what actually happened.
    /// </remarks>
    public (Geometry Simplified, int TrianglesBefore, int TrianglesAfter, float Error) Simplify(
        float ratio,
        float errorLimit = 0.01f)
    {
        NativeError.Check(NativeMethods.tn_geometry_simplify(
            Scene.Handle, Id, ratio, errorLimit, out uint simplified, out NativeSimplifyResult result));
        return (
            new Geometry(Scene, simplified),
            (int)result.TrianglesBefore,
            (int)result.TrianglesAfter,
            result.Error);
    }

    public (int Vertices, int Indices) Counts
    {
        get
        {
            NativeError.Check(NativeMethods.tn_geometry_get_counts(Scene.Handle, Id, out uint vertices, out uint indices));
            return ((int)vertices, (int)indices);
        }
    }

    /// <summary>Replaces the vertex and index data; the renderer re-uploads it.</summary>
    public unsafe void Update(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices = default)
    {
        fixed (Vertex* vertexPointer = vertices)
        fixed (uint* indexPointer = indices)
        {
            NativeError.Check(NativeMethods.tn_geometry_update(
                Scene.Handle, Id, vertexPointer, (uint)vertices.Length, indexPointer, (uint)indices.Length));
        }
    }

    /// <summary>
    /// Blend shapes on this geometry. Imported models bring their own; see
    /// <see cref="AddMorphTarget"/> to build one in code.
    /// </summary>
    public int MorphTargetCount
    {
        get
        {
            int count = NativeMethods.tn_geometry_morph_target_count(Scene.Handle, Id);
            NativeError.Check(count);
            return count;
        }
    }

    /// <summary>Name of one blend shape, as the asset named it.</summary>
    public unsafe string GetMorphTargetName(int index) => NativeError.ReadString((buffer, capacity) =>
        NativeMethods.tn_geometry_morph_target_name(Scene.Handle, Id, (uint)index, (byte*)buffer, capacity));

    /// <summary>Every blend shape name, in weight order.</summary>
    public IReadOnlyList<string> MorphTargetNames
    {
        get
        {
            int count = MorphTargetCount;
            string[] names = new string[count];
            for (int i = 0; i < count; i++)
            {
                names[i] = GetMorphTargetName(i);
            }

            return names;
        }
    }

    /// <summary>
    /// Adds a blend shape built in code: one position delta per vertex, and
    /// optionally one normal delta. Returns the index of the new target, which
    /// is the index its weight has on a node.
    /// </summary>
    /// <remarks>
    /// The geometry's vertices at the time of the first call become the rest
    /// pose every weight of zero returns to.
    /// </remarks>
    public unsafe int AddMorphTarget(string name, ReadOnlySpan<Vector3> positionDeltas, ReadOnlySpan<Vector3> normalDeltas = default)
    {
        if (!normalDeltas.IsEmpty && normalDeltas.Length != positionDeltas.Length)
        {
            throw new ArgumentException(
                "a morph target needs as many normal deltas as position deltas, or none",
                nameof(normalDeltas));
        }

        fixed (Vector3* positions = positionDeltas)
        fixed (Vector3* normals = normalDeltas)
        {
            int index = NativeMethods.tn_geometry_add_morph_target(
                Scene.Handle, Id, name, positions, normalDeltas.IsEmpty ? null : normals, (uint)positionDeltas.Length);
            NativeError.Check(index);
            return index;
        }
    }

    /// <summary>Recomputes smooth vertex normals by area weighted averaging.</summary>
    public void ComputeNormals() => NativeError.Check(NativeMethods.tn_geometry_compute_normals(Scene.Handle, Id));

    /// <summary>Recomputes tangents from the UV parameterisation.</summary>
    public void ComputeTangents() => NativeError.Check(NativeMethods.tn_geometry_compute_tangents(Scene.Handle, Id));

    /// <summary>Destroys the geometry. Nodes still referencing it stop rendering.</summary>
    public void Destroy() => NativeMethods.tn_geometry_destroy(Scene.Handle, Id);

    public bool Equals(Geometry? other) => other is not null && Id == other.Id && ReferenceEquals(Scene, other.Scene);

    public override bool Equals(object? obj) => Equals(obj as Geometry);

    public override int GetHashCode() => HashCode.Combine(Scene, Id);

    public static bool operator ==(Geometry? left, Geometry? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(Geometry? left, Geometry? right) => !(left == right);
}

/// <summary>Handle to a material owned by a <see cref="Scene"/>.</summary>
public sealed class Material : IEquatable<Material>
{
    internal Material(Scene scene, uint id)
    {
        Scene = scene;
        Id = id;
    }

    public Scene Scene { get; }

    public uint Id { get; }

    public bool IsNull => Id == 0;

    /// <summary>Reads the current material settings.</summary>
    public MaterialOptions Options
    {
        get
        {
            NativeError.Check(NativeMethods.tn_material_get(Scene.Handle, Id, out NativeMaterialDesc desc));
            return MaterialOptions.FromNative(Scene, desc);
        }
        set
        {
            NativeMaterialDesc desc = value.ToNative();
            NativeError.Check(NativeMethods.tn_material_update(Scene.Handle, Id, in desc));
        }
    }

    /// <summary>Applies a partial change without rewriting every field by hand.</summary>
    public void Update(Func<MaterialOptions, MaterialOptions> change) => Options = change(Options);

    /// <summary>Convenience setter for the most common property.</summary>
    public Vector4 BaseColor
    {
        get => Options.BaseColor;
        set
        {
            MaterialOptions options = Options;
            options.BaseColor = value;
            Options = options;
        }
    }

    /// <summary>Destroys the material; nodes using it stop rendering.</summary>
    public void Destroy() => NativeMethods.tn_material_destroy(Scene.Handle, Id);

    public bool Equals(Material? other) => other is not null && Id == other.Id && ReferenceEquals(Scene, other.Scene);

    public override bool Equals(object? obj) => Equals(obj as Material);

    public override int GetHashCode() => HashCode.Combine(Scene, Id);

    public static bool operator ==(Material? left, Material? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(Material? left, Material? right) => !(left == right);
}

/// <summary>Handle to a texture owned by a <see cref="Scene"/>.</summary>
public sealed class Texture : IEquatable<Texture>
{
    internal Texture(Scene scene, uint id)
    {
        Scene = scene;
        Id = id;
    }

    public Scene Scene { get; }

    public uint Id { get; }

    public bool IsNull => Id == 0;

    /// <summary>Changes filtering, wrapping and mip settings.</summary>
    public void SetSampler(
        WrapMode wrapU = WrapMode.Repeat,
        WrapMode wrapV = WrapMode.Repeat,
        bool linearFilter = true,
        bool mipmaps = true,
        int anisotropy = 8)
    {
        NativeSamplerDesc desc = new()
        {
            WrapU = (uint)wrapU,
            WrapV = (uint)wrapV,
            LinearFilter = linearFilter ? 1 : 0,
            Mipmaps = mipmaps ? 1 : 0,
            Anisotropy = (uint)Math.Clamp(anisotropy, 1, 16),
        };
        NativeError.Check(NativeMethods.tn_texture_set_sampler(Scene.Handle, Id, in desc));
    }

    /// <summary>
    /// Replaces the pixels, keeping the size and format. The GPU texture is
    /// written in place, so a height field or a flow map can be refreshed every
    /// frame without reallocating anything.
    /// </summary>
    public unsafe void Update(ReadOnlySpan<byte> pixels)
    {
        fixed (byte* data = pixels)
        {
            NativeError.Check(NativeMethods.tn_texture_update(Scene.Handle, Id, data, (uint)pixels.Length));
        }
    }

    /// <summary>Replaces the pixels of a <see cref="TextureFormat.Rgba32Float"/> texture.</summary>
    public unsafe void Update(ReadOnlySpan<float> pixels) =>
        Update(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels));

    /// <summary>Destroys the texture and frees its GPU memory.</summary>
    public void Destroy() => NativeMethods.tn_texture_destroy(Scene.Handle, Id);

    /// <summary>Streaming state; textures not loaded with <see cref="Scene.LoadTextureAsync"/> are <see cref="TextureState.Ready"/>.</summary>
    public TextureState State => (TextureState)NativeMethods.tn_texture_get_state(Scene.Handle, Id);

    /// <summary>Why a streamed texture failed to load, or <see langword="null"/>.</summary>
    public unsafe string? LoadError
    {
        get
        {
            string message = NativeError.ReadString((buffer, capacity) =>
                NativeMethods.tn_texture_get_error(Scene.Handle, Id, (byte*)buffer, capacity));
            return message.Length == 0 ? null : message;
        }
    }

    public bool Equals(Texture? other) => other is not null && Id == other.Id && ReferenceEquals(Scene, other.Scene);

    public override bool Equals(object? obj) => Equals(obj as Texture);

    public override int GetHashCode() => HashCode.Combine(Scene, Id);

    public static bool operator ==(Texture? left, Texture? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(Texture? left, Texture? right) => !(left == right);
}

/// <summary>Everything a material controls. Mirrors the native descriptor.</summary>
public struct MaterialOptions
{
    public ShadingModel Shading;
    public AlphaMode AlphaMode;
    public CullMode CullMode;
    /// <summary>Linear RGBA albedo / diffuse colour.</summary>
    public Vector4 BaseColor;
    public Vector3 Emissive;
    public float EmissiveIntensity;
    public float Metallic;
    public float Roughness;
    /// <summary>Blinn-Phong specular colour, ignored by the PBR model.</summary>
    public Vector3 Specular;
    public float Shininess;
    public float Reflectance;
    /// <summary>
    /// A thin glossy layer over the whole surface: car paint, lacquer, a
    /// varnished floor. 0 is off. What the coat reflects it also takes away
    /// from the layer underneath, so a strong coat dulls the colour below it.
    /// </summary>
    /// <remarks>
    /// Forward path only. The deferred G-buffer has five targets and no room
    /// for a sixth, so a deferred frame ignores this.
    /// </remarks>
    public float Clearcoat;
    /// <summary>How sharp the clearcoat reflection is; low is a mirror finish.</summary>
    public float ClearcoatRoughness;
    /// <summary>
    /// Retroreflective rim for cloth - velvet, satin, brushed wool. Black is
    /// off. Unlike the ordinary specular lobe it peaks at grazing angles, which
    /// is why velvet lights up around its edge rather than in the middle.
    /// </summary>
    /// <remarks>Forward path only, for the same reason as <see cref="Clearcoat"/>.</remarks>
    public Vector3 Sheen;
    /// <summary>How wide the sheen rim is.</summary>
    public float SheenRoughness;
    /// <summary>
    /// How much light passes through the surface rather than reflecting off it:
    /// glass, water, a gemstone. 0 is opaque, 1 is clear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A transmissive surface samples what the opaque pass left behind, bent by
    /// <see cref="Ior"/> and tinted by <see cref="Thickness"/>. It replaces the
    /// diffuse rather than adding to it: light that passed through did not
    /// scatter back.
    /// </para>
    /// <para>
    /// Forward path only, and glass does not refract other glass - both halves
    /// of the scene pass draw after the colour was copied aside, so only the
    /// opaque result is there to sample. Three.js has the same limitation.
    /// </para>
    /// </remarks>
    public float Transmission;
    /// <summary>Index of refraction: 1.0 air, 1.33 water, 1.5 window glass, 2.4 diamond.</summary>
    public float Ior;
    /// <summary>How thick the glass is, in metres. Thicker bends further and absorbs more.</summary>
    public float Thickness;
    /// <summary>
    /// A film thin enough to interfere with itself, laid over the surface: a
    /// soap bubble, an oil slick on a puddle, anodised titanium, the inside of
    /// a shell. 0 is off, 1 is the full effect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hue is not a colour the material has. Light bouncing off the top of
    /// the film and light bouncing off the surface underneath travel different
    /// distances, so some wavelengths cancel and others reinforce - and which
    /// ones do depends on the angle you look from, which is why the colour
    /// slides as the object turns. <see cref="IridescenceThickness"/> chooses
    /// the set of bands and <see cref="IridescenceIor"/> how strong they are.
    /// </para>
    /// <para>
    /// Forward path only, like the other extended lobes. The evaluation is the
    /// Belcour-Barla model that KHR_materials_iridescence specifies, so a glTF
    /// authored for Three.js or Blender looks the same here.
    /// </para>
    /// </remarks>
    public float Iridescence;
    /// <summary>
    /// Index of refraction of the film itself; 1.3 is a soap bubble. The
    /// further it sits from the air around it, the stronger the colours.
    /// </summary>
    public float IridescenceIor;
    /// <summary>
    /// How thick the film is, in nanometres - the scale visible light works
    /// at, so this is what the hue is a function of. Around 100 is a pale
    /// gold; 400 runs through a full set of bands.
    /// </summary>
    public float IridescenceThickness;
    /// <summary>
    /// Stretches the highlight along the surface grain: brushed metal, hair, a
    /// vinyl record. 0 is off, 1 stretches along the tangent and -1 across it.
    /// The mean roughness is held, so turning this up streaks the highlight
    /// without also making the surface look rougher.
    /// </summary>
    /// <remarks>
    /// Needs tangents on the geometry (<see cref="Geometry.ComputeTangents"/>,
    /// which every built-in generator already calls). Forward path only.
    /// </remarks>
    public float Anisotropy;
    /// <summary>Turns the grain within the tangent plane, in radians.</summary>
    public float AnisotropyRotation;
    public float NormalScale;
    public float OcclusionStrength;
    public float AlphaCutoff;
    public Vector2 UvScale;
    public Vector2 UvOffset;
    public bool DepthWrite;
    public bool DepthTest;
    public bool Wireframe;
    /// <summary>Draw order override for transparent surfaces (higher draws later).</summary>
    public int RenderOrder;
    public Texture? BaseColorMap;
    public Texture? NormalMap;
    /// <summary>Green channel = roughness, blue channel = metallic (glTF layout).</summary>
    public Texture? MetallicRoughnessMap;
    public Texture? EmissiveMap;
    public Texture? OcclusionMap;
    /// <summary>Custom shader hooks; null uses the built-in shading.</summary>
    public Shader? Shader;
    /// <summary>Free parameters readable by custom shaders as <c>custom0</c>.</summary>
    public Vector4 Custom0;
    /// <summary>Free parameters readable by custom shaders as <c>custom1</c>.</summary>
    public Vector4 Custom1;
    /// <summary>Free parameters readable by custom shaders as <c>custom2</c>.</summary>
    public Vector4 Custom2;
    /// <summary>Free parameters readable by custom shaders as <c>custom3</c>.</summary>
    public Vector4 Custom3;
    /// <summary>
    /// A texture the built-in shading never reads, sampled by a custom shader as
    /// <c>custom_texture</c>: a wave height field, a flow map, a gradient ramp.
    /// </summary>
    public Texture? CustomMap;

    /// <summary>Sensible physically based defaults.</summary>
    public static MaterialOptions Default => new();

    public MaterialOptions()
    {
        Shading = ShadingModel.Pbr;
        AlphaMode = AlphaMode.Opaque;
        CullMode = CullMode.Back;
        BaseColor = Vector4.One;
        Emissive = Vector3.Zero;
        EmissiveIntensity = 1f;
        Metallic = 0f;
        Roughness = 0.5f;
        Specular = new Vector3(0.04f);
        Shininess = 32f;
        Reflectance = 0.5f;
        Clearcoat = 0f;
        ClearcoatRoughness = 0.1f;
        Sheen = Vector3.Zero;
        SheenRoughness = 0.3f;
        Transmission = 0f;
        Ior = 1.5f;
        Thickness = 0f;
        Iridescence = 0f;
        IridescenceIor = 1.3f;
        IridescenceThickness = 400f;
        Anisotropy = 0f;
        AnisotropyRotation = 0f;
        NormalScale = 1f;
        OcclusionStrength = 1f;
        AlphaCutoff = 0.5f;
        UvScale = Vector2.One;
        UvOffset = Vector2.Zero;
        DepthWrite = true;
        DepthTest = true;
        Wireframe = false;
        RenderOrder = 0;
    }

    /// <summary>Physically based material.</summary>
    public static MaterialOptions Pbr(Vector4 baseColor, float metallic = 0f, float roughness = 0.5f) =>
        Default with { Shading = ShadingModel.Pbr, BaseColor = baseColor, Metallic = metallic, Roughness = roughness };

    /// <summary>Unlit material showing the base colour as is.</summary>
    public static MaterialOptions Basic(Vector4 baseColor) =>
        Default with { Shading = ShadingModel.Basic, BaseColor = baseColor };

    /// <summary>Classic Blinn-Phong material.</summary>
    public static MaterialOptions Phong(Vector4 baseColor, float shininess = 32f) =>
        Default with { Shading = ShadingModel.Phong, BaseColor = baseColor, Shininess = shininess };

    /// <summary>Lambert diffuse material.</summary>
    public static MaterialOptions Lambert(Vector4 baseColor) =>
        Default with { Shading = ShadingModel.Lambert, BaseColor = baseColor };

    /// <summary>Cel shaded material: diffuse quantised into <paramref name="bands"/> steps.</summary>
    public static MaterialOptions Toon(Vector4 baseColor, int bands = 4) =>
        Default with { Shading = ShadingModel.Toon, BaseColor = baseColor, Shininess = Math.Max(2, bands) };

    /// <summary>Unlit view depth, white near the camera and black at the far plane.</summary>
    public static MaterialOptions DepthPreview() => Default with { Shading = ShadingModel.Depth };

    /// <summary>Unlit world normal as colour.</summary>
    public static MaterialOptions NormalPreview() => Default with { Shading = ShadingModel.Normal };

    internal NativeMaterialDesc ToNative() => new()
    {
        Shading = (uint)Shading,
        AlphaMode = (uint)AlphaMode,
        CullMode = (uint)CullMode,
        BaseColor = BaseColor,
        Emissive = Emissive,
        EmissiveIntensity = EmissiveIntensity,
        Metallic = Metallic,
        Roughness = Roughness,
        Specular = Specular,
        Shininess = Shininess,
        Reflectance = Reflectance,
        Clearcoat = Clearcoat,
        ClearcoatRoughness = ClearcoatRoughness,
        Sheen = Sheen,
        SheenRoughness = SheenRoughness,
        Transmission = Transmission,
        Ior = Ior,
        Thickness = Thickness,
        Iridescence = Iridescence,
        IridescenceIor = IridescenceIor,
        IridescenceThickness = IridescenceThickness,
        Anisotropy = Anisotropy,
        AnisotropyRotation = AnisotropyRotation,
        NormalScale = NormalScale,
        OcclusionStrength = OcclusionStrength,
        AlphaCutoff = AlphaCutoff,
        UvScale = UvScale,
        UvOffset = UvOffset,
        DepthWrite = DepthWrite ? 1 : 0,
        DepthTest = DepthTest ? 1 : 0,
        Wireframe = Wireframe ? 1 : 0,
        RenderOrder = RenderOrder,
        BaseColorTexture = BaseColorMap?.Id ?? 0,
        NormalTexture = NormalMap?.Id ?? 0,
        MetallicRoughnessTexture = MetallicRoughnessMap?.Id ?? 0,
        EmissiveTexture = EmissiveMap?.Id ?? 0,
        OcclusionTexture = OcclusionMap?.Id ?? 0,
        CustomTexture = CustomMap?.Id ?? 0,
        Shader = Shader?.Id ?? 0,
        Custom0 = Custom0,
        Custom1 = Custom1,
        Custom2 = Custom2,
        Custom3 = Custom3,
    };

    internal static MaterialOptions FromNative(Scene scene, in NativeMaterialDesc desc)
    {
        // Texture slots come back as bare ids, so they are re-bound to the
        // owning scene here; otherwise a read-modify-write of the options would
        // silently drop every texture.
        Texture? Slot(uint id) => id == 0 ? null : new Texture(scene, id);

        return new MaterialOptions
    {
        Shading = (ShadingModel)desc.Shading,
        AlphaMode = (AlphaMode)desc.AlphaMode,
        CullMode = (CullMode)desc.CullMode,
        BaseColor = desc.BaseColor,
        Emissive = desc.Emissive,
        EmissiveIntensity = desc.EmissiveIntensity,
        Metallic = desc.Metallic,
        Roughness = desc.Roughness,
        Specular = desc.Specular,
        Shininess = desc.Shininess,
        Reflectance = desc.Reflectance,
        Clearcoat = desc.Clearcoat,
        ClearcoatRoughness = desc.ClearcoatRoughness,
        Sheen = desc.Sheen,
        SheenRoughness = desc.SheenRoughness,
        Transmission = desc.Transmission,
        Ior = desc.Ior,
        Thickness = desc.Thickness,
        Iridescence = desc.Iridescence,
        IridescenceIor = desc.IridescenceIor,
        IridescenceThickness = desc.IridescenceThickness,
        Anisotropy = desc.Anisotropy,
        AnisotropyRotation = desc.AnisotropyRotation,
        NormalScale = desc.NormalScale,
        OcclusionStrength = desc.OcclusionStrength,
        AlphaCutoff = desc.AlphaCutoff,
        UvScale = desc.UvScale,
        UvOffset = desc.UvOffset,
        DepthWrite = desc.DepthWrite != 0,
        DepthTest = desc.DepthTest != 0,
        Wireframe = desc.Wireframe != 0,
        RenderOrder = desc.RenderOrder,
        BaseColorMap = Slot(desc.BaseColorTexture),
        NormalMap = Slot(desc.NormalTexture),
        MetallicRoughnessMap = Slot(desc.MetallicRoughnessTexture),
        EmissiveMap = Slot(desc.EmissiveTexture),
        OcclusionMap = Slot(desc.OcclusionTexture),
        CustomMap = Slot(desc.CustomTexture),
        Shader = desc.Shader == 0 ? null : new Shader(scene, desc.Shader),
        Custom0 = desc.Custom0,
        Custom1 = desc.Custom1,
        Custom2 = desc.Custom2,
        Custom3 = desc.Custom3,
        };
    }
}

/// <summary>Loading state of a texture.</summary>
public enum TextureState
{
    /// <summary>The handle no longer refers to a texture.</summary>
    Missing = 0,
    /// <summary>A placeholder is shown while the image loads in the background.</summary>
    Loading = 1,
    Ready = 2,
    /// <summary>Loading failed; the placeholder stays (see <see cref="Texture.LoadError"/>).</summary>
    Failed = 3,
}

/// <summary>Texture streaming and asset cache counters.</summary>
public readonly record struct AssetStats(
    int PendingTextures,
    int CachedTextures,
    int CachedModels,
    long CacheHits,
    long CacheMisses,
    long StreamedTextures);

/// <summary>Result of importing an asset file.</summary>
public readonly record struct ImportResult(
    Node Root,
    int NodeCount,
    int GeometryCount,
    int MaterialCount,
    int TextureCount,
    int AnimationCount = 0,
    int SkinCount = 0)
{
    internal static ImportResult From(Scene scene, in NativeImportResult native) => new(
        new Node(scene, native.Root),
        (int)native.NodeCount,
        (int)native.GeometryCount,
        (int)native.MaterialCount,
        (int)native.TextureCount,
        (int)native.AnimationCount,
        (int)native.SkinCount);
}
