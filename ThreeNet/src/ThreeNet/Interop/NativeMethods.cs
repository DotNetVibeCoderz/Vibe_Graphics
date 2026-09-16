using System.Numerics;
using System.Runtime.InteropServices;

namespace ThreeNet.Interop;

/// <summary>
/// One to one bindings for the <c>threenet_core</c> C ABI. Everything here is
/// internal; the public API lives in the <c>ThreeNet</c> namespace.
/// </summary>
internal static unsafe partial class NativeMethods
{
    private const string Library = global::ThreeNet.Native.NativeRuntimeInfo.LibraryName;

    static NativeMethods() => NativeLibraryResolver.Register();

    // ------------------------------------------------------------- library

    [LibraryImport(Library)]
    internal static partial uint tn_abi_version();

    [LibraryImport(Library)]
    internal static partial int tn_version(byte* buffer, int capacity);

    [LibraryImport(Library)]
    internal static partial void tn_init_logging(uint level);

    [LibraryImport(Library)]
    internal static partial int tn_last_error_message(byte* buffer, int capacity);

    // --------------------------------------------------------------- scene

    [LibraryImport(Library)]
    internal static partial nint tn_scene_create();

    [LibraryImport(Library)]
    internal static partial void tn_scene_destroy(nint scene);

    [LibraryImport(Library)]
    internal static partial uint tn_scene_root(nint scene);

    [LibraryImport(Library)]
    internal static partial uint tn_scene_node_count(nint scene);

    [LibraryImport(Library)]
    internal static partial uint tn_scene_create_node(nint scene, uint parent);

    [LibraryImport(Library)]
    internal static partial int tn_scene_remove_node(nint scene, uint node);

    [LibraryImport(Library)]
    internal static partial int tn_scene_set_parent(nint scene, uint node, uint parent);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial uint tn_scene_find_by_name(nint scene, string name);

    [LibraryImport(Library)]
    internal static partial int tn_scene_set_active_camera(nint scene, uint node);

    [LibraryImport(Library)]
    internal static partial int tn_scene_set_environment(nint scene, in NativeEnvironmentDesc desc);

    [LibraryImport(Library)]
    internal static partial int tn_scene_compute_bounds(nint scene, uint node, out Vector3 min, out Vector3 max);

    // ---------------------------------------------------------------- node

    [LibraryImport(Library)]
    internal static partial int tn_node_set_transform(nint scene, uint node, in NativeTransform transform);

    [LibraryImport(Library)]
    internal static partial int tn_node_get_transform(nint scene, uint node, out NativeTransform transform);

    [LibraryImport(Library)]
    internal static partial int tn_node_set_position(nint scene, uint node, Vector3 position);

    [LibraryImport(Library)]
    internal static partial int tn_node_set_rotation(nint scene, uint node, Vector4 rotation);

    [LibraryImport(Library)]
    internal static partial int tn_node_set_euler_angles(nint scene, uint node, Vector3 angles);

    [LibraryImport(Library)]
    internal static partial int tn_node_get_euler_angles(nint scene, uint node, out Vector3 angles);

    [LibraryImport(Library)]
    internal static partial int tn_node_set_scale(nint scene, uint node, Vector3 scale);

    [LibraryImport(Library)]
    internal static partial int tn_node_look_at(nint scene, uint node, Vector3 target, Vector3 up);

    [LibraryImport(Library)]
    internal static partial int tn_node_set_visible(nint scene, uint node, int visible);

    [LibraryImport(Library)]
    internal static partial int tn_node_set_layers(nint scene, uint node, uint layers);

    [LibraryImport(Library)]
    internal static partial int tn_node_set_user_data(nint scene, uint node, ulong userData);

    [LibraryImport(Library)]
    internal static partial int tn_node_get_user_data(nint scene, uint node, out ulong userData);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int tn_node_set_name(nint scene, uint node, string name);

    [LibraryImport(Library)]
    internal static partial int tn_node_get_name(nint scene, uint node, byte* buffer, int capacity);

