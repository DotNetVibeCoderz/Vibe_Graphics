//! GPU uniform layouts. Every struct is explicitly padded to 16 byte
//! boundaries so it can be memcopied straight into a WGSL `uniform` block.

use bytemuck::{Pod, Zeroable};

use crate::light::Light;
use crate::material::{Material, ShadingModel};
use crate::math::{Mat3, Mat4, Vec3, Vec4};
use crate::scene::Environment;

/// Maximum number of lights uploaded per frame; matches `MAX_LIGHTS` in the shader.
pub const MAX_LIGHTS: usize = 128;

#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
pub struct FrameUniform {
    pub view: [[f32; 4]; 4],
    pub projection: [[f32; 4]; 4],
    pub view_projection: [[f32; 4]; 4],
    pub inverse_view_projection: [[f32; 4]; 4],
    /// `xyz` = camera world position, `w` = exposure.
    pub camera_position: [f32; 4],
    /// `xyz` = ambient colour, `w` = ambient intensity.
    pub ambient: [f32; 4],
    /// `xyz` = fog colour, `w` = fog density (`0` disables fog).
    pub fog_color: [f32; 4],
    /// `x` = fog start, `y` = fog end, `z` = elapsed time, `w` = environment intensity.
    pub fog_params: [f32; 4],
    /// `x` = light count, `y` = has environment map, `z` = near, `w` = far.
    pub misc: [f32; 4],
    /// `xy` = render target size in pixels, `z` = SSAO enabled, `w` = SSAO strength on direct light.
    pub screen: [f32; 4],
}

impl FrameUniform {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        view: Mat4,
        projection: Mat4,
        camera_position: Vec3,
        exposure: f32,
        environment: &Environment,
        light_count: u32,
        has_environment_map: bool,
        near: f32,
        far: f32,
        time: f32,
        screen: [f32; 4],
    ) -> Self {
        let view_projection = projection * view;
        Self {
            view: view.to_cols_array_2d(),
            projection: projection.to_cols_array_2d(),
            view_projection: view_projection.to_cols_array_2d(),
            inverse_view_projection: view_projection.inverse().to_cols_array_2d(),
            camera_position: camera_position.extend(exposure).to_array(),
            ambient: environment
                .ambient_color
                .extend(environment.ambient_intensity)
                .to_array(),
            fog_color: environment
                .fog_color
                .extend(environment.fog_density)
                .to_array(),
            fog_params: [
                environment.fog_start,
                environment.fog_end,
                time,
                environment.environment_intensity,
            ],
            misc: [
                light_count as f32,
                if has_environment_map { 1.0 } else { 0.0 },
                near,
                far,
            ],
            screen,
        }
    }
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
pub struct LightUniform {
    /// `xyz` = world position, `w` = light kind.
    pub position: [f32; 4],
    /// `xyz` = world direction (node `-Z`), `w` = range (`0` = infinite).
    pub direction: [f32; 4],
    /// `xyz` = linear colour, `w` = intensity.
    pub color: [f32; 4],
    /// `x` = cos(inner), `y` = cos(outer), `z` = width, `w` = height.
    pub params: [f32; 4],
    /// `x` = first shadow map layer (`-1` = no shadow), `y` = cascade count, `z` = depth bias, `w` = normal bias.
    pub shadow: [f32; 4],
    /// `x` = shadow strength, `yzw` reserved.
    pub shadow_extra: [f32; 4],
}

impl Default for LightUniform {
    fn default() -> Self {
        Self::zeroed()
    }
}

