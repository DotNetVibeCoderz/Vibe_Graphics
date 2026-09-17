//! Stable C ABI consumed by the managed `ThreeNet` library.
//!
//! Conventions:
//! * functions returning `i32` use `0` for success and a negative
//!   [`status`] code for failure; the message is retrievable with
//!   [`tn_last_error_message`];
//! * functions returning `u32` return `0` when they fail (ids are 1 based);
//! * every pointer argument is checked before use, so a null pointer is an
//!   error rather than undefined behaviour;
//! * strings are null terminated UTF-8.

#![allow(clippy::missing_safety_doc)]

use std::cell::RefCell;
use std::ffi::{CStr, CString, c_char, c_void};

use crate::animation::{AnimationClip, AnimationPlayer, Channel, Interpolation, TargetPath};
use crate::renderer::RenderPath;
use crate::shader::{CustomShader, ShaderLanguage};
use crate::camera::{Camera, Projection};
use crate::error::Error;
use crate::geometry::{Geometry, Topology, Vertex, primitives};
use crate::light::{Light, LightKind};
use crate::material::{AlphaMode, CullMode, Material, MaterialTextures, ShadingModel};
use crate::math::{Quat, Transform, Vec2, Vec3, Vec4};
use crate::raycast::{Ray, RaycastOptions, raycast};
use crate::renderer::{PowerPreference, Renderer, RendererConfig, ToneMapping};
use crate::scene::{MeshBinding, Scene};
use crate::texture::{SamplerDesc, Texture, TextureFormat, WrapMode};
use crate::window::{AppHandler, InputEvent, WindowConfig};

/// Status codes returned by the fallible entry points.
pub mod status {
    pub const OK: i32 = 0;
    pub const ERROR: i32 = -1;
    pub const NULL_POINTER: i32 = -2;
    pub const INVALID_HANDLE: i32 = -3;
    pub const INVALID_ARGUMENT: i32 = -4;
    pub const BUFFER_TOO_SMALL: i32 = -5;
}

thread_local! {
    static LAST_ERROR: RefCell<Option<CString>> = const { RefCell::new(None) };
}

fn set_last_error(message: impl Into<String>) {
    let message = message.into();
    log::debug!("threenet error: {message}");
    let value = CString::new(message).unwrap_or_else(|_| CString::new("invalid error").unwrap());
    LAST_ERROR.with(|slot| *slot.borrow_mut() = Some(value));
}

fn fail(error: Error) -> i32 {
    let code = match &error {
        Error::InvalidHandle(_) => status::INVALID_HANDLE,
        Error::InvalidArgument(_) => status::INVALID_ARGUMENT,
        _ => status::ERROR,
    };
    set_last_error(error.to_string());
    code
}

/// Copies `text` into the caller provided buffer and returns the number of
/// bytes required (including the terminator), so callers can size a buffer by
/// passing a null pointer first.
unsafe fn copy_string(text: &str, buffer: *mut c_char, capacity: i32) -> i32 {
    let bytes = text.as_bytes();
    let required = bytes.len() as i32 + 1;
    if buffer.is_null() || capacity <= 0 {
        return required;
    }
    if capacity < required {
        return status::BUFFER_TOO_SMALL;
    }
    unsafe {
        std::ptr::copy_nonoverlapping(bytes.as_ptr() as *const c_char, buffer, bytes.len());
        *buffer.add(bytes.len()) = 0;
    }
    required
}

unsafe fn str_from_ptr<'a>(pointer: *const c_char) -> Option<&'a str> {
    if pointer.is_null() {
        return None;
    }
    unsafe { CStr::from_ptr(pointer) }.to_str().ok()
}

macro_rules! scene_ref {
    ($scene:expr) => {
        match unsafe { $scene.as_mut() } {
            Some(scene) => scene,
            None => {
                set_last_error("scene pointer is null");
                return status::NULL_POINTER;
            }
        }
    };
    ($scene:expr, $fallback:expr) => {
        match unsafe { $scene.as_mut() } {
            Some(scene) => scene,
            None => {
                set_last_error("scene pointer is null");
                return $fallback;
            }
        }
    };
}

macro_rules! renderer_ref {
    ($renderer:expr) => {
        match unsafe { $renderer.as_mut() } {
            Some(renderer) => renderer,
            None => {
                set_last_error("renderer pointer is null");
                return status::NULL_POINTER;
            }
        }
    };
}

// ---------------------------------------------------------------- interop types

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct TnVec2 {
    pub x: f32,
    pub y: f32,
}

impl From<TnVec2> for Vec2 {
    fn from(value: TnVec2) -> Self {
        Vec2::new(value.x, value.y)
    }
}