    [LibraryImport(Library)]
    internal static partial int tn_node_get_world_matrix(nint scene, uint node, float* matrix);

    [LibraryImport(Library)]
    internal static partial uint tn_node_get_parent(nint scene, uint node);

    [LibraryImport(Library)]
    internal static partial uint tn_node_get_child_count(nint scene, uint node);

    [LibraryImport(Library)]
    internal static partial uint tn_node_get_child(nint scene, uint node, uint index);

    [LibraryImport(Library)]
    internal static partial int tn_node_attach_mesh(nint scene, uint node, uint geometry, uint material);

    [LibraryImport(Library)]
    internal static partial int tn_node_detach_mesh(nint scene, uint node);

    [LibraryImport(Library)]
    internal static partial uint tn_node_clone(nint scene, uint node, uint parent);

    [LibraryImport(Library)]
    internal static partial int tn_node_set_shadow_flags(nint scene, uint node, int castShadow, int receiveShadow);

    [LibraryImport(Library)]
    internal static partial int tn_node_get_shadow_flags(nint scene, uint node, out uint flags);

    [LibraryImport(Library)]
    internal static partial int tn_node_set_light(nint scene, uint node, in NativeLightDesc desc);

    [LibraryImport(Library)]
    internal static partial int tn_node_get_light(nint scene, uint node, out NativeLightDesc desc);

    [LibraryImport(Library)]
    internal static partial int tn_node_clear_light(nint scene, uint node);

    [LibraryImport(Library)]
    internal static partial int tn_node_set_camera(nint scene, uint node, in NativeCameraDesc desc);

    [LibraryImport(Library)]
    internal static partial int tn_node_get_camera(nint scene, uint node, out NativeCameraDesc desc);

    [LibraryImport(Library)]
    internal static partial int tn_node_clear_camera(nint scene, uint node);

    // ------------------------------------------------------------ geometry

    [LibraryImport(Library)]
    internal static partial uint tn_geometry_create(
        nint scene, Vertex* vertices, uint vertexCount, uint* indices, uint indexCount, uint topology);

    [LibraryImport(Library)]
    internal static partial int tn_geometry_update(
        nint scene, uint geometry, Vertex* vertices, uint vertexCount, uint* indices, uint indexCount);

    [LibraryImport(Library)]
    internal static partial int tn_geometry_destroy(nint scene, uint geometry);

    [LibraryImport(Library)]
    internal static partial int tn_geometry_compute_normals(nint scene, uint geometry);

    [LibraryImport(Library)]
    internal static partial int tn_geometry_compute_tangents(nint scene, uint geometry);

    [LibraryImport(Library)]
    internal static partial int tn_geometry_get_counts(nint scene, uint geometry, out uint vertexCount, out uint indexCount);

    [LibraryImport(Library)]
    internal static partial uint tn_geometry_plane(nint scene, float width, float height, uint widthSegments, uint heightSegments);

    [LibraryImport(Library)]
    internal static partial uint tn_geometry_box(nint scene, float width, float height, float depth, uint segments);

    [LibraryImport(Library)]
    internal static partial uint tn_geometry_sphere(nint scene, float radius, uint widthSegments, uint heightSegments);

    [LibraryImport(Library)]
    internal static partial uint tn_geometry_cylinder(
        nint scene, float radiusTop, float radiusBottom, float height, uint radialSegments, uint heightSegments, int capped);

    [LibraryImport(Library)]
    internal static partial uint tn_geometry_cone(nint scene, float radius, float height, uint radialSegments);

    [LibraryImport(Library)]
    internal static partial uint tn_geometry_torus(
        nint scene, float radius, float tube, uint radialSegments, uint tubularSegments, float arc);

    [LibraryImport(Library)]
    internal static partial uint tn_geometry_grid(nint scene, float size, uint divisions);

    // ------------------------------------------------------------ material

    [LibraryImport(Library)]
    internal static partial uint tn_material_create(nint scene, in NativeMaterialDesc desc);

