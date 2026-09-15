//! Light sources. Lights are components attached to scene nodes, so their
//! position and direction come from the node world transform.

use crate::math::Vec3;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u32)]
pub enum LightKind {
    /// Infinitely distant light, direction taken from the node `-Z` axis.
    Directional = 0,
    /// Omnidirectional point light with inverse square falloff.
    Point = 1,
    /// Cone shaped light with a smooth penumbra.
    Spot = 2,
    /// Rectangular area light approximated as a diffuse disc.
    Area = 3,
    /// Constant term added to every fragment.
    Ambient = 4,
}

impl LightKind {
    pub fn from_u32(value: u32) -> Self {
        match value {
            1 => LightKind::Point,
            2 => LightKind::Spot,
            3 => LightKind::Area,
            4 => LightKind::Ambient,
            _ => LightKind::Directional,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Light {
    pub kind: LightKind,
    /// Linear RGB colour.
    pub color: Vec3,
    /// Multiplier applied to `color`; candela for point/spot, lux for directional.
    pub intensity: f32,
    /// Maximum reach of point and spot lights in world units. `0` means infinite.
    pub range: f32,
    /// Inner cone half angle in radians (spot lights).
    pub inner_cone_angle: f32,
    /// Outer cone half angle in radians (spot lights).
    pub outer_cone_angle: f32,
    /// Width and height of an area light.
    pub size: (f32, f32),
    pub cast_shadow: bool,
    pub enabled: bool,
}

impl Default for Light {
    fn default() -> Self {
        Self {
            kind: LightKind::Directional,
            color: Vec3::ONE,
            intensity: 1.0,
            range: 0.0,
            inner_cone_angle: std::f32::consts::FRAC_PI_8,
            outer_cone_angle: std::f32::consts::FRAC_PI_4,
            size: (1.0, 1.0),
            cast_shadow: false,
            enabled: true,
        }
    }
}

impl Light {
    pub fn directional(color: Vec3, intensity: f32) -> Self {
        Self {
            kind: LightKind::Directional,
            color,
            intensity,
            ..Default::default()
        }
    }

    pub fn point(color: Vec3, intensity: f32, range: f32) -> Self {
        Self {
            kind: LightKind::Point,
            color,
            intensity,
            range,
            ..Default::default()
        }
    }

    pub fn spot(color: Vec3, intensity: f32, range: f32, inner: f32, outer: f32) -> Self {
        Self {
            kind: LightKind::Spot,
            color,
            intensity,
            range,
            inner_cone_angle: inner,
            outer_cone_angle: outer,
            ..Default::default()
        }
    }

    pub fn ambient(color: Vec3, intensity: f32) -> Self {
        Self {
            kind: LightKind::Ambient,
            color,
            intensity,
            ..Default::default()
        }
    }
}