impl From<Vec2> for TnVec2 {
    fn from(value: Vec2) -> Self {
        Self {
            x: value.x,
            y: value.y,
        }
    }
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct TnVec3 {
    pub x: f32,
    pub y: f32,
    pub z: f32,
}

impl From<TnVec3> for Vec3 {
    fn from(value: TnVec3) -> Self {
        Vec3::new(value.x, value.y, value.z)
    }
}

impl From<Vec3> for TnVec3 {
    fn from(value: Vec3) -> Self {
        Self {
            x: value.x,
            y: value.y,
            z: value.z,
        }
    }
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct TnVec4 {
    pub x: f32,
    pub y: f32,
    pub z: f32,
    pub w: f32,
}

impl From<TnVec4> for Vec4 {
    fn from(value: TnVec4) -> Self {
        Vec4::new(value.x, value.y, value.z, value.w)
    }
}

impl From<Vec4> for TnVec4 {
    fn from(value: Vec4) -> Self {
        Self {
            x: value.x,
            y: value.y,
            z: value.z,
            w: value.w,
        }
    }
}

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct TnTransform {
    pub translation: TnVec3,
    /// Rotation quaternion `(x, y, z, w)`.
    pub rotation: TnVec4,
    pub scale: TnVec3,
}

impl Default for TnTransform {
    fn default() -> Self {
        Self {
            translation: TnVec3::default(),
            rotation: TnVec4 {
                x: 0.0,
                y: 0.0,
                z: 0.0,
                w: 1.0,
            },
            scale: TnVec3 {
                x: 1.0,
                y: 1.0,
                z: 1.0,
            },
        }
    }
}

impl From<TnTransform> for Transform {
    fn from(value: TnTransform) -> Self {
        Transform {
            translation: value.translation.into(),
            rotation: Quat::from_xyzw(
                value.rotation.x,
                value.rotation.y,
                value.rotation.z,
                value.rotation.w,
            )
            .normalize(),
            scale: value.scale.into(),
        }
    }
}

impl From<Transform> for TnTransform {
    fn from(value: Transform) -> Self {
        Self {
            translation: value.translation.into(),
            rotation: TnVec4 {
                x: value.rotation.x,
                y: value.rotation.y,
                z: value.rotation.z,
                w: value.rotation.w,
            },
            scale: value.scale.into(),
        }
    }
}

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct TnMaterialDesc {
    pub shading: u32,
    pub alpha_mode: u32,
    pub cull_mode: u32,
    pub base_color: TnVec4,
    pub emissive: TnVec3,
    pub emissive_intensity: f32,
    pub metallic: f32,
    pub roughness: f32,
    pub specular: TnVec3,
    pub shininess: f32,
    pub reflectance: f32,
    pub normal_scale: f32,
    pub occlusion_strength: f32,
    pub alpha_cutoff: f32,
    pub uv_scale: TnVec2,
    pub uv_offset: TnVec2,
    pub depth_write: i32,
    pub depth_test: i32,
    pub wireframe: i32,
    pub render_order: i32,
    /// Texture ids, `0` means no texture.
    pub base_color_texture: u32,
    pub normal_texture: u32,
    pub metallic_roughness_texture: u32,
    pub emissive_texture: u32,
    pub occlusion_texture: u32,
    /// Custom shader id, `0` for the built-in shading.
    pub shader: u32,
    /// Free parameters exposed to custom shaders as `custom0` and `custom1`.
    pub custom: [f32; 8],
}

impl Default for TnMaterialDesc {
    fn default() -> Self {
        Material::default().into()
    }
}

impl From<Material> for TnMaterialDesc {
    fn from(value: Material) -> Self {
        Self {
            shading: value.shading as u32,
            alpha_mode: value.alpha_mode as u32,
            cull_mode: value.cull_mode as u32,
            base_color: value.base_color.into(),
            emissive: value.emissive.into(),
            emissive_intensity: value.emissive_intensity,
            metallic: value.metallic,
            roughness: value.roughness,
            specular: value.specular.into(),
            shininess: value.shininess,
            reflectance: value.reflectance,
            normal_scale: value.normal_scale,
            occlusion_strength: value.occlusion_strength,
            alpha_cutoff: value.alpha_cutoff,
            uv_scale: value.uv_scale.into(),
            uv_offset: value.uv_offset.into(),
            depth_write: i32::from(value.depth_write),
            depth_test: i32::from(value.depth_test),
            wireframe: i32::from(value.wireframe),
            render_order: value.render_order,
            base_color_texture: value.textures.base_color.unwrap_or(0),
            normal_texture: value.textures.normal.unwrap_or(0),
            metallic_roughness_texture: value.textures.metallic_roughness.unwrap_or(0),
            emissive_texture: value.textures.emissive.unwrap_or(0),
            occlusion_texture: value.textures.occlusion.unwrap_or(0),
            shader: value.shader.unwrap_or(0),
            custom: value.custom,
        }
    }
}

impl TnMaterialDesc {
    fn apply(&self, material: &mut Material) {
        let slot = |id: u32| (id != 0).then_some(id);
        material.shading = ShadingModel::from_u32(self.shading);
        material.alpha_mode = AlphaMode::from_u32(self.alpha_mode);
        material.cull_mode = CullMode::from_u32(self.cull_mode);
        material.base_color = self.base_color.into();
        material.emissive = self.emissive.into();
        material.emissive_intensity = self.emissive_intensity;
        material.metallic = self.metallic;
        material.roughness = self.roughness;
        material.specular = self.specular.into();
        material.shininess = self.shininess;
        material.reflectance = self.reflectance;
        material.normal_scale = self.normal_scale;
        material.occlusion_strength = self.occlusion_strength;
        material.alpha_cutoff = self.alpha_cutoff;
        material.uv_scale = self.uv_scale.into();
        material.uv_offset = self.uv_offset.into();
        material.depth_write = self.depth_write != 0;
        material.depth_test = self.depth_test != 0;
        material.wireframe = self.wireframe != 0;
        material.render_order = self.render_order;
        material.textures = MaterialTextures {
            base_color: slot(self.base_color_texture),
            normal: slot(self.normal_texture),
            metallic_roughness: slot(self.metallic_roughness_texture),
            emissive: slot(self.emissive_texture),
            occlusion: slot(self.occlusion_texture),
        };
        material.shader = slot(self.shader);
        material.custom = self.custom;
        material.touch();
    }
}

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct TnLightDesc {
    pub kind: u32,
    pub color: TnVec3,
    pub intensity: f32,
    pub range: f32,
    pub inner_cone_angle: f32,
    pub outer_cone_angle: f32,
    pub width: f32,
    pub height: f32,
    pub cast_shadow: i32,
    pub enabled: i32,
    /// Depth bias in normalised shadow map depth.
    pub shadow_bias: f32,
    /// Normal offset in shadow map texels.
    pub shadow_normal_bias: f32,
    /// 0 = no darkening, 1 = fully dark shadows.
    pub shadow_strength: f32,
}

impl From<TnLightDesc> for Light {
    fn from(value: TnLightDesc) -> Self {
        Light {
            kind: LightKind::from_u32(value.kind),
            color: value.color.into(),
            intensity: value.intensity,
            range: value.range,
            inner_cone_angle: value.inner_cone_angle,
            outer_cone_angle: value.outer_cone_angle,
            size: (value.width, value.height),
            cast_shadow: value.cast_shadow != 0,
            enabled: value.enabled != 0,
            shadow_bias: value.shadow_bias,
            shadow_normal_bias: value.shadow_normal_bias,
            shadow_strength: value.shadow_strength.clamp(0.0, 1.0),
        }
    }
}

impl From<Light> for TnLightDesc {
    fn from(value: Light) -> Self {
        Self {
            kind: value.kind as u32,
            color: value.color.into(),
            intensity: value.intensity,
            range: value.range,
            inner_cone_angle: value.inner_cone_angle,
            outer_cone_angle: value.outer_cone_angle,
            width: value.size.0,
            height: value.size.1,
            cast_shadow: i32::from(value.cast_shadow),
            enabled: i32::from(value.enabled),
            shadow_bias: value.shadow_bias,
            shadow_normal_bias: value.shadow_normal_bias,
            shadow_strength: value.shadow_strength,
        }
    }
}

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct TnCameraDesc {
    /// `0` = perspective, `1` = orthographic.
    pub projection: u32,
    /// Vertical field of view in radians (perspective).
    pub fov_y: f32,
    /// View volume height in world units (orthographic).
    pub ortho_height: f32,
    /// `<= 0` follows the render target aspect ratio.
    pub aspect: f32,
    pub near: f32,
    pub far: f32,
    /// Frustum half angles for `projection` 2 (off-axis), radians.
    pub angle_left: f32,
    pub angle_right: f32,
    pub angle_up: f32,
    pub angle_down: f32,
}

impl From<TnCameraDesc> for Camera {
    fn from(value: TnCameraDesc) -> Self {
        let aspect = (value.aspect > 0.0).then_some(value.aspect);
        Camera {
            projection: if value.projection == 2 {
                Projection::OffAxis {
                    left: value.angle_left,
                    right: value.angle_right,
                    up: value.angle_up,
                    down: value.angle_down,
                    near: value.near,
                    far: value.far,
                }
            } else if value.projection == 1 {
                Projection::Orthographic {
                    height: value.ortho_height,
                    aspect,
                    near: value.near,
                    far: value.far,
                }
            } else {
                Projection::Perspective {
                    fov_y: value.fov_y,
                    aspect,
                    near: value.near,
                    far: value.far,
                }
            },
            viewport: None,
        }
    }
}

impl From<Camera> for TnCameraDesc {
    fn from(value: Camera) -> Self {
        match value.projection {
            Projection::Perspective {
                fov_y,
                aspect,
                near,
                far,
            } => Self {
                projection: 0,
                fov_y,
                ortho_height: 0.0,
                aspect: aspect.unwrap_or(0.0),
                near,
                far,
                angle_left: 0.0,
                angle_right: 0.0,
                angle_up: 0.0,
                angle_down: 0.0,
            },
            Projection::OffAxis { left, right, up, down, near, far } => Self {
                projection: 2,
                fov_y: up - down,
                ortho_height: 0.0,
                aspect: 0.0,
                near,
                far,
                angle_left: left,
                angle_right: right,
                angle_up: up,
                angle_down: down,
            },
            Projection::Orthographic {
                height,
                aspect,
                near,
                far,
            } => Self {
                projection: 1,
                fov_y: 0.0,
                ortho_height: height,
                aspect: aspect.unwrap_or(0.0),
                near,
                far,
                angle_left: 0.0,
                angle_right: 0.0,
                angle_up: 0.0,
                angle_down: 0.0,
            },
        }
    }
}

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct TnEnvironmentDesc {
    pub background: TnVec4,
    pub ambient_color: TnVec3,
    pub ambient_intensity: f32,
    pub fog_color: TnVec3,
    pub fog_density: f32,
    pub fog_start: f32,
    pub fog_end: f32,
    pub environment_map: u32,
    pub environment_intensity: f32,
}

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct TnRendererDesc {
    pub width: u32,
    pub height: u32,
    pub vsync: i32,
    pub msaa_samples: u32,
    pub exposure: f32,
    pub tone_mapping: u32,
    pub bloom: i32,
    pub bloom_intensity: f32,
    pub bloom_threshold: f32,
    pub frustum_culling: i32,
    pub power_preference: u32,
    /// Offscreen only: emit BGRA pixels for UI toolkit bitmaps.
    pub bgra_output: i32,
    pub shadows: i32,
    pub shadow_map_size: u32,
    pub shadow_distance: f32,
    pub shadow_cascades: u32,
    pub shadow_softness: u32,
    pub ssao: i32,
    pub ssao_radius: f32,
    pub ssao_intensity: f32,
    pub ssao_bias: f32,
    pub ssao_samples: u32,
    pub ssao_direct_strength: f32,
    pub render_path: u32,
    pub depth_of_field: i32,
    pub dof_focus_distance: f32,
    pub dof_focus_range: f32,
    pub dof_max_blur: f32,
    pub motion_blur: i32,
    pub motion_blur_strength: f32,
    pub motion_blur_samples: u32,
}

impl From<TnRendererDesc> for RendererConfig {
    fn from(value: TnRendererDesc) -> Self {
        RendererConfig {
            width: value.width,
            height: value.height,
            vsync: value.vsync != 0,
            msaa_samples: value.msaa_samples,
            exposure: value.exposure,
            tone_mapping: ToneMapping::from_u32(value.tone_mapping),
            bloom: value.bloom != 0,
            bloom_intensity: value.bloom_intensity,
            bloom_threshold: value.bloom_threshold,
            frustum_culling: value.frustum_culling != 0,
            power_preference: PowerPreference::from_u32(value.power_preference),
            bgra_output: value.bgra_output != 0,
            shadows: value.shadows != 0,
            shadow_map_size: value.shadow_map_size,
            shadow_distance: value.shadow_distance,
            shadow_cascades: value.shadow_cascades,
            shadow_softness: value.shadow_softness,
            ssao: value.ssao != 0,
            ssao_radius: value.ssao_radius,
            ssao_intensity: value.ssao_intensity,
            ssao_bias: value.ssao_bias,
            ssao_samples: value.ssao_samples,
            ssao_direct_strength: value.ssao_direct_strength,
            render_path: RenderPath::from_u32(value.render_path),
            depth_of_field: value.depth_of_field != 0,
            dof_focus_distance: value.dof_focus_distance,
            dof_focus_range: value.dof_focus_range,
            dof_max_blur: value.dof_max_blur,
            motion_blur: value.motion_blur != 0,
            motion_blur_strength: value.motion_blur_strength,
            motion_blur_samples: value.motion_blur_samples,
        }
    }
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct TnFrameStats {
    pub draw_calls: u32,
    pub triangles: u32,
    pub visible_nodes: u32,
    pub culled_nodes: u32,
    pub lights: u32,
    pub cpu_time_ms: f32,
    pub shadow_layers: u32,
    pub shadow_draw_calls: u32,
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct TnRayHit {
    pub node: u32,
    pub distance: f32,
    pub point: TnVec3,
    pub normal: TnVec3,
    pub triangle: u32,
    pub barycentric: TnVec2,
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct TnImportResult {
    pub root: u32,
    pub node_count: u32,
    pub geometry_count: u32,
    pub material_count: u32,
    pub texture_count: u32,
    pub animation_count: u32,
    pub skin_count: u32,
}

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct TnSamplerDesc {
    pub wrap_u: u32,
    pub wrap_v: u32,
    pub linear_filter: i32,
    pub mipmaps: i32,
    pub anisotropy: u32,
}

impl From<TnSamplerDesc> for SamplerDesc {
    fn from(value: TnSamplerDesc) -> Self {
        SamplerDesc {
            wrap_u: WrapMode::from_u32(value.wrap_u),
            wrap_v: WrapMode::from_u32(value.wrap_v),
            linear_filter: value.linear_filter != 0,
            mipmaps: value.mipmaps != 0,
            anisotropy: value.anisotropy.min(16) as u16,
        }
    }
}

// ------------------------------------------------------------------- library

/// ABI revision of this binary; the managed loader refuses a mismatch.
#[unsafe(no_mangle)]
pub extern "C" fn tn_abi_version() -> u32 {
    crate::ABI_VERSION
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_version(buffer: *mut c_char, capacity: i32) -> i32 {
    unsafe { copy_string(crate::VERSION, buffer, capacity) }
}

/// Initialises logging. `level`: 0 = off, 1 = error, 2 = warn, 3 = info,
/// 4 = debug, 5 = trace.
#[unsafe(no_mangle)]
pub extern "C" fn tn_init_logging(level: u32) {
    let filter = match level {
        0 => log::LevelFilter::Off,
        1 => log::LevelFilter::Error,
        2 => log::LevelFilter::Warn,
        3 => log::LevelFilter::Info,
        4 => log::LevelFilter::Debug,
        _ => log::LevelFilter::Trace,
    };
    // Ignore the error: logging may already be initialised by the host.
    let _ = env_logger::Builder::new().filter_level(filter).try_init();
}

/// Copies the last error message of the calling thread into `buffer`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_last_error_message(buffer: *mut c_char, capacity: i32) -> i32 {
    LAST_ERROR.with(|slot| match slot.borrow().as_ref() {
        Some(message) => unsafe {
            copy_string(message.to_string_lossy().as_ref(), buffer, capacity)
        },
        None => unsafe { copy_string("", buffer, capacity) },
    })
}

// --------------------------------------------------------------------- scene

#[unsafe(no_mangle)]
pub extern "C" fn tn_scene_create() -> *mut Scene {
    Box::into_raw(Box::new(Scene::new()))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_scene_destroy(scene: *mut Scene) {
    if !scene.is_null() {
        drop(unsafe { Box::from_raw(scene) });
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_scene_root(scene: *mut Scene) -> u32 {
    let scene = scene_ref!(scene, 0);
    scene.root()
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_scene_node_count(scene: *mut Scene) -> u32 {
    let scene = scene_ref!(scene, 0);
    scene.node_count() as u32
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_scene_create_node(scene: *mut Scene, parent: u32) -> u32 {
    let scene = scene_ref!(scene, 0);
    match scene.create_node((parent != 0).then_some(parent)) {
        Ok(id) => id,
        Err(error) => {
            fail(error);
            0
        }
    }
}

/// Deep copies `node` under `parent` (0 = root), sharing geometry and
/// materials. Returns the new node id, or 0 on failure.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_clone(scene: *mut Scene, node: u32, parent: u32) -> u32 {
    let scene = scene_ref!(scene, 0);
    match scene.clone_subtree(node, (parent != 0).then_some(parent)) {
        Ok(id) => id,
        Err(error) => {
            fail(error);
            0
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_scene_remove_node(scene: *mut Scene, node: u32) -> i32 {
    let scene = scene_ref!(scene);
    match scene.remove_node(node) {
        Ok(()) => status::OK,
        Err(error) => fail(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_scene_set_parent(scene: *mut Scene, node: u32, parent: u32) -> i32 {
    let scene = scene_ref!(scene);
    match scene.set_parent(node, (parent != 0).then_some(parent)) {
        Ok(()) => status::OK,
        Err(error) => fail(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_scene_find_by_name(scene: *mut Scene, name: *const c_char) -> u32 {
    let scene = scene_ref!(scene, 0);
    let Some(name) = (unsafe { str_from_ptr(name) }) else {
        set_last_error("name is null or not valid UTF-8");
        return 0;
    };
    scene.find_by_name(name).unwrap_or(0)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_scene_set_active_camera(scene: *mut Scene, node: u32) -> i32 {
    let scene = scene_ref!(scene);
    scene.set_active_camera((node != 0).then_some(node));
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_scene_set_environment(
    scene: *mut Scene,
    desc: *const TnEnvironmentDesc,
) -> i32 {
    let scene = scene_ref!(scene);
    let Some(desc) = (unsafe { desc.as_ref() }) else {
        set_last_error("environment descriptor is null");
        return status::NULL_POINTER;
    };
    scene.environment.background = [
        desc.background.x,
        desc.background.y,
        desc.background.z,
        desc.background.w,
    ];
    scene.environment.ambient_color = desc.ambient_color.into();
    scene.environment.ambient_intensity = desc.ambient_intensity;
    scene.environment.fog_color = desc.fog_color.into();
    scene.environment.fog_density = desc.fog_density;
    scene.environment.fog_start = desc.fog_start;
    scene.environment.fog_end = desc.fog_end;
    scene.environment.environment_map = (desc.environment_map != 0).then_some(desc.environment_map);
    scene.environment.environment_intensity = desc.environment_intensity;
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_scene_compute_bounds(
    scene: *mut Scene,
    node: u32,
    out_min: *mut TnVec3,
    out_max: *mut TnVec3,
) -> i32 {
    let scene = scene_ref!(scene);
    if out_min.is_null() || out_max.is_null() {
        set_last_error("output pointer is null");
        return status::NULL_POINTER;
    }
    let bounds = scene.compute_bounds(if node == 0 { scene.root() } else { node });
    unsafe {
        *out_min = bounds.min.into();
        *out_max = bounds.max.into();
    }
    status::OK
}

// ---------------------------------------------------------------------- node

macro_rules! node_mut {
    ($scene:expr, $node:expr) => {
        match $scene.node_mut($node) {
            Some(node) => node,
            None => {
                set_last_error("invalid node handle");
                return status::INVALID_HANDLE;
            }
        }
    };
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_set_transform(
    scene: *mut Scene,
    node: u32,
    transform: *const TnTransform,
) -> i32 {
    let scene = scene_ref!(scene);
    let Some(transform) = (unsafe { transform.as_ref() }) else {
        set_last_error("transform pointer is null");
        return status::NULL_POINTER;
    };
    node_mut!(scene, node).transform = (*transform).into();
    scene.mark_dirty(node);
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_get_transform(
    scene: *mut Scene,
    node: u32,
    out_transform: *mut TnTransform,
) -> i32 {
    let scene = scene_ref!(scene);
    if out_transform.is_null() {
        set_last_error("output pointer is null");
        return status::NULL_POINTER;
    }
    let transform = node_mut!(scene, node).transform;
    unsafe { *out_transform = transform.into() };
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_set_position(
    scene: *mut Scene,
    node: u32,
    position: TnVec3,
) -> i32 {
    let scene = scene_ref!(scene);
    node_mut!(scene, node).transform.translation = position.into();
    scene.mark_dirty(node);
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_set_rotation(
    scene: *mut Scene,
    node: u32,
    rotation: TnVec4,
) -> i32 {
    let scene = scene_ref!(scene);
    node_mut!(scene, node).transform.rotation =
        Quat::from_xyzw(rotation.x, rotation.y, rotation.z, rotation.w).normalize();
    scene.mark_dirty(node);
    status::OK
}

/// Euler angles in radians, `YXZ` order (pitch, yaw, roll in `x`, `y`, `z`).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_set_euler_angles(
    scene: *mut Scene,
    node: u32,
    angles: TnVec3,
) -> i32 {
    let scene = scene_ref!(scene);
    node_mut!(scene, node)
        .transform
        .set_euler_angles(angles.into());
    scene.mark_dirty(node);
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_get_euler_angles(
    scene: *mut Scene,
    node: u32,
    out_angles: *mut TnVec3,
) -> i32 {
    let scene = scene_ref!(scene);
    if out_angles.is_null() {
        set_last_error("output pointer is null");
        return status::NULL_POINTER;
    }
    let angles = node_mut!(scene, node).transform.euler_angles();
    unsafe { *out_angles = angles.into() };
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_set_scale(scene: *mut Scene, node: u32, scale: TnVec3) -> i32 {
    let scene = scene_ref!(scene);
    node_mut!(scene, node).transform.scale = scale.into();
    scene.mark_dirty(node);
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_look_at(
    scene: *mut Scene,
    node: u32,
    target: TnVec3,
    up: TnVec3,
) -> i32 {
    let scene = scene_ref!(scene);
    node_mut!(scene, node)
        .transform
        .look_at(target.into(), Vec3::from(up).normalize_or(Vec3::Y));
    scene.mark_dirty(node);
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_set_visible(scene: *mut Scene, node: u32, visible: i32) -> i32 {
    let scene = scene_ref!(scene);
    node_mut!(scene, node).visible = visible != 0;
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_set_layers(scene: *mut Scene, node: u32, layers: u32) -> i32 {
    let scene = scene_ref!(scene);
    node_mut!(scene, node).layers = layers;
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_set_user_data(
    scene: *mut Scene,
    node: u32,
    user_data: u64,
) -> i32 {
    let scene = scene_ref!(scene);
    node_mut!(scene, node).user_data = user_data;
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_get_user_data(scene: *mut Scene, node: u32, out: *mut u64) -> i32 {
    let scene = scene_ref!(scene);
    if out.is_null() {
        set_last_error("output pointer is null");
        return status::NULL_POINTER;
    }
    let value = node_mut!(scene, node).user_data;
    unsafe { *out = value };
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_set_name(
    scene: *mut Scene,
    node: u32,
    name: *const c_char,
) -> i32 {
    let scene = scene_ref!(scene);
    let Some(name) = (unsafe { str_from_ptr(name) }) else {
        set_last_error("name is null or not valid UTF-8");
        return status::INVALID_ARGUMENT;
    };
    node_mut!(scene, node).name = name.to_string();
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_get_name(
    scene: *mut Scene,
    node: u32,
    buffer: *mut c_char,
    capacity: i32,
) -> i32 {
    let scene = scene_ref!(scene);
    let Some(node) = scene.node(node) else {
        set_last_error("invalid node handle");
        return status::INVALID_HANDLE;
    };
    unsafe { copy_string(&node.name, buffer, capacity) }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_get_world_matrix(
    scene: *mut Scene,
    node: u32,
    out_matrix: *mut f32,
) -> i32 {
    let scene = scene_ref!(scene);
    if out_matrix.is_null() {
        set_last_error("output pointer is null");
        return status::NULL_POINTER;
    }
    let Some(matrix) = scene.world_matrix(node) else {
        set_last_error("invalid node handle");
        return status::INVALID_HANDLE;
    };
    let values = matrix.to_cols_array();
    unsafe { std::ptr::copy_nonoverlapping(values.as_ptr(), out_matrix, 16) };
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_get_parent(scene: *mut Scene, node: u32) -> u32 {
    let scene = scene_ref!(scene, 0);
    scene.node(node).and_then(|n| n.parent()).unwrap_or(0)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_get_child_count(scene: *mut Scene, node: u32) -> u32 {
    let scene = scene_ref!(scene, 0);
    scene
        .node(node)
        .map(|n| n.children().len() as u32)
        .unwrap_or(0)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_get_child(scene: *mut Scene, node: u32, index: u32) -> u32 {
    let scene = scene_ref!(scene, 0);
    scene
        .node(node)
        .and_then(|n| n.children().get(index as usize).copied())
        .unwrap_or(0)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_attach_mesh(
    scene: *mut Scene,
    node: u32,
    geometry: u32,
    material: u32,
) -> i32 {
    let scene = scene_ref!(scene);
    if scene.geometry(geometry).is_none() {
        set_last_error("invalid geometry handle");
        return status::INVALID_HANDLE;
    }
    if scene.material(material).is_none() {
        set_last_error("invalid material handle");
        return status::INVALID_HANDLE;
    }
    node_mut!(scene, node).mesh = Some(MeshBinding {
        geometry,
        material,
        cast_shadow: true,
        receive_shadow: true,
        skin: None,
    });
    status::OK
}

/// Sets whether the mesh at `node` casts and receives shadows.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_set_shadow_flags(
    scene: *mut Scene,
    node: u32,
    cast_shadow: i32,
    receive_shadow: i32,
) -> i32 {
    let scene = scene_ref!(scene);
    match node_mut!(scene, node).mesh.as_mut() {
        Some(mesh) => {
            mesh.cast_shadow = cast_shadow != 0;
            mesh.receive_shadow = receive_shadow != 0;
            status::OK
        }
        None => {
            set_last_error("node has no mesh");
            status::INVALID_ARGUMENT
        }
    }
}

/// Reads the shadow flags of the mesh at `node` (bit 0 = cast, bit 1 = receive).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_get_shadow_flags(
    scene: *mut Scene,
    node: u32,
    out_flags: *mut u32,
) -> i32 {
    let scene = scene_ref!(scene);
    if out_flags.is_null() {
        set_last_error("output pointer is null");
        return status::NULL_POINTER;
    }
    let flags = node_mut!(scene, node)
        .mesh
        .map(|mesh| u32::from(mesh.cast_shadow) | (u32::from(mesh.receive_shadow) << 1))
        .unwrap_or(0);
    unsafe { *out_flags = flags };
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_detach_mesh(scene: *mut Scene, node: u32) -> i32 {
    let scene = scene_ref!(scene);
    node_mut!(scene, node).mesh = None;
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_set_light(
    scene: *mut Scene,
    node: u32,
    desc: *const TnLightDesc,
) -> i32 {
    let scene = scene_ref!(scene);
    let Some(desc) = (unsafe { desc.as_ref() }) else {
        set_last_error("light descriptor is null");
        return status::NULL_POINTER;
    };
    node_mut!(scene, node).light = Some((*desc).into());
    status::OK
}

/// Reads the light attached to `node`. Returns `INVALID_ARGUMENT` when the node
/// carries no light, so callers can distinguish "no light" from a failure.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_get_light(
    scene: *mut Scene,
    node: u32,
    out_desc: *mut TnLightDesc,
) -> i32 {
    let scene = scene_ref!(scene);
    if out_desc.is_null() {
        set_last_error("output pointer is null");
        return status::NULL_POINTER;
    }
    match node_mut!(scene, node).light {
        Some(light) => {
            unsafe { *out_desc = light.into() };
            status::OK
        }
        None => {
            set_last_error("node has no light");
            status::INVALID_ARGUMENT
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_clear_light(scene: *mut Scene, node: u32) -> i32 {
    let scene = scene_ref!(scene);
    node_mut!(scene, node).light = None;
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_set_camera(
    scene: *mut Scene,
    node: u32,
    desc: *const TnCameraDesc,
) -> i32 {
    let scene = scene_ref!(scene);
    let Some(desc) = (unsafe { desc.as_ref() }) else {
        set_last_error("camera descriptor is null");
        return status::NULL_POINTER;
    };
    node_mut!(scene, node).camera = Some((*desc).into());
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_get_camera(
    scene: *mut Scene,
    node: u32,
    out_desc: *mut TnCameraDesc,
) -> i32 {
    let scene = scene_ref!(scene);
    if out_desc.is_null() {
        set_last_error("output pointer is null");
        return status::NULL_POINTER;
    }
    let Some(camera) = scene.node(node).and_then(|n| n.camera) else {
        set_last_error("the node has no camera");
        return status::INVALID_HANDLE;
    };
    unsafe { *out_desc = camera.into() };
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_node_clear_camera(scene: *mut Scene, node: u32) -> i32 {
    let scene = scene_ref!(scene);
    node_mut!(scene, node).camera = None;
    status::OK
}

// ------------------------------------------------------------------ geometry

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_geometry_create(
    scene: *mut Scene,
    vertices: *const Vertex,
    vertex_count: u32,
    indices: *const u32,
    index_count: u32,
    topology: u32,
) -> u32 {
    let scene = scene_ref!(scene, 0);
    if vertices.is_null() || vertex_count == 0 {
        set_last_error("vertex buffer is null or empty");
        return 0;
    }
    let vertices = unsafe { std::slice::from_raw_parts(vertices, vertex_count as usize) }.to_vec();
    let indices = if indices.is_null() || index_count == 0 {
        Vec::new()
    } else {
        unsafe { std::slice::from_raw_parts(indices, index_count as usize) }.to_vec()
    };
    let mut geometry = Geometry::new(vertices, indices);
    geometry.topology = match topology {
        1 => Topology::LineList,
        2 => Topology::PointList,
        _ => Topology::TriangleList,
    };
    scene.add_geometry(geometry)
}

/// Replaces the vertex and index data of an existing geometry in place.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_geometry_update(
    scene: *mut Scene,
    geometry: u32,
    vertices: *const Vertex,
    vertex_count: u32,
    indices: *const u32,
    index_count: u32,
) -> i32 {
    let scene = scene_ref!(scene);
    let Some(target) = scene.geometry_mut(geometry) else {
        set_last_error("invalid geometry handle");
        return status::INVALID_HANDLE;
    };
    if vertices.is_null() || vertex_count == 0 {
        set_last_error("vertex buffer is null or empty");
        return status::INVALID_ARGUMENT;
    }
    target.vertices =
        unsafe { std::slice::from_raw_parts(vertices, vertex_count as usize) }.to_vec();
    target.indices = if indices.is_null() || index_count == 0 {
        Vec::new()
    } else {
        unsafe { std::slice::from_raw_parts(indices, index_count as usize) }.to_vec()
    };
    target.compute_bounds();
    target.touch();
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_geometry_destroy(scene: *mut Scene, geometry: u32) -> i32 {
    let scene = scene_ref!(scene);
    if scene.remove_geometry(geometry) {
        status::OK
    } else {
        set_last_error("invalid geometry handle");
        status::INVALID_HANDLE
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_geometry_compute_normals(scene: *mut Scene, geometry: u32) -> i32 {
    let scene = scene_ref!(scene);
    match scene.geometry_mut(geometry) {
        Some(geometry) => {
            geometry.compute_normals();
            status::OK
        }
        None => {
            set_last_error("invalid geometry handle");
            status::INVALID_HANDLE
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_geometry_compute_tangents(scene: *mut Scene, geometry: u32) -> i32 {
    let scene = scene_ref!(scene);
    match scene.geometry_mut(geometry) {
        Some(geometry) => {
            geometry.compute_tangents();
            status::OK
        }
        None => {
            set_last_error("invalid geometry handle");
            status::INVALID_HANDLE
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_geometry_get_counts(
    scene: *mut Scene,
    geometry: u32,
    out_vertex_count: *mut u32,
    out_index_count: *mut u32,
) -> i32 {
    let scene = scene_ref!(scene);
    let Some(geometry) = scene.geometry(geometry) else {
        set_last_error("invalid geometry handle");
        return status::INVALID_HANDLE;
    };
    unsafe {
        if !out_vertex_count.is_null() {
            *out_vertex_count = geometry.vertices.len() as u32;
        }
        if !out_index_count.is_null() {
            *out_index_count = geometry.indices.len() as u32;
        }
    }
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_geometry_plane(
    scene: *mut Scene,
    width: f32,
    height: f32,
    width_segments: u32,
    height_segments: u32,
) -> u32 {
    let scene = scene_ref!(scene, 0);
    scene.add_geometry(primitives::plane(
        width,
        height,
        width_segments,
        height_segments,
    ))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_geometry_box(
    scene: *mut Scene,
    width: f32,
    height: f32,
    depth: f32,
    segments: u32,
) -> u32 {
    let scene = scene_ref!(scene, 0);
    scene.add_geometry(primitives::cuboid(width, height, depth, segments))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_geometry_sphere(
    scene: *mut Scene,
    radius: f32,
    width_segments: u32,
    height_segments: u32,
) -> u32 {
    let scene = scene_ref!(scene, 0);
    scene.add_geometry(primitives::sphere(radius, width_segments, height_segments))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_geometry_cylinder(
    scene: *mut Scene,
    radius_top: f32,
    radius_bottom: f32,
    height: f32,
    radial_segments: u32,
    height_segments: u32,
    capped: i32,
) -> u32 {
    let scene = scene_ref!(scene, 0);
    scene.add_geometry(primitives::cylinder(
        radius_top,
        radius_bottom,
        height,
        radial_segments,
        height_segments,
        capped != 0,
    ))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_geometry_cone(
    scene: *mut Scene,
    radius: f32,
    height: f32,
    radial_segments: u32,
) -> u32 {
    let scene = scene_ref!(scene, 0);
    scene.add_geometry(primitives::cone(radius, height, radial_segments))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_geometry_torus(
    scene: *mut Scene,
    radius: f32,
    tube: f32,
    radial_segments: u32,
    tubular_segments: u32,
    arc: f32,
) -> u32 {
    let scene = scene_ref!(scene, 0);
    scene.add_geometry(primitives::torus(
        radius,
        tube,
        radial_segments,
        tubular_segments,
        arc,
    ))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_geometry_grid(scene: *mut Scene, size: f32, divisions: u32) -> u32 {
    let scene = scene_ref!(scene, 0);
    scene.add_geometry(primitives::grid(size, divisions))
}

// ------------------------------------------------------------------ material

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_material_create(scene: *mut Scene, desc: *const TnMaterialDesc) -> u32 {
    let scene = scene_ref!(scene, 0);
    let mut material = Material::default();
    if let Some(desc) = unsafe { desc.as_ref() } {
        desc.apply(&mut material);
    }
    scene.add_material(material)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_material_update(
    scene: *mut Scene,
    material: u32,
    desc: *const TnMaterialDesc,
) -> i32 {
    let scene = scene_ref!(scene);
    let Some(desc) = (unsafe { desc.as_ref() }) else {
        set_last_error("material descriptor is null");
        return status::NULL_POINTER;
    };
    match scene.material_mut(material) {
        Some(material) => {
            desc.apply(material);
            status::OK
        }
        None => {
            set_last_error("invalid material handle");
            status::INVALID_HANDLE
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_material_get(
    scene: *mut Scene,
    material: u32,
    out_desc: *mut TnMaterialDesc,
) -> i32 {
    let scene = scene_ref!(scene);
    if out_desc.is_null() {
        set_last_error("output pointer is null");
        return status::NULL_POINTER;
    }
    match scene.material(material) {
        Some(material) => {
            unsafe { *out_desc = material.clone().into() };
            status::OK
        }
        None => {
            set_last_error("invalid material handle");
            status::INVALID_HANDLE
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_material_destroy(scene: *mut Scene, material: u32) -> i32 {
    let scene = scene_ref!(scene);
    if scene.remove_material(material) {
        status::OK
    } else {
        set_last_error("invalid material handle");
        status::INVALID_HANDLE
    }
}

// ------------------------------------------------------------------- texture

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_texture_create(
    scene: *mut Scene,
    width: u32,
    height: u32,
    format: u32,
    pixels: *const u8,
    length: u32,
) -> u32 {
    let scene = scene_ref!(scene, 0);
    if pixels.is_null() {
        set_last_error("pixel buffer is null");
        return 0;
    }
    let data = unsafe { std::slice::from_raw_parts(pixels, length as usize) }.to_vec();
    match Texture::new(width, height, TextureFormat::from_u32(format), data) {
        Ok(texture) => scene.add_texture(texture),
        Err(error) => {
            fail(error);
            0
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_texture_load_file(
    scene: *mut Scene,
    path: *const c_char,
    srgb: i32,
) -> u32 {
    let scene = scene_ref!(scene, 0);
    let Some(path) = (unsafe { str_from_ptr(path) }) else {
        set_last_error("path is null or not valid UTF-8");
        return 0;
    };
    match Texture::from_file(path, srgb != 0) {
        Ok(texture) => scene.add_texture(texture),
        Err(error) => {
            fail(error);
            0
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_texture_load_memory(
    scene: *mut Scene,
    bytes: *const u8,
    length: u32,
    srgb: i32,
) -> u32 {
    let scene = scene_ref!(scene, 0);
    if bytes.is_null() || length == 0 {
        set_last_error("image buffer is null or empty");
        return 0;
    }
    let data = unsafe { std::slice::from_raw_parts(bytes, length as usize) };
    match Texture::from_encoded_bytes(data, srgb != 0) {
        Ok(texture) => scene.add_texture(texture),
        Err(error) => {
            fail(error);
            0
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_texture_set_sampler(
    scene: *mut Scene,
    texture: u32,
    desc: *const TnSamplerDesc,
) -> i32 {
    let scene = scene_ref!(scene);
    let Some(desc) = (unsafe { desc.as_ref() }) else {
        set_last_error("sampler descriptor is null");
        return status::NULL_POINTER;
    };
    match scene.texture_mut(texture) {
        Some(texture) => {
            texture.sampler = (*desc).into();
            texture.touch();
            status::OK
        }
        None => {
            set_last_error("invalid texture handle");
            status::INVALID_HANDLE
        }
    }
}

// ------------------------------------------------------------------ shaders

/// Creates a custom shader from WGSL (`language` 0) or GLSL (1) hook
/// functions. Returns 0 and sets the last error (with the compiler message)
/// when the source does not translate or validate.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_shader_create(
    scene: *mut Scene,
    language: u32,
    source: *const c_char,
    name: *const c_char,
) -> u32 {
    let scene = scene_ref!(scene, 0);
    let Some(source) = (unsafe { str_from_ptr(source) }) else {
        set_last_error("shader source is null or not valid UTF-8");
        return 0;
    };
    let name = unsafe { str_from_ptr(name) }.unwrap_or("custom");
    match CustomShader::new(name, ShaderLanguage::from_u32(language), source) {
        Ok(shader) => scene.add_shader(shader),
        Err(error) => {
            fail(error);
            0
        }
    }
}

/// Replaces the source of a shader; every material using it switches on the
/// next frame. On error the previous version stays active.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_shader_update(
    scene: *mut Scene,
    shader: u32,
    language: u32,
    source: *const c_char,
) -> i32 {
    let scene = scene_ref!(scene);
    let Some(source) = (unsafe { str_from_ptr(source) }) else {
        set_last_error("shader source is null or not valid UTF-8");
        return status::NULL_POINTER;
    };
    let name = match scene.shader(shader) {
        Some(existing) => existing.name.clone(),
        None => {
            set_last_error("invalid shader handle");
            return status::INVALID_HANDLE;
        }
    };
    match CustomShader::new(name, ShaderLanguage::from_u32(language), source) {
        Ok(compiled) => {
            scene.replace_shader(shader, compiled);
            status::OK
        }
        Err(error) => fail(error),
    }
}

/// Copies the WGSL hooks a shader compiled to (useful to inspect GLSL output).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_shader_get_wgsl(
    scene: *mut Scene,
    shader: u32,
    buffer: *mut c_char,
    capacity: i32,
) -> i32 {
    let scene = scene_ref!(scene);
    match scene.shader(shader) {
        Some(existing) => unsafe { copy_string(&existing.hooks, buffer, capacity) },
        None => {
            set_last_error("invalid shader handle");
            status::INVALID_HANDLE
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_shader_destroy(scene: *mut Scene, shader: u32) -> i32 {
    let scene = scene_ref!(scene);
    if scene.remove_shader(shader) {
        status::OK
    } else {
        set_last_error("invalid shader handle");
        status::INVALID_HANDLE
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_texture_destroy(scene: *mut Scene, texture: u32) -> i32 {
    let scene = scene_ref!(scene);
    if scene.remove_texture(texture) {
        status::OK
    } else {
        set_last_error("invalid texture handle");
        status::INVALID_HANDLE
    }
}

// ------------------------------------------------------------------ renderer

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_renderer_create_offscreen(
    desc: *const TnRendererDesc,
) -> *mut Renderer {
    let Some(desc) = (unsafe { desc.as_ref() }) else {
        set_last_error("renderer descriptor is null");
        return std::ptr::null_mut();
    };
    match Renderer::new_offscreen((*desc).into()) {
        Ok(renderer) => Box::into_raw(Box::new(renderer)),
        Err(error) => {
            fail(error);
            std::ptr::null_mut()
        }
    }
}

/// Creates a renderer for a Win32 window (`hwnd` from Avalonia, WPF, WinForms).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_renderer_create_win32(
    hwnd: *mut c_void,
    hinstance: *mut c_void,
    desc: *const TnRendererDesc,
) -> *mut Renderer {
    #[cfg(target_os = "windows")]
    {
        use raw_window_handle::{
            RawDisplayHandle, RawWindowHandle, Win32WindowHandle, WindowsDisplayHandle,
        };
        let Some(desc) = (unsafe { desc.as_ref() }) else {
            set_last_error("renderer descriptor is null");
            return std::ptr::null_mut();
        };
        let Some(handle) = std::num::NonZeroIsize::new(hwnd as isize) else {
            set_last_error("hwnd is null");
            return std::ptr::null_mut();
        };
        let mut window = Win32WindowHandle::new(handle);
        window.hinstance = std::num::NonZeroIsize::new(hinstance as isize);
        let display = RawDisplayHandle::Windows(WindowsDisplayHandle::new());
        match unsafe {
            Renderer::new_with_raw_handles(display, RawWindowHandle::Win32(window), (*desc).into())
        } {
            Ok(renderer) => Box::into_raw(Box::new(renderer)),
            Err(error) => {
                fail(error);
                std::ptr::null_mut()
            }
        }
    }
    #[cfg(not(target_os = "windows"))]
    {
        let _ = (hwnd, hinstance, desc);
        set_last_error("tn_renderer_create_win32 is only available on Windows");
        std::ptr::null_mut()
    }
}

/// Creates a renderer for an X11 window.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_renderer_create_xlib(
    window: u64,
    display: *mut c_void,
    screen: i32,
    desc: *const TnRendererDesc,
) -> *mut Renderer {
    #[cfg(all(unix, not(target_os = "macos"), not(target_os = "android")))]
    {
        use raw_window_handle::{
            RawDisplayHandle, RawWindowHandle, XlibDisplayHandle, XlibWindowHandle,
        };
        let Some(desc) = (unsafe { desc.as_ref() }) else {
            set_last_error("renderer descriptor is null");
            return std::ptr::null_mut();
        };
        let window_handle = XlibWindowHandle::new(window);
        let display_handle = XlibDisplayHandle::new(std::ptr::NonNull::new(display), screen);
        match unsafe {
            Renderer::new_with_raw_handles(
                RawDisplayHandle::Xlib(display_handle),
                RawWindowHandle::Xlib(window_handle),
                (*desc).into(),
            )
        } {
            Ok(renderer) => Box::into_raw(Box::new(renderer)),
            Err(error) => {
                fail(error);
                std::ptr::null_mut()
            }
        }
    }
    #[cfg(not(all(unix, not(target_os = "macos"), not(target_os = "android"))))]
    {
        let _ = (window, display, screen, desc);
        set_last_error("tn_renderer_create_xlib is only available on X11 systems");
        std::ptr::null_mut()
    }
}

/// Creates a renderer for a macOS `NSView` / `CAMetalLayer` host view.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_renderer_create_appkit(
    ns_view: *mut c_void,
    desc: *const TnRendererDesc,
) -> *mut Renderer {
    #[cfg(target_os = "macos")]
    {
        use raw_window_handle::{
            AppKitDisplayHandle, AppKitWindowHandle, RawDisplayHandle, RawWindowHandle,
        };
        let Some(desc) = (unsafe { desc.as_ref() }) else {
            set_last_error("renderer descriptor is null");
            return std::ptr::null_mut();
        };
        let Some(view) = std::ptr::NonNull::new(ns_view) else {
            set_last_error("ns_view is null");
            return std::ptr::null_mut();
        };
        match unsafe {
            Renderer::new_with_raw_handles(
                RawDisplayHandle::AppKit(AppKitDisplayHandle::new()),
                RawWindowHandle::AppKit(AppKitWindowHandle::new(view)),
                (*desc).into(),
            )
        } {
            Ok(renderer) => Box::into_raw(Box::new(renderer)),
            Err(error) => {
                fail(error);
                std::ptr::null_mut()
            }
        }
    }
    #[cfg(not(target_os = "macos"))]
    {
        let _ = (ns_view, desc);
        set_last_error("tn_renderer_create_appkit is only available on macOS");
        std::ptr::null_mut()
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_renderer_destroy(renderer: *mut Renderer) {
    if !renderer.is_null() {
        drop(unsafe { Box::from_raw(renderer) });
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_renderer_resize(
    renderer: *mut Renderer,
    width: u32,
    height: u32,
) -> i32 {
    let renderer = renderer_ref!(renderer);
    renderer.resize(width, height);
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_renderer_set_config(
    renderer: *mut Renderer,
    desc: *const TnRendererDesc,
) -> i32 {
    let renderer = renderer_ref!(renderer);
    let Some(desc) = (unsafe { desc.as_ref() }) else {
        set_last_error("renderer descriptor is null");
        return status::NULL_POINTER;
    };
    renderer.set_config((*desc).into());
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_renderer_render(
    renderer: *mut Renderer,
    scene: *mut Scene,
    camera_node: u32,
) -> i32 {
    let renderer = renderer_ref!(renderer);
    let scene = scene_ref!(scene);
    match renderer.render(scene, (camera_node != 0).then_some(camera_node)) {
        Ok(()) => status::OK,
        Err(error) => fail(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_renderer_read_pixels(
    renderer: *mut Renderer,
    buffer: *mut u8,
    capacity: u32,
) -> i32 {
    let renderer = renderer_ref!(renderer);
    let required = (renderer.width() * renderer.height() * 4) as i32;
    if buffer.is_null() {
        return required;
    }
    if (capacity as i32) < required {
        set_last_error("pixel buffer is too small");
        return status::BUFFER_TOO_SMALL;
    }
    match renderer.read_pixels() {
        Ok(pixels) => {
            unsafe { std::ptr::copy_nonoverlapping(pixels.as_ptr(), buffer, pixels.len()) };
            pixels.len() as i32
        }
        Err(error) => fail(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_renderer_get_stats(
    renderer: *mut Renderer,
    out_stats: *mut TnFrameStats,
) -> i32 {
    let renderer = renderer_ref!(renderer);
    if out_stats.is_null() {
        set_last_error("output pointer is null");
        return status::NULL_POINTER;
    }
    let stats = renderer.stats();
    unsafe {
        *out_stats = TnFrameStats {
            draw_calls: stats.draw_calls,
            triangles: stats.triangles,
            visible_nodes: stats.visible_nodes,
            culled_nodes: stats.culled_nodes,
            lights: stats.lights,
            cpu_time_ms: stats.cpu_time_ms,
            shadow_layers: stats.shadow_layers,
            shadow_draw_calls: stats.shadow_draw_calls,
        }
    };
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_renderer_get_adapter_name(
    renderer: *mut Renderer,
    buffer: *mut c_char,
    capacity: i32,
) -> i32 {
    let renderer = renderer_ref!(renderer);
    unsafe { copy_string(&renderer.adapter_name(), buffer, capacity) }
}

// ------------------------------------------------------------------- loaders

unsafe fn write_import_result(out: *mut TnImportResult, result: &crate::loaders::ImportResult) {
    if out.is_null() {
        return;
    }
    unsafe {
        *out = TnImportResult {
            root: result.root,
            node_count: result.nodes.len() as u32,
            geometry_count: result.geometries.len() as u32,
            material_count: result.materials.len() as u32,
            texture_count: result.textures.len() as u32,
            animation_count: result.animations.len() as u32,
            skin_count: result.skins.len() as u32,
        }
    };
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_load_gltf(
    scene: *mut Scene,
    path: *const c_char,
    parent: u32,
    out_result: *mut TnImportResult,
) -> i32 {
    let scene = scene_ref!(scene);
    let Some(path) = (unsafe { str_from_ptr(path) }) else {
        set_last_error("path is null or not valid UTF-8");
        return status::INVALID_ARGUMENT;
    };
    match crate::loaders::load_gltf(scene, path, (parent != 0).then_some(parent)) {
        Ok(result) => {
            unsafe { write_import_result(out_result, &result) };
            status::OK
        }
        Err(error) => fail(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_load_gltf_memory(
    scene: *mut Scene,
    bytes: *const u8,
    length: u32,
    parent: u32,
    out_result: *mut TnImportResult,
) -> i32 {
    let scene = scene_ref!(scene);
    if bytes.is_null() || length == 0 {
        set_last_error("glTF buffer is null or empty");
        return status::INVALID_ARGUMENT;
    }
    let data = unsafe { std::slice::from_raw_parts(bytes, length as usize) };
    match crate::loaders::load_gltf_from_slice(scene, data, (parent != 0).then_some(parent)) {
        Ok(result) => {
            unsafe { write_import_result(out_result, &result) };
            status::OK
        }
        Err(error) => fail(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_load_obj(
    scene: *mut Scene,
    path: *const c_char,
    parent: u32,
    out_result: *mut TnImportResult,
) -> i32 {
    let scene = scene_ref!(scene);
    let Some(path) = (unsafe { str_from_ptr(path) }) else {
        set_last_error("path is null or not valid UTF-8");
        return status::INVALID_ARGUMENT;
    };
    match crate::loaders::load_obj(scene, path, (parent != 0).then_some(parent)) {
        Ok(result) => {
            unsafe { write_import_result(out_result, &result) };
            status::OK
        }
        Err(error) => fail(error),
    }
}

// ------------------------------------------------------------------ raycasting

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct TnRaycastOptions {
    pub max_distance: f32,
    pub layers: u32,
    pub visible_only: i32,
    pub include_back_faces: i32,
}

/// Casts a ray and writes up to `max_hits` results, nearest first.
/// Returns the number of hits written, or a negative status code.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_raycast(
    scene: *mut Scene,
    origin: TnVec3,
    direction: TnVec3,
    options: *const TnRaycastOptions,
    out_hits: *mut TnRayHit,
    max_hits: u32,
) -> i32 {
    let scene = scene_ref!(scene);
    let options = match unsafe { options.as_ref() } {
        Some(options) => RaycastOptions {
            max_distance: if options.max_distance > 0.0 {
                options.max_distance
            } else {
                f32::INFINITY
            },
            layers: options.layers,
            visible_only: options.visible_only != 0,
            include_back_faces: options.include_back_faces != 0,
        },
        None => RaycastOptions::default(),
    };
    let ray = Ray::new(origin.into(), direction.into());
    let hits = raycast(scene, &ray, &options);
    if out_hits.is_null() {
        return hits.len() as i32;
    }
    let count = hits.len().min(max_hits as usize);
    for (index, hit) in hits.iter().take(count).enumerate() {
        unsafe {
            *out_hits.add(index) = TnRayHit {
                node: hit.node,
                distance: hit.distance,
                point: hit.point.into(),
                normal: hit.normal.into(),
                triangle: hit.triangle,
                barycentric: hit.barycentric.into(),
            }
        };
    }
    count as i32
}

/// Builds a world space picking ray from normalised device coordinates
/// (`-1..1`, `y` up) for the given camera node.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_camera_ray(
    scene: *mut Scene,
    camera_node: u32,
    ndc_x: f32,
    ndc_y: f32,
    aspect: f32,
    out_origin: *mut TnVec3,
    out_direction: *mut TnVec3,
) -> i32 {
    let scene = scene_ref!(scene);
    if out_origin.is_null() || out_direction.is_null() {
        set_last_error("output pointer is null");
        return status::NULL_POINTER;
    }
    let Some(camera) = scene.node(camera_node).and_then(|n| n.camera) else {
        set_last_error("the node has no camera");
        return status::INVALID_HANDLE;
    };
    let Some(world) = scene.world_matrix(camera_node) else {
        set_last_error("invalid camera node");
        return status::INVALID_HANDLE;
    };
    let aspect = if aspect > 0.0 { aspect } else { 1.0 };
    let origin = camera.ray_origin((ndc_x, ndc_y), &world, aspect);
    let direction = camera.ray_direction((ndc_x, ndc_y), &world, aspect);
    unsafe {
        *out_origin = origin.into();
        *out_direction = direction.into();
    }
    status::OK
}

// ------------------------------------------------------------------- windowing

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct TnWindowDesc {
    pub title: *const c_char,
    pub width: u32,
    pub height: u32,
    pub resizable: i32,
    pub decorations: i32,
    pub renderer: TnRendererDesc,
}

/// Callbacks invoked from the render loop. On the managed side these are
/// `[UnmanagedCallersOnly]` static methods.
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct TnAppCallbacks {
    pub user_data: *mut c_void,
    pub on_init: Option<extern "C" fn(*mut c_void, *mut Renderer)>,
    pub on_frame: Option<extern "C" fn(*mut c_void, *mut Renderer, f32)>,
    pub on_event: Option<extern "C" fn(*mut c_void, *mut Renderer, *const InputEvent)>,
    /// Return non-zero to let the window close.
    pub on_close: Option<extern "C" fn(*mut c_void) -> i32>,
}

struct CallbackHandler {
    callbacks: TnAppCallbacks,
}

impl AppHandler for CallbackHandler {
    fn on_init(&mut self, renderer: &mut Renderer) {
        if let Some(callback) = self.callbacks.on_init {
            callback(self.callbacks.user_data, renderer as *mut Renderer);
        }
    }

    fn on_frame(&mut self, renderer: &mut Renderer, delta_seconds: f32) {
        if let Some(callback) = self.callbacks.on_frame {
            callback(
                self.callbacks.user_data,
                renderer as *mut Renderer,
                delta_seconds,
            );
        }
    }

    fn on_event(&mut self, renderer: &mut Renderer, event: &InputEvent) {
        if let Some(callback) = self.callbacks.on_event {
            callback(
                self.callbacks.user_data,
                renderer as *mut Renderer,
                event as *const InputEvent,
            );
        }
    }

    fn on_close(&mut self) -> bool {
        match self.callbacks.on_close {
            Some(callback) => callback(self.callbacks.user_data) != 0,
            None => true,
        }
    }
}

/// Opens a window and runs the render loop until it closes. Blocks the calling
/// thread, which must be the process main thread on Windows and macOS.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_app_run(desc: *const TnWindowDesc, callbacks: TnAppCallbacks) -> i32 {
    let Some(desc) = (unsafe { desc.as_ref() }) else {
        set_last_error("window descriptor is null");
        return status::NULL_POINTER;
    };
    let config = WindowConfig {
        title: unsafe { str_from_ptr(desc.title) }
            .unwrap_or("Three.Net")
            .to_string(),
        width: desc.width.max(1),
        height: desc.height.max(1),
        resizable: desc.resizable != 0,
        decorations: desc.decorations != 0,
        renderer: desc.renderer.into(),
    };
    match crate::window::run_app(config, CallbackHandler { callbacks }) {
        Ok(()) => status::OK,
        Err(error) => fail(error),
    }
}

// ---------------------------------------------------------------- animation

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct TnPlayerDesc {
    pub time: f32,
    pub speed: f32,
    pub weight: f32,
    pub looping: i32,
    pub playing: i32,
}

impl From<AnimationPlayer> for TnPlayerDesc {
    fn from(value: AnimationPlayer) -> Self {
        Self {
            time: value.time,
            speed: value.speed,
            weight: value.weight,
            looping: i32::from(value.looping),
            playing: i32::from(value.playing),
        }
    }
}

impl TnPlayerDesc {
    fn apply(&self, player: &mut AnimationPlayer) {
        player.time = self.time;
        player.speed = self.speed;
        player.weight = self.weight.clamp(0.0, 1.0);
        player.looping = self.looping != 0;
        player.playing = self.playing != 0;
    }
}

/// Copies up to `capacity` animation clip ids into `out_ids`; returns the total count.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_scene_get_animations(scene: *mut Scene, out_ids: *mut u32, capacity: u32) -> i32 {
    let scene = scene_ref!(scene);
    let ids = scene.animation_ids();
    if !out_ids.is_null() {
        let count = ids.len().min(capacity as usize);
        unsafe { std::ptr::copy_nonoverlapping(ids.as_ptr(), out_ids, count) };
    }
    ids.len() as i32
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_animation_get_name(scene: *mut Scene, clip: u32, buffer: *mut c_char, capacity: i32) -> i32 {
    let scene = scene_ref!(scene);
    match scene.animation(clip) {
        Some(animation) => unsafe { copy_string(&animation.name, buffer, capacity) },
        None => {
            set_last_error("invalid animation handle");
            status::INVALID_HANDLE
        }
    }
}

/// Duration in seconds (last key time of any channel), or a negative status.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_animation_get_duration(scene: *mut Scene, clip: u32, out_duration: *mut f32) -> i32 {
    let scene = scene_ref!(scene);
    if out_duration.is_null() {
        set_last_error("output pointer is null");
        return status::NULL_POINTER;
    }
    match scene.animation(clip) {
        Some(animation) => {
            unsafe { *out_duration = animation.duration() };
            status::OK
        }
        None => {
            set_last_error("invalid animation handle");
            status::INVALID_HANDLE
        }
    }
}

/// Creates an empty clip that channels can be added to.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_animation_create(scene: *mut Scene, name: *const c_char) -> u32 {
    let scene = scene_ref!(scene, 0);
    let name = unsafe { str_from_ptr(name) }.unwrap_or("animation").to_string();
    scene.add_animation(AnimationClip { name, channels: Vec::new() })
}

/// Adds a keyframe channel. `path`: 0 translation, 1 rotation (quaternion
/// xyzw), 2 scale. `interpolation`: 0 step, 1 linear, 2 cubic spline (values
/// then hold in tangent, value, out tangent per key).
#[unsafe(no_mangle)]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn tn_animation_add_channel(
    scene: *mut Scene,
    clip: u32,
    node: u32,
    path: u32,
    interpolation: u32,
    times: *const f32,
    key_count: u32,
    values: *const f32,
    value_count: u32,
) -> i32 {
    let scene = scene_ref!(scene);
    if times.is_null() || values.is_null() || key_count == 0 {
        set_last_error("keyframe arrays are null or empty");
        return status::NULL_POINTER;
    }
    if scene.node(node).is_none() {
        set_last_error("invalid node handle");
        return status::INVALID_HANDLE;
    }
    let path = TargetPath::from_u32(path);
    let interpolation = Interpolation::from_u32(interpolation);
    let per_key = path.components() * if interpolation == Interpolation::CubicSpline { 3 } else { 1 };
    if value_count as usize != key_count as usize * per_key {
        set_last_error(&format!("expected {} values for {key_count} keys, got {value_count}", key_count as usize * per_key));
        return status::INVALID_ARGUMENT;
    }
    let times = unsafe { std::slice::from_raw_parts(times, key_count as usize) }.to_vec();
    if times.windows(2).any(|w| w[1] < w[0]) {
        set_last_error("key times must be ascending");
        return status::INVALID_ARGUMENT;
    }
    let values = unsafe { std::slice::from_raw_parts(values, value_count as usize) }.to_vec();
    match scene.animation_mut(clip) {
        Some(animation) => {
            animation.channels.push(Channel { target: node, path, interpolation, times, values });
            status::OK
        }
        None => {
            set_last_error("invalid animation handle");
            status::INVALID_HANDLE
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_animation_destroy(scene: *mut Scene, clip: u32) -> i32 {
    let scene = scene_ref!(scene);
    if scene.remove_animation(clip) {
        status::OK
    } else {
        set_last_error("invalid animation handle");
        status::INVALID_HANDLE
    }
}

/// Starts playing a clip; returns the player id (0 on failure).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_animation_play(scene: *mut Scene, clip: u32, desc: *const TnPlayerDesc) -> u32 {
    let scene = scene_ref!(scene, 0);
    let mut player = AnimationPlayer::new(clip);
    if let Some(desc) = unsafe { desc.as_ref() } {
        desc.apply(&mut player);
    }
    match scene.play_animation(player) {
        Some(id) => id,
        None => {
            set_last_error("invalid animation handle");
            0
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_player_get(scene: *mut Scene, player: u32, out_desc: *mut TnPlayerDesc) -> i32 {
    let scene = scene_ref!(scene);
    if out_desc.is_null() {
        set_last_error("output pointer is null");
        return status::NULL_POINTER;
    }
    match scene.player(player) {
        Some(existing) => {
            unsafe { *out_desc = (*existing).into() };
            status::OK
        }
        None => {
            set_last_error("invalid player handle");
            status::INVALID_HANDLE
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_player_set(scene: *mut Scene, player: u32, desc: *const TnPlayerDesc) -> i32 {
    let scene = scene_ref!(scene);
    let Some(desc) = (unsafe { desc.as_ref() }) else {
        set_last_error("player descriptor is null");
        return status::NULL_POINTER;
    };
    match scene.player_mut(player) {
        Some(existing) => {
            desc.apply(existing);
            status::OK
        }
        None => {
            set_last_error("invalid player handle");
            status::INVALID_HANDLE
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_player_stop(scene: *mut Scene, player: u32) -> i32 {
    let scene = scene_ref!(scene);
    if scene.stop_animation(player) {
        status::OK
    } else {
        set_last_error("invalid player handle");
        status::INVALID_HANDLE
    }
}

/// Advances every animation player by `delta` seconds and deforms skinned meshes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_scene_update_animations(scene: *mut Scene, delta: f32) -> i32 {
    let scene = scene_ref!(scene);
    scene.update_animations(delta);
    status::OK
}

// ---------------------------------------------------------------------- FBX

/// Imports a binary or ASCII FBX file (meshes, materials, textures, skeletons
/// and animation stacks).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_load_fbx(
    scene: *mut Scene,
    path: *const c_char,
    parent: u32,
    out_result: *mut TnImportResult,
) -> i32 {
    let scene = scene_ref!(scene);
    let Some(path) = (unsafe { str_from_ptr(path) }) else {
        set_last_error("path is null or not valid UTF-8");
        return status::INVALID_ARGUMENT;
    };
    match crate::fbx::load_fbx(scene, path, (parent != 0).then_some(parent)) {
        Ok(result) => {
            unsafe { write_import_result(out_result, &result) };
            status::OK
        }
        Err(error) => fail(error),
    }
}

/// Imports FBX from memory; only embedded textures can be resolved.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_load_fbx_memory(
    scene: *mut Scene,
    bytes: *const u8,
    length: u32,
    parent: u32,
    out_result: *mut TnImportResult,
) -> i32 {
    let scene = scene_ref!(scene);
    if bytes.is_null() || length == 0 {
        set_last_error("FBX buffer is null or empty");
        return status::INVALID_ARGUMENT;
    }
    let data = unsafe { std::slice::from_raw_parts(bytes, length as usize) };
    match crate::fbx::load_fbx_from_slice(scene, data, (parent != 0).then_some(parent), "fbx", None) {
        Ok(result) => {
            unsafe { write_import_result(out_result, &result) };
            status::OK
        }
        Err(error) => fail(error),
    }
}

// ------------------------------------------------------- streaming and cache

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct TnAssetStats {
    pub pending_textures: u32,
    pub cached_textures: u32,
    pub cached_models: u32,
    pub _padding: u32,
    pub cache_hits: u64,
    pub cache_misses: u64,
    pub streamed_textures: u64,
}

/// Starts loading an image in the background; the returned texture shows a
/// placeholder until the renderer (or `tn_scene_poll_streaming`) swaps it in.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_texture_load_async(scene: *mut Scene, path: *const c_char, srgb: i32) -> u32 {
    let scene = scene_ref!(scene, 0);
    let Some(path) = (unsafe { str_from_ptr(path) }) else {
        set_last_error("path is null or not valid UTF-8");
        return 0;
    };
    match scene.load_texture_async(path, srgb != 0) {
        Ok(id) => id,
        Err(error) => {
            fail(error);
            0
        }
    }
}

/// Loads an image once per path and colour space.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_texture_load_cached(scene: *mut Scene, path: *const c_char, srgb: i32) -> u32 {
    let scene = scene_ref!(scene, 0);
    let Some(path) = (unsafe { str_from_ptr(path) }) else {
        set_last_error("path is null or not valid UTF-8");
        return 0;
    };
    match scene.load_texture_cached(path, srgb != 0) {
        Ok(id) => id,
        Err(error) => {
            fail(error);
            0
        }
    }
}

/// Returns the streaming state (0 missing, 1 loading, 2 ready, 3 failed).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_texture_get_state(scene: *mut Scene, texture: u32) -> i32 {
    let scene = scene_ref!(scene);
    scene.texture_state(texture) as i32
}

/// Copies the error of a failed streamed texture (empty when none).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_texture_get_error(
    scene: *mut Scene,
    texture: u32,
    buffer: *mut c_char,
    capacity: i32,
) -> i32 {
    let scene = scene_ref!(scene);
    let message = scene.texture_error(texture).unwrap_or("");
    unsafe { copy_string(message, buffer, capacity) }
}

/// Applies finished background loads; returns how many were applied.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_scene_poll_streaming(scene: *mut Scene) -> i32 {
    let scene = scene_ref!(scene);
    scene.poll_streaming() as i32
}

/// Blocks until all streamed textures are applied; returns 1 when done, 0 on timeout.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_scene_finish_streaming(scene: *mut Scene, timeout_ms: u32) -> i32 {
    let scene = scene_ref!(scene);
    scene.finish_streaming(std::time::Duration::from_millis(timeout_ms as u64)) as i32
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_scene_set_streaming_budget(scene: *mut Scene, uploads_per_frame: u32) -> i32 {
    let scene = scene_ref!(scene);
    scene.set_streaming_uploads_per_frame(uploads_per_frame as usize);
    status::OK
}

/// Places a model, importing the file only once (animated models excepted).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_load_model_cached(
    scene: *mut Scene,
    path: *const c_char,
    parent: u32,
    out_result: *mut TnImportResult,
) -> i32 {
    let scene = scene_ref!(scene);
    let Some(path) = (unsafe { str_from_ptr(path) }) else {
        set_last_error("path is null or not valid UTF-8");
        return status::INVALID_ARGUMENT;
    };
    match scene.load_model_cached(path, (parent != 0).then_some(parent)) {
        Ok(result) => {
            unsafe { write_import_result(out_result, &result) };
            status::OK
        }
        Err(error) => fail(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_scene_get_asset_stats(scene: *mut Scene, out_stats: *mut TnAssetStats) -> i32 {
    let scene = scene_ref!(scene);
    if out_stats.is_null() {
        set_last_error("stats pointer is null");
        return status::NULL_POINTER;
    }
    let stats = scene.asset_stats();
    unsafe {
        *out_stats = TnAssetStats {
            pending_textures: stats.pending_textures,
            cached_textures: stats.cached_textures,
            cached_models: stats.cached_models,
            _padding: 0,
            cache_hits: stats.cache_hits,
            cache_misses: stats.cache_misses,
            streamed_textures: stats.streamed_textures,
        };
    }
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_scene_clear_asset_cache(scene: *mut Scene) -> i32 {
    let scene = scene_ref!(scene);
    scene.clear_asset_cache();
    status::OK
}

// ------------------------------------------------------------------ overlay

/// Overlay element description; `kind` 0 = panel, 1 = image, 2 = text.
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct TnOverlayElement {
    pub kind: u32,
    pub parent: u32,
    pub anchor: u32,
    pub layer: i32,
    pub offset: [f32; 2],
    pub size: [f32; 2],
    pub color: [f32; 4],
    pub border_color: [f32; 4],
    pub border_width: f32,
    pub corner_radius: f32,
    pub visible: i32,
    pub interactive: i32,
    pub texture: u32,
    pub uv: [f32; 4],
    pub font: u32,
    pub font_size: f32,
    pub align: u32,
    pub vertical_align: u32,
    pub wrap: i32,
}

fn overlay_from_desc(desc: &TnOverlayElement, text: Option<&str>) -> crate::overlay::OverlayElement {
    use crate::overlay::{Anchor, OverlayContent, OverlayElement, TextAlign};
    let content = match desc.kind {
        1 => OverlayContent::Image { texture: desc.texture, uv: desc.uv },
        2 => OverlayContent::Text {
            text: text.unwrap_or_default().to_string(),
            font: desc.font,
            size: if desc.font_size > 0.0 { desc.font_size } else { 16.0 },
            align: TextAlign::from_u32(desc.align),
            vertical_align: TextAlign::from_u32(desc.vertical_align),
            wrap: desc.wrap != 0,
        },
        _ => OverlayContent::Panel,
    };
    OverlayElement {
        content,
        parent: (desc.parent != 0).then_some(desc.parent),
        anchor: Anchor::from_u32(desc.anchor),
        offset: desc.offset,
        size: desc.size,
        color: desc.color,
        border_color: desc.border_color,
        border_width: desc.border_width,
        corner_radius: desc.corner_radius,
        layer: desc.layer,
        visible: desc.visible != 0,
        interactive: desc.interactive != 0,
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_overlay_add(scene: *mut Scene, desc: *const TnOverlayElement, text: *const c_char) -> u32 {
    let scene = scene_ref!(scene, 0);
    let Some(desc) = (unsafe { desc.as_ref() }) else {
        set_last_error("overlay descriptor is null");
        return 0;
    };
    let text = unsafe { str_from_ptr(text) };
    match scene.overlay.add(overlay_from_desc(desc, text)) {
        Ok(id) => id,
        Err(error) => {
            fail(error);
            0
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_overlay_update(scene: *mut Scene, id: u32, desc: *const TnOverlayElement, text: *const c_char) -> i32 {
    let scene = scene_ref!(scene);
    let Some(desc) = (unsafe { desc.as_ref() }) else {
        set_last_error("overlay descriptor is null");
        return status::NULL_POINTER;
    };
    let text = unsafe { str_from_ptr(text) };
    match scene.overlay.update(id, overlay_from_desc(desc, text)) {
        Ok(()) => status::OK,
        Err(error) => fail(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_overlay_get(scene: *mut Scene, id: u32, out: *mut TnOverlayElement) -> i32 {
    use crate::overlay::OverlayContent;
    let scene = scene_ref!(scene);
    let Some(element) = scene.overlay.get(id) else {
        set_last_error("invalid overlay element");
        return status::INVALID_HANDLE;
    };
    let Some(out) = (unsafe { out.as_mut() }) else {
        set_last_error("output pointer is null");
        return status::NULL_POINTER;
    };
    let mut desc = TnOverlayElement {
        kind: 0,
        parent: element.parent.unwrap_or(0),
        anchor: element.anchor as u32,
        layer: element.layer,
        offset: element.offset,
        size: element.size,
        color: element.color,
        border_color: element.border_color,
        border_width: element.border_width,
        corner_radius: element.corner_radius,
        visible: element.visible as i32,
        interactive: element.interactive as i32,
        texture: 0,
        uv: [0.0, 0.0, 1.0, 1.0],
        font: 0,
        font_size: 16.0,
        align: 0,
        vertical_align: 0,
        wrap: 0,
    };
    match &element.content {
        OverlayContent::Panel => {}
        OverlayContent::Image { texture, uv } => {
            desc.kind = 1;
            desc.texture = *texture;
            desc.uv = *uv;
        }
        OverlayContent::Text { font, size, align, vertical_align, wrap, .. } => {
            desc.kind = 2;
            desc.font = *font;
            desc.font_size = *size;
            desc.align = *align as u32;
            desc.vertical_align = *vertical_align as u32;
            desc.wrap = *wrap as i32;
        }
    }
    *out = desc;
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_overlay_get_text(scene: *mut Scene, id: u32, buffer: *mut c_char, capacity: i32) -> i32 {
    use crate::overlay::OverlayContent;
    let scene = scene_ref!(scene);
    match scene.overlay.get(id) {
        Some(element) => {
            let text = match &element.content {
                OverlayContent::Text { text, .. } => text.as_str(),
                _ => "",
            };
            unsafe { copy_string(text, buffer, capacity) }
        }
        None => {
            set_last_error("invalid overlay element");
            status::INVALID_HANDLE
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_overlay_remove(scene: *mut Scene, id: u32) -> i32 {
    let scene = scene_ref!(scene);
    if scene.overlay.remove(id) {
        status::OK
    } else {
        set_last_error("invalid overlay element");
        status::INVALID_HANDLE
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_overlay_clear(scene: *mut Scene) -> i32 {
    let scene = scene_ref!(scene);
    scene.overlay.clear();
    status::OK
}

/// Topmost interactive element at a pixel of a target of the given size (0 = none).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_overlay_hit_test(scene: *mut Scene, x: f32, y: f32, width: f32, height: f32) -> u32 {
    let scene = scene_ref!(scene, 0);
    scene.overlay.hit_test(x, y, width, height).unwrap_or(0)
}

/// Screen rectangle (x, y, width, height) of an element; zero size when hidden.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_overlay_get_rect(scene: *mut Scene, id: u32, width: f32, height: f32, out: *mut [f32; 4]) -> i32 {
    let scene = scene_ref!(scene);
    if scene.overlay.get(id).is_none() {
        set_last_error("invalid overlay element");
        return status::INVALID_HANDLE;
    }
    let Some(out) = (unsafe { out.as_mut() }) else {
        set_last_error("output pointer is null");
        return status::NULL_POINTER;
    };
    let rect = scene
        .overlay
        .layout(width, height)
        .into_iter()
        .find(|(element, _)| *element == id)
        .map(|(_, r)| [r.x, r.y, r.width, r.height])
        .unwrap_or([0.0; 4]);
    *out = rect;
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_overlay_measure_text(
    scene: *mut Scene,
    font: u32,
    size: f32,
    text: *const c_char,
    max_width: f32,
    out_width: *mut f32,
    out_height: *mut f32,
) -> i32 {
    let scene = scene_ref!(scene);
    let text = unsafe { str_from_ptr(text) }.unwrap_or_default();
    if scene.overlay.font(font).is_none() {
        set_last_error("invalid font");
        return status::INVALID_HANDLE;
    }
    let (w, h) = scene.overlay.measure_text(font, size, text, (max_width > 0.0).then_some(max_width));
    unsafe {
        if let Some(out) = out_width.as_mut() {
            *out = w;
        }
        if let Some(out) = out_height.as_mut() {
            *out = h;
        }
    }
    status::OK
}

/// Loads a TTF / OTF font from memory; returns its index (>= 1) or a negative status.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_overlay_load_font(scene: *mut Scene, bytes: *const u8, length: u32) -> i32 {
    let scene = scene_ref!(scene);
    if bytes.is_null() || length == 0 {
        set_last_error("font buffer is null or empty");
        return status::INVALID_ARGUMENT;
    }
    let data = unsafe { std::slice::from_raw_parts(bytes, length as usize) }.to_vec();
    match scene.overlay.add_font(data) {
        Ok(index) => index as i32,
        Err(error) => fail(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_overlay_configure(scene: *mut Scene, scale: f32, enabled: i32) -> i32 {
    let scene = scene_ref!(scene);
    scene.overlay.scale = if scale > 0.0 { scale } else { 1.0 };
    scene.overlay.enabled = enabled != 0;
    status::OK
}

// ----------------------------------------------------------------- gamepads

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct TnGamepadState {
    pub connected: i32,
    pub is_virtual: i32,
    pub buttons: u32,
    pub previous_buttons: u32,
    pub axes: [f32; 6],
}

#[unsafe(no_mangle)]
pub extern "C" fn tn_gamepads_create() -> *mut crate::gamepad::Gamepads {
    Box::into_raw(Box::new(crate::gamepad::Gamepads::new()))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_gamepads_destroy(pads: *mut crate::gamepad::Gamepads) {
    if !pads.is_null() {
        drop(unsafe { Box::from_raw(pads) });
    }
}

macro_rules! pads_ref {
    ($pads:expr) => {
        match unsafe { $pads.as_mut() } {
            Some(pads) => pads,
            None => {
                set_last_error("gamepads pointer is null");
                return status::NULL_POINTER;
            }
        }
    };
}

/// Polls the platform; returns the number of connected pads.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_gamepads_update(pads: *mut crate::gamepad::Gamepads) -> i32 {
    let pads = pads_ref!(pads);
    pads.update() as i32
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_gamepads_slot_count(pads: *mut crate::gamepad::Gamepads) -> i32 {
    let pads = pads_ref!(pads);
    pads.slot_count() as i32
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_gamepads_configure(pads: *mut crate::gamepad::Gamepads, dead_zone: f32, trigger_threshold: f32) -> i32 {
    let pads = pads_ref!(pads);
    pads.dead_zone = dead_zone.clamp(0.0, 0.95);
    pads.trigger_threshold = trigger_threshold.clamp(0.0, 1.0);
    status::OK
}

/// Copies the platform initialisation error (empty when gamepads work).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_gamepads_get_error(pads: *mut crate::gamepad::Gamepads, buffer: *mut c_char, capacity: i32) -> i32 {
    let pads = pads_ref!(pads);
    let message = pads.init_error.clone().unwrap_or_default();
    unsafe { copy_string(&message, buffer, capacity) }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_gamepad_get_state(pads: *mut crate::gamepad::Gamepads, slot: u32, out: *mut TnGamepadState) -> i32 {
    let pads = pads_ref!(pads);
    let Some(state) = pads.state(slot as usize) else {
        set_last_error("gamepad slot out of range");
        return status::INVALID_HANDLE;
    };
    let Some(out) = (unsafe { out.as_mut() }) else {
        set_last_error("output pointer is null");
        return status::NULL_POINTER;
    };
    *out = TnGamepadState {
        connected: state.connected as i32,
        is_virtual: state.is_virtual as i32,
        buttons: state.buttons,
        previous_buttons: state.previous_buttons,
        axes: state.axes,
    };
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_gamepad_get_name(pads: *mut crate::gamepad::Gamepads, slot: u32, buffer: *mut c_char, capacity: i32) -> i32 {
    let pads = pads_ref!(pads);
    match pads.state(slot as usize) {
        Some(state) => {
            let name = state.name.clone();
            unsafe { copy_string(&name, buffer, capacity) }
        }
        None => {
            set_last_error("gamepad slot out of range");
            status::INVALID_HANDLE
        }
    }
}

/// Creates or updates a virtual pad in `slot` (at most one past the last slot).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_gamepad_set_virtual(
    pads: *mut crate::gamepad::Gamepads,
    slot: u32,
    state: *const TnGamepadState,
    name: *const c_char,
) -> i32 {
    let pads = pads_ref!(pads);
    let Some(state) = (unsafe { state.as_ref() }) else {
        set_last_error("state pointer is null");
        return status::NULL_POINTER;
    };
    let name = unsafe { str_from_ptr(name) }.unwrap_or("Virtual gamepad");
    if pads.set_virtual(slot as usize, state.buttons, state.axes, state.connected != 0, name) {
        status::OK
    } else {
        set_last_error("slot is owned by a physical controller or out of range");
        status::INVALID_ARGUMENT
    }
}

/// Rumble for `duration_ms`; returns 1 when played, 0 when unsupported.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_gamepad_rumble(pads: *mut crate::gamepad::Gamepads, slot: u32, strong: f32, weak: f32, duration_ms: u32) -> i32 {
    let pads = pads_ref!(pads);
    pads.rumble(slot as usize, strong, weak, duration_ms) as i32
}

// ------------------------------------------------------------------ physics

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct TnBodyDesc {
    /// 0 dynamic, 1 fixed, 2 kinematic.
    pub kind: u32,
    pub additional_mass: f32,
    pub linear_damping: f32,
    pub angular_damping: f32,
    pub gravity_scale: f32,
    pub ccd: i32,
    pub lock_rotations: i32,
    pub can_sleep: i32,
    pub linear_velocity: TnVec3,
    pub angular_velocity: TnVec3,
}

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct TnColliderDesc {
    /// 0 box, 1 sphere, 2 capsule, 3 cylinder, 4 triangle mesh, 5 convex hull.
    pub shape: u32,
    pub half_extents: TnVec3,
    pub radius: f32,
    pub half_height: f32,
    pub geometry: u32,
    pub offset: TnVec3,
    pub rotation: TnVec4,
    pub friction: f32,
    pub restitution: f32,
    pub density: f32,
    pub sensor: i32,
    pub membership: u32,
    pub filter: u32,
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct TnPhysicsHit {
    pub node: u32,
    pub distance: f32,
    pub point: TnVec3,
    pub normal: TnVec3,
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct TnContactEvent {
    pub node_a: u32,
    pub node_b: u32,
    pub started: i32,
    pub sensor: i32,
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_physics_configure(scene: *mut Scene, gravity: TnVec3, fixed_timestep: f32, max_substeps: u32) -> i32 {
    let scene = scene_ref!(scene);
    scene.physics.set_gravity(gravity.into());
    scene.physics.fixed_timestep = fixed_timestep.clamp(1.0 / 1000.0, 0.5);
    scene.physics.max_substeps = max_substeps.clamp(1, 64);
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_physics_add_body(scene: *mut Scene, node: u32, desc: *const TnBodyDesc) -> i32 {
    use crate::physics::{BodyDesc, BodyKind};
    let scene = scene_ref!(scene);
    let Some(d) = (unsafe { desc.as_ref() }) else {
        set_last_error("body descriptor is null");
        return status::NULL_POINTER;
    };
    let body = BodyDesc {
        kind: BodyKind::from_u32(d.kind),
        additional_mass: d.additional_mass,
        linear_damping: d.linear_damping,
        angular_damping: d.angular_damping,
        gravity_scale: d.gravity_scale,
        ccd: d.ccd != 0,
        lock_rotations: d.lock_rotations != 0,
        can_sleep: d.can_sleep != 0,
        linear_velocity: d.linear_velocity.into(),
        angular_velocity: d.angular_velocity.into(),
    };
    match scene.physics_add_body(node, body) {
        Ok(()) => status::OK,
        Err(error) => fail(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_physics_add_collider(scene: *mut Scene, node: u32, desc: *const TnColliderDesc) -> i32 {
    use crate::physics::{ColliderDesc, ShapeDesc};
    let scene = scene_ref!(scene);
    let Some(d) = (unsafe { desc.as_ref() }) else {
        set_last_error("collider descriptor is null");
        return status::NULL_POINTER;
    };
    let shape = match d.shape {
        0 => ShapeDesc::Box { half_extents: d.half_extents.into() },
        1 => ShapeDesc::Sphere { radius: d.radius },
        2 => ShapeDesc::Capsule { half_height: d.half_height, radius: d.radius },
        3 => ShapeDesc::Cylinder { half_height: d.half_height, radius: d.radius },
        4 => ShapeDesc::TriMesh { geometry: d.geometry },
        5 => ShapeDesc::ConvexHull { geometry: d.geometry },
        _ => {
            set_last_error("unknown collider shape");
            return status::INVALID_ARGUMENT;
        }
    };
    let rotation: Vec4 = d.rotation.into();
    let collider = ColliderDesc {
        shape,
        offset: d.offset.into(),
        rotation: Quat::from_xyzw(rotation.x, rotation.y, rotation.z, rotation.w),
        friction: d.friction,
        restitution: d.restitution,
        density: d.density,
        sensor: d.sensor != 0,
        membership: d.membership,
        filter: d.filter,
    };
    match scene.physics_add_collider(node, collider) {
        Ok(()) => status::OK,
        Err(error) => fail(error),
    }
}

/// Removes the node's body and colliders; returns 1 when something was removed.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_physics_remove(scene: *mut Scene, node: u32) -> i32 {
    let scene = scene_ref!(scene);
    scene.physics_remove(node) as i32
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_physics_has_body(scene: *mut Scene, node: u32) -> i32 {
    let scene = scene_ref!(scene);
    scene.physics_has_body(node) as i32
}

/// Advances the simulation; returns the number of fixed steps taken.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_physics_step(scene: *mut Scene, delta: f32) -> i32 {
    let scene = scene_ref!(scene);
    scene.physics_step(delta) as i32
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_physics_apply_impulse(scene: *mut Scene, node: u32, impulse: TnVec3, torque_impulse: TnVec3) -> i32 {
    let scene = scene_ref!(scene);
    match scene.physics_apply_impulse(node, impulse.into(), torque_impulse.into()) {
        Ok(()) => status::OK,
        Err(error) => fail(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_physics_add_force(scene: *mut Scene, node: u32, force: TnVec3, torque: TnVec3) -> i32 {
    let scene = scene_ref!(scene);
    match scene.physics_add_force(node, force.into(), torque.into()) {
        Ok(()) => status::OK,
        Err(error) => fail(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_physics_set_velocity(scene: *mut Scene, node: u32, linear: TnVec3, angular: TnVec3) -> i32 {
    let scene = scene_ref!(scene);
    match scene.physics_set_velocity(node, linear.into(), angular.into()) {
        Ok(()) => status::OK,
        Err(error) => fail(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_physics_get_velocity(scene: *mut Scene, node: u32, linear: *mut TnVec3, angular: *mut TnVec3) -> i32 {
    let scene = scene_ref!(scene);
    match scene.physics_velocity(node) {
        Ok((l, a)) => {
            unsafe {
                if let Some(out) = linear.as_mut() {
                    *out = l.into();
                }
                if let Some(out) = angular.as_mut() {
                    *out = a.into();
                }
            }
            status::OK
        }
        Err(error) => fail(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_physics_teleport(scene: *mut Scene, node: u32, position: TnVec3, rotation: TnVec4) -> i32 {
    let scene = scene_ref!(scene);
    let r: Vec4 = rotation.into();
    match scene.physics_teleport(node, position.into(), Quat::from_xyzw(r.x, r.y, r.z, r.w)) {
        Ok(()) => status::OK,
        Err(error) => fail(error),
    }
}

/// Returns 1 when sleeping, 0 when awake, negative on error.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_physics_is_sleeping(scene: *mut Scene, node: u32) -> i32 {
    let scene = scene_ref!(scene);
    match scene.physics_is_sleeping(node) {
        Ok(sleeping) => sleeping as i32,
        Err(error) => fail(error),
    }
}

/// Returns 1 and fills `out` on a hit, 0 when nothing was hit.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_physics_raycast(
    scene: *mut Scene,
    origin: TnVec3,
    direction: TnVec3,
    max_distance: f32,
    exclude: u32,
    out: *mut TnPhysicsHit,
) -> i32 {
    let scene = scene_ref!(scene);
    match scene.physics_raycast(origin.into(), direction.into(), max_distance, (exclude != 0).then_some(exclude)) {
        Some(hit) => {
            if let Some(out) = unsafe { out.as_mut() } {
                *out = TnPhysicsHit { node: hit.node, distance: hit.distance, point: hit.point.into(), normal: hit.normal.into() };
            }
            1
        }
        None => 0,
    }
}

/// Copies up to `capacity` pending contact events and removes them; returns the count copied.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_physics_take_events(scene: *mut Scene, out: *mut TnContactEvent, capacity: u32) -> i32 {
    let scene = scene_ref!(scene);
    let events = scene.physics_take_events();
    let count = events.len().min(capacity as usize);
    if !out.is_null() {
        for (i, event) in events.iter().take(count).enumerate() {
            unsafe {
                *out.add(i) = TnContactEvent {
                    node_a: event.node_a,
                    node_b: event.node_b,
                    started: event.started as i32,
                    sensor: event.sensor as i32,
                };
            }
        }
    }
    count as i32
}

/// Adds a joint (0 fixed, 1 ball, 2 hinge, 3 slider); returns its id or 0.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_physics_add_joint(
    scene: *mut Scene,
    kind: u32,
    node_a: u32,
    node_b: u32,
    anchor_a: TnVec3,
    anchor_b: TnVec3,
    axis: TnVec3,
) -> u32 {
    let scene = scene_ref!(scene, 0);
    match scene.physics_add_joint(crate::physics::JointKind::from_u32(kind), node_a, node_b, anchor_a.into(), anchor_b.into(), axis.into()) {
        Ok(id) => id,
        Err(error) => {
            fail(error);
            0
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_physics_remove_joint(scene: *mut Scene, joint: u32) -> i32 {
    let scene = scene_ref!(scene);
    scene.physics_remove_joint(joint) as i32
}

/// Moves a kinematic character; returns 1 when grounded, 0 when airborne, negative on error.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_physics_move_character(scene: *mut Scene, node: u32, desired: TnVec3, delta: f32, applied: *mut TnVec3) -> i32 {
    let scene = scene_ref!(scene);
    match scene.physics_move_character(node, desired.into(), delta) {
        Ok((movement, grounded)) => {
            if let Some(out) = unsafe { applied.as_mut() } {
                *out = movement.into();
            }
            grounded as i32
        }
        Err(error) => fail(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_physics_configure_character(scene: *mut Scene, max_slope_degrees: f32, step_height: f32, snap_to_ground: f32) -> i32 {
    let scene = scene_ref!(scene);
    scene.physics_configure_character(max_slope_degrees, step_height, snap_to_ground);
    status::OK
}

// -------------------------------------------------------------------- audio

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct TnSoundDesc {
    pub clip: u32,
    pub gain: f32,
    pub pitch: f32,
    pub looping: i32,
    pub spatial: i32,
    pub position: TnVec3,
    pub velocity: TnVec3,
    pub min_distance: f32,
    pub max_distance: f32,
    pub rolloff: f32,
    pub node: u32,
    pub paused: i32,
}

impl From<&TnSoundDesc> for crate::audio::SourceDesc {
    fn from(d: &TnSoundDesc) -> Self {
        Self {
            clip: d.clip,
            gain: d.gain,
            pitch: d.pitch,
            looping: d.looping != 0,
            spatial: d.spatial != 0,
            position: d.position.into(),
            velocity: d.velocity.into(),
            min_distance: d.min_distance,
            max_distance: d.max_distance,
            rolloff: d.rolloff,
            node: (d.node != 0).then_some(d.node),
            paused: d.paused != 0,
        }
    }
}

impl From<crate::audio::SourceDesc> for TnSoundDesc {
    fn from(d: crate::audio::SourceDesc) -> Self {
        Self {
            clip: d.clip,
            gain: d.gain,
            pitch: d.pitch,
            looping: d.looping as i32,
            spatial: d.spatial as i32,
            position: d.position.into(),
            velocity: d.velocity.into(),
            min_distance: d.min_distance,
            max_distance: d.max_distance,
            rolloff: d.rolloff,
            node: d.node.unwrap_or(0),
            paused: d.paused as i32,
        }
    }
}

macro_rules! audio_ref {
    ($engine:expr) => {
        match unsafe { $engine.as_mut() } {
            Some(engine) => engine,
            None => {
                set_last_error("audio engine pointer is null");
                return status::NULL_POINTER;
            }
        }
    };
    ($engine:expr, $fallback:expr) => {
        match unsafe { $engine.as_mut() } {
            Some(engine) => engine,
            None => {
                set_last_error("audio engine pointer is null");
                return $fallback;
            }
        }
    };
}

/// Opens the default output device, or an offline mixer when `offline` != 0.
#[unsafe(no_mangle)]
pub extern "C" fn tn_audio_create(offline: i32, sample_rate: u32) -> *mut crate::audio::AudioEngine {
    let engine = if offline != 0 {
        Ok(crate::audio::AudioEngine::offline(if sample_rate > 0 { sample_rate } else { 48_000 }))
    } else {
        crate::audio::AudioEngine::new()
    };
    match engine {
        Ok(engine) => Box::into_raw(Box::new(engine)),
        Err(error) => {
            fail(error);
            std::ptr::null_mut()
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_audio_destroy(engine: *mut crate::audio::AudioEngine) {
    if !engine.is_null() {
        drop(unsafe { Box::from_raw(engine) });
    }
}

/// Copies the device name; writes the mixer sample rate.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_audio_get_info(engine: *mut crate::audio::AudioEngine, sample_rate: *mut u32, buffer: *mut c_char, capacity: i32) -> i32 {
    let engine = audio_ref!(engine);
    if let Some(out) = unsafe { sample_rate.as_mut() } {
        *out = engine.with_mixer(|m| m.sample_rate);
    }
    let name = engine.device_name.clone();
    unsafe { copy_string(&name, buffer, capacity) }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_audio_load_clip(engine: *mut crate::audio::AudioEngine, bytes: *const u8, length: u32) -> u32 {
    let engine = audio_ref!(engine, 0);
    if bytes.is_null() || length == 0 {
        set_last_error("audio buffer is null or empty");
        return 0;
    }
    let data = unsafe { std::slice::from_raw_parts(bytes, length as usize) }.to_vec();
    match crate::audio::AudioClip::decode(data) {
        Ok(clip) => engine.with_mixer(|m| m.add_clip(clip)),
        Err(error) => {
            fail(error);
            0
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_audio_create_clip(
    engine: *mut crate::audio::AudioEngine,
    samples: *const f32,
    sample_count: u32,
    channels: u32,
    sample_rate: u32,
) -> u32 {
    let engine = audio_ref!(engine, 0);
    if samples.is_null() || sample_count == 0 {
        set_last_error("sample buffer is null or empty");
        return 0;
    }
    let data = unsafe { std::slice::from_raw_parts(samples, sample_count as usize) }.to_vec();
    match crate::audio::AudioClip::from_samples(data, channels as u16, sample_rate) {
        Ok(clip) => engine.with_mixer(|m| m.add_clip(clip)),
        Err(error) => {
            fail(error);
            0
        }
    }
}

/// Clip length in seconds, negative for an invalid clip.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_audio_clip_duration(engine: *mut crate::audio::AudioEngine, clip: u32) -> f32 {
    let engine = audio_ref!(engine, -1.0);
    engine.with_mixer(|m| m.clip(clip).map_or(-1.0, |c| c.duration()))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_audio_remove_clip(engine: *mut crate::audio::AudioEngine, clip: u32) -> i32 {
    let engine = audio_ref!(engine);
    engine.with_mixer(|m| m.remove_clip(clip)) as i32
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_audio_play(engine: *mut crate::audio::AudioEngine, desc: *const TnSoundDesc) -> u32 {
    let engine = audio_ref!(engine, 0);
    let Some(desc) = (unsafe { desc.as_ref() }) else {
        set_last_error("sound descriptor is null");
        return 0;
    };
    match engine.with_mixer(|m| m.play(desc.into())) {
        Ok(id) => id,
        Err(error) => {
            fail(error);
            0
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_audio_update_source(engine: *mut crate::audio::AudioEngine, source: u32, desc: *const TnSoundDesc) -> i32 {
    let engine = audio_ref!(engine);
    let Some(desc) = (unsafe { desc.as_ref() }) else {
        set_last_error("sound descriptor is null");
        return status::NULL_POINTER;
    };
    match engine.with_mixer(|m| m.update_source(source, desc.into())) {
        Ok(()) => status::OK,
        Err(error) => fail(error),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_audio_get_source(engine: *mut crate::audio::AudioEngine, source: u32, out: *mut TnSoundDesc) -> i32 {
    let engine = audio_ref!(engine);
    match engine.with_mixer(|m| m.source(source)) {
        Some(desc) => {
            if let Some(out) = unsafe { out.as_mut() } {
                *out = desc.into();
            }
            status::OK
        }
        None => {
            set_last_error("the sound finished or was stopped");
            status::INVALID_HANDLE
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_audio_is_playing(engine: *mut crate::audio::AudioEngine, source: u32) -> i32 {
    let engine = audio_ref!(engine);
    engine.with_mixer(|m| m.is_playing(source)) as i32
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_audio_stop(engine: *mut crate::audio::AudioEngine, source: u32) -> i32 {
    let engine = audio_ref!(engine);
    engine.with_mixer(|m| m.stop(source)) as i32
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_audio_seek(engine: *mut crate::audio::AudioEngine, source: u32, seconds: f32) -> i32 {
    let engine = audio_ref!(engine);
    engine.with_mixer(|m| m.seek(source, seconds)) as i32
}

/// Playback position in seconds, negative when the source is gone.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_audio_get_time(engine: *mut crate::audio::AudioEngine, source: u32) -> f32 {
    let engine = audio_ref!(engine, -1.0);
    engine.with_mixer(|m| m.position_seconds(source).unwrap_or(-1.0))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_audio_set_listener(
    engine: *mut crate::audio::AudioEngine,
    position: TnVec3,
    forward: TnVec3,
    up: TnVec3,
    velocity: TnVec3,
) -> i32 {
    let engine = audio_ref!(engine);
    engine.with_mixer(|m| {
        m.listener = crate::audio::Listener {
            position: position.into(),
            forward: forward.into(),
            up: up.into(),
            velocity: velocity.into(),
        }
    });
    status::OK
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_audio_configure(engine: *mut crate::audio::AudioEngine, master_gain: f32, doppler_factor: f32, speed_of_sound: f32) -> i32 {
    let engine = audio_ref!(engine);
    engine.with_mixer(|m| {
        m.master_gain = master_gain.max(0.0);
        m.doppler_factor = doppler_factor.max(0.0);
        m.speed_of_sound = speed_of_sound.max(1.0);
    });
    status::OK
}

/// Places the listener on `listener_node` (0 keeps it) and moves node attached sources.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_audio_sync_scene(engine: *mut crate::audio::AudioEngine, scene: *mut Scene, listener_node: u32, delta: f32) -> i32 {
    let engine = audio_ref!(engine);
    let scene = scene_ref!(scene);
    engine.sync_scene(scene, (listener_node != 0).then_some(listener_node), delta);
    status::OK
}

/// Renders `frames` stereo frames into `out` (offline engines only).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_audio_render(engine: *mut crate::audio::AudioEngine, out: *mut f32, frames: u32) -> i32 {
    let engine = audio_ref!(engine);
    if !engine.is_offline() {
        set_last_error("only offline engines can be rendered manually");
        return status::INVALID_ARGUMENT;
    }
    if out.is_null() {
        set_last_error("output buffer is null");
        return status::NULL_POINTER;
    }
    let buffer = unsafe { std::slice::from_raw_parts_mut(out, frames as usize * 2) };
    engine.with_mixer(|m| m.render(buffer, 2)) as i32
}

// ----------------------------------------------------------------------- XR

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct TnXrInfo {
    pub loader_found: i32,
    pub runtime_found: i32,
    pub headset_found: i32,
    pub vendor_id: u32,
    pub recommended_width: u32,
    pub recommended_height: u32,
    pub view_count: u32,
    pub orientation_tracking: i32,
    pub position_tracking: i32,
}

/// Probes OpenXR; strings are written as "runtime name|runtime version|system name|message".
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tn_xr_probe(out: *mut TnXrInfo, buffer: *mut c_char, capacity: i32) -> i32 {
    let probe = crate::xr::probe();
    if let Some(out) = unsafe { out.as_mut() } {
        *out = TnXrInfo {
            loader_found: probe.loader_found as i32,
            runtime_found: probe.runtime_found as i32,
            headset_found: probe.headset_found as i32,
            vendor_id: probe.vendor_id,
            recommended_width: probe.recommended_width,
            recommended_height: probe.recommended_height,
            view_count: probe.view_count,
            orientation_tracking: probe.orientation_tracking as i32,
            position_tracking: probe.position_tracking as i32,
        };
    }
    let text = format!("{}|{}|{}|{}", probe.runtime_name, probe.runtime_version, probe.system_name, probe.message.replace('|', "/"));
    unsafe { copy_string(&text, buffer, capacity) }
}