    [LibraryImport(Library)]
    internal static partial int tn_material_update(nint scene, uint material, in NativeMaterialDesc desc);

    [LibraryImport(Library)]
    internal static partial int tn_material_get(nint scene, uint material, out NativeMaterialDesc desc);

    [LibraryImport(Library)]
    internal static partial int tn_material_destroy(nint scene, uint material);

    // ------------------------------------------------------------- texture

    [LibraryImport(Library)]
    internal static partial uint tn_texture_create(nint scene, uint width, uint height, uint format, byte* pixels, uint length);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial uint tn_texture_load_file(nint scene, string path, int srgb);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial uint tn_shader_create(nint scene, uint language, string source, string? name);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int tn_shader_update(nint scene, uint shader, uint language, string source);

    [LibraryImport(Library)]
    internal static partial int tn_shader_get_wgsl(nint scene, uint shader, byte* buffer, int capacity);

    [LibraryImport(Library)]
    internal static partial int tn_shader_destroy(nint scene, uint shader);

    [LibraryImport(Library)]
    internal static partial uint tn_texture_load_memory(nint scene, byte* bytes, uint length, int srgb);

    [LibraryImport(Library)]
    internal static partial int tn_texture_set_sampler(nint scene, uint texture, in NativeSamplerDesc desc);

    [LibraryImport(Library)]
    internal static partial int tn_texture_destroy(nint scene, uint texture);

    // ------------------------------------------------------------ renderer

    [LibraryImport(Library)]
    internal static partial nint tn_renderer_create_offscreen(in NativeRendererDesc desc);

    [LibraryImport(Library)]
    internal static partial nint tn_renderer_create_win32(nint hwnd, nint hinstance, in NativeRendererDesc desc);

    [LibraryImport(Library)]
    internal static partial nint tn_renderer_create_xlib(ulong window, nint display, int screen, in NativeRendererDesc desc);

    [LibraryImport(Library)]
    internal static partial nint tn_renderer_create_appkit(nint nsView, in NativeRendererDesc desc);

    [LibraryImport(Library)]
    internal static partial void tn_renderer_destroy(nint renderer);

    [LibraryImport(Library)]
    internal static partial int tn_renderer_resize(nint renderer, uint width, uint height);

    [LibraryImport(Library)]
    internal static partial int tn_renderer_set_config(nint renderer, in NativeRendererDesc desc);

    [LibraryImport(Library)]
    internal static partial int tn_renderer_render(nint renderer, nint scene, uint cameraNode);

    [LibraryImport(Library)]
    internal static partial int tn_renderer_read_pixels(nint renderer, byte* buffer, uint capacity);

    [LibraryImport(Library)]
    internal static partial int tn_renderer_get_stats(nint renderer, out NativeFrameStats stats);

    [LibraryImport(Library)]
    internal static partial int tn_renderer_get_adapter_name(nint renderer, byte* buffer, int capacity);

    // ------------------------------------------------------------- loaders

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int tn_load_gltf(nint scene, string path, uint parent, out NativeImportResult result);

    [LibraryImport(Library)]
    internal static partial int tn_load_gltf_memory(nint scene, byte* bytes, uint length, uint parent, out NativeImportResult result);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int tn_load_obj(nint scene, string path, uint parent, out NativeImportResult result);

    // ---------------------------------------------------------- raycasting

    [LibraryImport(Library)]
    internal static partial int tn_raycast(
        nint scene, Vector3 origin, Vector3 direction, in NativeRaycastOptions options, NativeRayHit* hits, uint maxHits);

    [LibraryImport(Library)]
    internal static partial int tn_camera_ray(
        nint scene, uint cameraNode, float ndcX, float ndcY, float aspect, out Vector3 origin, out Vector3 direction);

    // ----------------------------------------------------------- windowing

    [LibraryImport(Library)]
    internal static partial int tn_app_run(NativeWindowDesc* desc, NativeAppCallbacks callbacks);
}
