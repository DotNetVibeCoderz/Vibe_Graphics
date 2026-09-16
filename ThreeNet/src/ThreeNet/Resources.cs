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

    /// <summary>Destroys the texture and frees its GPU memory.</summary>
    public void Destroy() => NativeMethods.tn_texture_destroy(Scene.Handle, Id);

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
        Shader = Shader?.Id ?? 0,
        Custom0 = Custom0,
        Custom1 = Custom1,
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
        Shader = desc.Shader == 0 ? null : new Shader(scene, desc.Shader),
        Custom0 = desc.Custom0,
        Custom1 = desc.Custom1,
        };
    }
}

/// <summary>Result of importing an asset file.</summary>
public readonly record struct ImportResult(Node Root, int NodeCount, int GeometryCount, int MaterialCount, int TextureCount)
{
    internal static ImportResult From(Scene scene, in NativeImportResult native) => new(
        new Node(scene, native.Root),
        (int)native.NodeCount,
        (int)native.GeometryCount,
        (int)native.MaterialCount,
        (int)native.TextureCount);
}
