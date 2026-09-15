//! Material definitions. A single uber shader implements every shading model,
//! selected per draw call through a specialisation constant-like flag.

use crate::math::{Vec2, Vec3, Vec4};
use crate::scene::TextureId;

/// Shading models available out of the box, mirroring the Three.js material set.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u32)]
pub enum ShadingModel {
    /// Unlit, base colour only (`MeshBasicMaterial`).
    Basic = 0,
    /// Lambert diffuse (`MeshLambertMaterial`).
    Lambert = 1,
    /// Blinn-Phong specular (`MeshPhongMaterial`).
    Phong = 2,
    /// Metallic-roughness physically based shading (`MeshStandardMaterial`).
    Pbr = 3,
}

impl ShadingModel {
    pub fn from_u32(value: u32) -> Self {
        match value {
            1 => ShadingModel::Lambert,
            2 => ShadingModel::Phong,
            3 => ShadingModel::Pbr,
            _ => ShadingModel::Basic,
        }
    }
}

/// How the fragment alpha is resolved.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u32)]
pub enum AlphaMode {
    Opaque = 0,
    /// Alpha tested against [`Material::alpha_cutoff`].
    Mask = 1,
    /// Sorted back to front and alpha blended.
    Blend = 2,
}

impl AlphaMode {
    pub fn from_u32(value: u32) -> Self {
        match value {
            1 => AlphaMode::Mask,
            2 => AlphaMode::Blend,
            _ => AlphaMode::Opaque,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u32)]
pub enum CullMode {
    None = 0,
    Back = 1,
    Front = 2,
}

impl CullMode {
    pub fn from_u32(value: u32) -> Self {
        match value {
            1 => CullMode::Back,
            2 => CullMode::Front,
            _ => CullMode::None,
        }
    }

    pub(crate) fn to_wgpu(self) -> Option<wgpu::Face> {
        match self {
            CullMode::None => None,
            CullMode::Back => Some(wgpu::Face::Back),
            CullMode::Front => Some(wgpu::Face::Front),
        }
    }
}

/// Optional texture slots. `None` falls back to a 1x1 default texture so the
/// bind group layout stays identical for every material.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct MaterialTextures {
    pub base_color: Option<TextureId>,
    pub normal: Option<TextureId>,
    /// Green channel = roughness, blue channel = metallic (glTF convention).
    pub metallic_roughness: Option<TextureId>,
    pub emissive: Option<TextureId>,
    pub occlusion: Option<TextureId>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Material {
    pub name: String,
    pub shading: ShadingModel,
    pub alpha_mode: AlphaMode,
    pub cull_mode: CullMode,
    /// Linear RGBA base colour (albedo / diffuse).
    pub base_color: Vec4,
    pub emissive: Vec3,
    pub emissive_intensity: f32,
    pub metallic: f32,
    pub roughness: f32,
    /// Blinn-Phong specular colour and exponent, ignored by the PBR model.
    pub specular: Vec3,
    pub shininess: f32,
    pub reflectance: f32,
    pub normal_scale: f32,
    pub occlusion_strength: f32,
    pub alpha_cutoff: f32,
    pub uv_scale: Vec2,
    pub uv_offset: Vec2,
    pub depth_write: bool,
    pub depth_test: bool,
    pub wireframe: bool,
    /// Rendering order override for transparent objects (higher renders later).
    pub render_order: i32,
    pub textures: MaterialTextures,
    pub(crate) version: u32,
}

impl Default for Material {
    fn default() -> Self {
        Self {
            name: String::new(),
            shading: ShadingModel::Pbr,
            alpha_mode: AlphaMode::Opaque,
            cull_mode: CullMode::Back,
            base_color: Vec4::ONE,
            emissive: Vec3::ZERO,
            emissive_intensity: 1.0,
            metallic: 0.0,
            roughness: 0.5,
            specular: Vec3::splat(0.04),
            shininess: 32.0,
            reflectance: 0.5,
            normal_scale: 1.0,
            occlusion_strength: 1.0,
            alpha_cutoff: 0.5,
            uv_scale: Vec2::ONE,
            uv_offset: Vec2::ZERO,
            depth_write: true,
            depth_test: true,
            wireframe: false,
            render_order: 0,
            textures: MaterialTextures::default(),
            version: 1,
        }
    }
}

impl Material {
    pub fn basic(color: Vec4) -> Self {
        Self {
            shading: ShadingModel::Basic,
            base_color: color,
            ..Default::default()
        }
    }

    pub fn pbr(color: Vec4, metallic: f32, roughness: f32) -> Self {
        Self {
            shading: ShadingModel::Pbr,
            base_color: color,
            metallic,
            roughness,
            ..Default::default()
        }
    }

    #[inline]
    pub fn is_transparent(&self) -> bool {
        self.alpha_mode == AlphaMode::Blend || self.base_color.w < 1.0
    }

    /// Marks the material dirty so its GPU uniform and bind group are rebuilt.
    #[inline]
    pub fn touch(&mut self) {
        self.version = self.version.wrapping_add(1).max(1);
    }

    #[inline]
    pub fn version(&self) -> u32 {
        self.version
    }
}
