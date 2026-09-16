using ThreeNet.Interop;

namespace ThreeNet;

/// <summary>Source language of a custom shader.</summary>
public enum ShaderLanguage : uint
{
    Wgsl = 0,
    /// <summary>GLSL 4.50 hook functions, translated to WGSL with naga.</summary>
    Glsl = 1,
}

/// <summary>
/// Custom shading injected into the built-in pipelines. The source defines one or both hooks:
/// <code>
/// // WGSL
/// fn user_vertex(context: VertexContext) -> vec3&lt;f32&gt;                     // object space position
/// fn user_surface(context: SurfaceContext, surface: Surface) -> Surface
/// // GLSL
/// vec3 user_vertex(VertexContext context);
/// Surface user_surface(SurfaceContext context, Surface surface);
/// </code>
/// <c>Surface</c> carries albedo, alpha, normal, metallic, emissive, roughness, specular,
/// occlusion, shininess, reflectance, shading_model and receive_shadow.
/// <c>SurfaceContext</c> gives world_position, world_normal, view_direction, uv, screen_uv,
/// time, custom0 and custom1; <c>VertexContext</c> gives position, normal, uv, time,
/// custom0 and custom1. The custom vectors come from <see cref="MaterialOptions.Custom0"/>
/// and <see cref="MaterialOptions.Custom1"/>. Shaders work in both the forward and the
/// deferred renderer, and are validated on creation.
/// </summary>
public sealed class Shader : IEquatable<Shader>
{
    internal Shader(Scene scene, uint id)
    {
        Scene = scene;
        Id = id;
    }

    public Scene Scene { get; }

    public uint Id { get; }

    /// <summary>The WGSL hooks the shader compiled to (the translation, for GLSL sources).</summary>
    public unsafe string CompiledWgsl => NativeError.ReadString((buffer, capacity) =>
        NativeMethods.tn_shader_get_wgsl(Scene.Handle, Id, (byte*)buffer, capacity));

    /// <summary>Replaces the source; on a compile error the previous version stays active.</summary>
    public void Update(string source, ShaderLanguage language = ShaderLanguage.Wgsl)
    {
        ArgumentNullException.ThrowIfNull(source);
        NativeError.Check(NativeMethods.tn_shader_update(Scene.Handle, Id, (uint)language, source));
    }

    public void Destroy() => NativeMethods.tn_shader_destroy(Scene.Handle, Id);

    public bool Equals(Shader? other) => other is not null && Id == other.Id && ReferenceEquals(Scene, other.Scene);

    public override bool Equals(object? obj) => Equals(obj as Shader);

    public override int GetHashCode() => HashCode.Combine(Scene, Id);
}