impl LightUniform {
    pub fn new(light: &Light, world: &Mat4) -> Self {
        let position = world.w_axis.truncate();
        // Node local `-Z` is the emission direction, matching glTF and Three.js.
        let direction = Mat3::from_mat4(*world)
            .mul_vec3(Vec3::NEG_Z)
            .normalize_or(Vec3::NEG_Z);
        Self {
            position: position.extend(light.kind as u32 as f32).to_array(),
            direction: direction.extend(light.range).to_array(),
            color: light.color.extend(light.intensity).to_array(),
            params: [
                light.inner_cone_angle.cos(),
                light.outer_cone_angle.cos(),
                light.size.0,
                light.size.1,
            ],
            shadow: [-1.0, 0.0, light.shadow_bias, light.shadow_normal_bias],
            shadow_extra: [light.shadow_strength, 0.0, 0.0, 0.0],
        }
    }
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
pub struct LightsUniform {
    pub lights: [LightUniform; MAX_LIGHTS],
}

impl Default for LightsUniform {
    fn default() -> Self {
        Self::zeroed()
    }
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
pub struct MaterialUniform {
    pub base_color: [f32; 4],
    /// `xyz` = emissive colour, `w` = emissive intensity.
    pub emissive: [f32; 4],
    /// `x` = metallic, `y` = roughness, `z` = reflectance, `w` = shininess.
    pub params0: [f32; 4],
    /// `x` = normal scale, `y` = occlusion strength, `z` = alpha cutoff, `w` = shading model.
    pub params1: [f32; 4],
    /// `xyz` = specular colour, `w` = alpha mode.
    pub specular: [f32; 4],
    /// `xy` = uv scale, `zw` = uv offset.
    pub uv_transform: [f32; 4],
    /// Texture presence flags: base colour, normal, metallic-roughness, emissive.
    pub texture_flags: [f32; 4],
    /// `x` = occlusion map flag, `yzw` reserved.
    pub texture_flags2: [f32; 4],
    /// Free parameters for custom shaders.
    pub custom0: [f32; 4],
    pub custom1: [f32; 4],
}

impl Default for MaterialUniform {
    fn default() -> Self {
        Self::zeroed()
    }
}

impl From<&Material> for MaterialUniform {
    fn from(material: &Material) -> Self {
        let flag = |value: bool| if value { 1.0 } else { 0.0 };
        Self {
            base_color: material.base_color.to_array(),
            emissive: material
                .emissive
                .extend(material.emissive_intensity)
                .to_array(),
            params0: [
                material.metallic,
                material.roughness.clamp(0.015, 1.0),
                material.reflectance,
                material.shininess.max(1.0),
            ],
            params1: [
                material.normal_scale,
                material.occlusion_strength,
                material.alpha_cutoff,
                material.shading as u32 as f32,
            ],
            specular: material
                .specular
                .extend(material.alpha_mode as u32 as f32)
                .to_array(),
            uv_transform: [
                material.uv_scale.x,
                material.uv_scale.y,
                material.uv_offset.x,
                material.uv_offset.y,
            ],
            texture_flags: [
                flag(material.textures.base_color.is_some()),
                flag(material.textures.normal.is_some()),
                flag(material.textures.metallic_roughness.is_some()),
                flag(material.textures.emissive.is_some()),
            ],
            texture_flags2: [flag(material.textures.occlusion.is_some()), 0.0, 0.0, 0.0],
            custom0: [material.custom[0], material.custom[1], material.custom[2], material.custom[3]],
            custom1: [material.custom[4], material.custom[5], material.custom[6], material.custom[7]],
        }
    }
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
pub struct ObjectUniform {
    pub model: [[f32; 4]; 4],
    /// Inverse transpose of the model matrix, padded to `mat4` for alignment.
    pub normal_matrix: [[f32; 4]; 4],
    /// `x` = receives shadows (0 or 1), `yzw` reserved.
    pub flags: [f32; 4],
}

impl Default for ObjectUniform {
    fn default() -> Self {
        Self {
            model: Mat4::IDENTITY.to_cols_array_2d(),
            normal_matrix: Mat4::IDENTITY.to_cols_array_2d(),
            flags: [1.0, 0.0, 0.0, 0.0],
        }
    }
}

impl ObjectUniform {
    pub fn new(model: &Mat4, receive_shadow: bool) -> Self {
        let normal = Mat3::from_mat4(*model).inverse().transpose();
        Self {
            model: model.to_cols_array_2d(),
            normal_matrix: Mat4::from_cols(
                normal.x_axis.extend(0.0),
                normal.y_axis.extend(0.0),
                normal.z_axis.extend(0.0),
                Vec4::W,
            )
            .to_cols_array_2d(),
            flags: [if receive_shadow { 1.0 } else { 0.0 }, 0.0, 0.0, 0.0],
        }
    }
}

/// Shadow map layers available per frame; matches `MAX_SHADOW_LAYERS` in the shader.
pub const MAX_SHADOW_LAYERS: usize = 8;

/// Maximum cascades for one directional light.
pub const MAX_CASCADES: usize = 4;

/// Everything the lighting shader needs to sample the shadow map array.
#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
pub struct ShadowUniform {
    /// World to shadow clip space, one per shadow map layer.
    pub matrices: [[[f32; 4]; 4]; MAX_SHADOW_LAYERS],
    /// View space far distance of each directional cascade.
    pub cascade_splits: [f32; 4],
    /// `x` = texel size (1 / map size), `y` = PCF radius in texels, `z` = cascade blend width (0..1), `w` = enabled.
    pub params: [f32; 4],
    /// World space size of one shadow texel for each layer, used to scale the normal bias.
    pub texel_world: [[f32; 4]; 2],
}

impl Default for ShadowUniform {
    fn default() -> Self {
        Self::zeroed()
    }
}

/// View-projection used while rendering one shadow map layer.
#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
pub struct ShadowPassUniform {
    pub view_projection: [[f32; 4]; 4],
}

/// SSAO kernel size; matches `MAX_SSAO_SAMPLES` in the shader.
pub const MAX_SSAO_SAMPLES: usize = 32;

#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
pub struct SsaoUniform {
    pub projection: [[f32; 4]; 4],
    /// Hemisphere samples in tangent space (`xyz`), `w` unused.
    pub kernel: [[f32; 4]; MAX_SSAO_SAMPLES],
    /// `x` = radius, `y` = bias, `z` = intensity (power), `w` = sample count.
    pub params: [f32; 4],
    /// `xy` = noise uv scale (target size / noise size), `zw` = texel size of the AO target.
    pub noise: [f32; 4],
}

/// Parameters of one bilateral blur pass.
#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable, Default)]
pub struct BlurUniform {
    /// `xy` = texel step (direction scaled by texel size), `z` = depth sharpness, `w` unused.
    pub params: [f32; 4],
}
/// Parameters shared by the post-processing passes.
#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable, Default)]
pub struct PostUniform {
    /// `x` = exposure, `y` = tone mapping operator, `z` = bloom intensity, `w` = bloom threshold.
    pub params: [f32; 4],
    /// `xy` = texel size of the source texture, `zw` = blur direction.
    pub texel: [f32; 4],
}

/// Shading model helper used when sorting draw calls.
pub fn shading_sort_key(model: ShadingModel) -> u32 {
    model as u32
}
