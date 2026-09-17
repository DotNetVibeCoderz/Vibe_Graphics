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
    internal static partial int tn_scene_get_animations(nint scene, uint* ids, uint capacity);

    [LibraryImport(Library)]
    internal static partial int tn_animation_get_name(nint scene, uint clip, byte* buffer, int capacity);

    [LibraryImport(Library)]
    internal static partial int tn_animation_get_duration(nint scene, uint clip, out float duration);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial uint tn_animation_create(nint scene, string? name);

    [LibraryImport(Library)]
    internal static partial int tn_animation_add_channel(nint scene, uint clip, uint node, uint path, uint interpolation, float* times, uint keyCount, float* values, uint valueCount);

    [LibraryImport(Library)]
    internal static partial int tn_animation_destroy(nint scene, uint clip);

    [LibraryImport(Library)]
    internal static partial uint tn_animation_play(nint scene, uint clip, in NativePlayerDesc desc);

    [LibraryImport(Library)]
    internal static partial int tn_player_get(nint scene, uint player, out NativePlayerDesc desc);

    [LibraryImport(Library)]
    internal static partial int tn_player_set(nint scene, uint player, in NativePlayerDesc desc);

    [LibraryImport(Library)]
    internal static partial int tn_player_stop(nint scene, uint player);

    [LibraryImport(Library)]
    internal static partial int tn_scene_update_animations(nint scene, float delta);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int tn_load_fbx(nint scene, string path, uint parent, out NativeImportResult result);

    [LibraryImport(Library)]
    internal static partial int tn_load_fbx_memory(nint scene, byte* bytes, uint length, uint parent, out NativeImportResult result);

    [LibraryImport(Library)]
    internal static partial uint tn_texture_load_memory(nint scene, byte* bytes, uint length, int srgb);

    [LibraryImport(Library)]
    internal static partial int tn_texture_set_sampler(nint scene, uint texture, in NativeSamplerDesc desc);

    [LibraryImport(Library)]
    internal static partial int tn_texture_destroy(nint scene, uint texture);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial uint tn_texture_load_async(nint scene, string path, int srgb);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial uint tn_texture_load_cached(nint scene, string path, int srgb);

    [LibraryImport(Library)]
    internal static partial int tn_texture_get_state(nint scene, uint texture);

    [LibraryImport(Library)]
    internal static partial int tn_texture_get_error(nint scene, uint texture, byte* buffer, int capacity);

    [LibraryImport(Library)]
    internal static partial int tn_scene_poll_streaming(nint scene);

    [LibraryImport(Library)]
    internal static partial int tn_scene_finish_streaming(nint scene, uint timeoutMs);

    [LibraryImport(Library)]
    internal static partial int tn_scene_set_streaming_budget(nint scene, uint uploadsPerFrame);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int tn_load_model_cached(nint scene, string path, uint parent, out NativeImportResult result);

    [LibraryImport(Library)]
    internal static partial int tn_scene_get_asset_stats(nint scene, out NativeAssetStats stats);

    [LibraryImport(Library)]
    internal static partial int tn_scene_clear_asset_cache(nint scene);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial uint tn_overlay_add(nint scene, in NativeOverlayElement desc, string? text);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int tn_overlay_update(nint scene, uint id, in NativeOverlayElement desc, string? text);

    [LibraryImport(Library)]
    internal static partial int tn_overlay_get(nint scene, uint id, out NativeOverlayElement desc);

    [LibraryImport(Library)]
    internal static partial int tn_overlay_get_text(nint scene, uint id, byte* buffer, int capacity);

    [LibraryImport(Library)]
    internal static partial int tn_overlay_remove(nint scene, uint id);

    [LibraryImport(Library)]
    internal static partial int tn_overlay_clear(nint scene);

    [LibraryImport(Library)]
    internal static partial uint tn_overlay_hit_test(nint scene, float x, float y, float width, float height);

    [LibraryImport(Library)]
    internal static partial int tn_overlay_get_rect(nint scene, uint id, float width, float height, out Vector4 rect);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int tn_overlay_measure_text(nint scene, uint font, float size, string text, float maxWidth, out float width, out float height);

    [LibraryImport(Library)]
    internal static partial int tn_overlay_load_font(nint scene, byte* bytes, uint length);

    [LibraryImport(Library)]
    internal static partial int tn_overlay_configure(nint scene, float scale, int enabled);

    [LibraryImport(Library)]
    internal static partial nint tn_gamepads_create();

    [LibraryImport(Library)]
    internal static partial void tn_gamepads_destroy(nint pads);

    [LibraryImport(Library)]
    internal static partial int tn_gamepads_update(nint pads);

    [LibraryImport(Library)]
    internal static partial int tn_gamepads_slot_count(nint pads);

    [LibraryImport(Library)]
    internal static partial int tn_gamepads_configure(nint pads, float deadZone, float triggerThreshold);

    [LibraryImport(Library)]
    internal static partial int tn_gamepads_get_error(nint pads, byte* buffer, int capacity);

    [LibraryImport(Library)]
    internal static partial int tn_gamepad_get_state(nint pads, uint slot, out NativeGamepadState state);

    [LibraryImport(Library)]
    internal static partial int tn_gamepad_get_name(nint pads, uint slot, byte* buffer, int capacity);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int tn_gamepad_set_virtual(nint pads, uint slot, in NativeGamepadState state, string? name);

    [LibraryImport(Library)]
    internal static partial int tn_gamepad_rumble(nint pads, uint slot, float strong, float weak, uint durationMs);

    [LibraryImport(Library)]
    internal static partial int tn_physics_configure(nint scene, Vector3 gravity, float fixedTimestep, uint maxSubsteps);

    [LibraryImport(Library)]
    internal static partial int tn_physics_add_body(nint scene, uint node, in NativeBodyDesc desc);

    [LibraryImport(Library)]
    internal static partial int tn_physics_add_collider(nint scene, uint node, in NativeColliderDesc desc);

    [LibraryImport(Library)]
    internal static partial int tn_physics_remove(nint scene, uint node);

    [LibraryImport(Library)]
    internal static partial int tn_physics_has_body(nint scene, uint node);

    [LibraryImport(Library)]
    internal static partial int tn_physics_step(nint scene, float delta);

    [LibraryImport(Library)]
    internal static partial int tn_physics_apply_impulse(nint scene, uint node, Vector3 impulse, Vector3 torqueImpulse);

    [LibraryImport(Library)]
    internal static partial int tn_physics_add_force(nint scene, uint node, Vector3 force, Vector3 torque);

    [LibraryImport(Library)]
    internal static partial int tn_physics_set_velocity(nint scene, uint node, Vector3 linear, Vector3 angular);

    [LibraryImport(Library)]
    internal static partial int tn_physics_get_velocity(nint scene, uint node, out Vector3 linear, out Vector3 angular);

    [LibraryImport(Library)]
    internal static partial int tn_physics_teleport(nint scene, uint node, Vector3 position, Vector4 rotation);

    [LibraryImport(Library)]
    internal static partial int tn_physics_is_sleeping(nint scene, uint node);

    [LibraryImport(Library)]
    internal static partial int tn_physics_raycast(nint scene, Vector3 origin, Vector3 direction, float maxDistance, uint exclude, out NativePhysicsHit hit);

    [LibraryImport(Library)]
    internal static partial int tn_physics_take_events(nint scene, NativeContactEvent* events, uint capacity);

    [LibraryImport(Library)]
    internal static partial uint tn_physics_add_joint(nint scene, uint kind, uint nodeA, uint nodeB, Vector3 anchorA, Vector3 anchorB, Vector3 axis);

    [LibraryImport(Library)]
    internal static partial int tn_physics_remove_joint(nint scene, uint joint);

    [LibraryImport(Library)]
    internal static partial int tn_physics_move_character(nint scene, uint node, Vector3 desired, float delta, out Vector3 applied);

    [LibraryImport(Library)]
    internal static partial int tn_physics_configure_character(nint scene, float maxSlopeDegrees, float stepHeight, float snapToGround);

    [LibraryImport(Library)]
    internal static partial nint tn_audio_create(int offline, uint sampleRate);

    [LibraryImport(Library)]
    internal static partial void tn_audio_destroy(nint engine);

    [LibraryImport(Library)]
    internal static partial int tn_audio_get_info(nint engine, out uint sampleRate, byte* buffer, int capacity);

    [LibraryImport(Library)]
    internal static partial uint tn_audio_load_clip(nint engine, byte* bytes, uint length);

    [LibraryImport(Library)]
    internal static partial uint tn_audio_create_clip(nint engine, float* samples, uint sampleCount, uint channels, uint sampleRate);

    [LibraryImport(Library)]
    internal static partial float tn_audio_clip_duration(nint engine, uint clip);

    [LibraryImport(Library)]
    internal static partial int tn_audio_remove_clip(nint engine, uint clip);

    [LibraryImport(Library)]
    internal static partial uint tn_audio_play(nint engine, in NativeSoundDesc desc);

    [LibraryImport(Library)]
    internal static partial int tn_audio_update_source(nint engine, uint source, in NativeSoundDesc desc);

    [LibraryImport(Library)]
    internal static partial int tn_audio_get_source(nint engine, uint source, out NativeSoundDesc desc);

    [LibraryImport(Library)]
    internal static partial int tn_audio_is_playing(nint engine, uint source);

    [LibraryImport(Library)]
    internal static partial int tn_audio_stop(nint engine, uint source);

    [LibraryImport(Library)]
    internal static partial int tn_audio_seek(nint engine, uint source, float seconds);

    [LibraryImport(Library)]
    internal static partial float tn_audio_get_time(nint engine, uint source);

    [LibraryImport(Library)]
    internal static partial int tn_audio_set_listener(nint engine, Vector3 position, Vector3 forward, Vector3 up, Vector3 velocity);

    [LibraryImport(Library)]
    internal static partial int tn_audio_configure(nint engine, float masterGain, float dopplerFactor, float speedOfSound);

    [LibraryImport(Library)]
    internal static partial int tn_audio_sync_scene(nint engine, nint scene, uint listenerNode, float delta);

    [LibraryImport(Library)]
    internal static partial int tn_audio_render(nint engine, float* output, uint frames);

    [LibraryImport(Library)]
    internal static partial int tn_xr_probe(out NativeXrInfo info, byte* buffer, int capacity);

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
