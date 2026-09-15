//! Cameras. Like lights, a camera is a component on a scene node: the view
//! matrix is the inverse of the node world matrix.

use crate::math::{Mat4, Vec3};

#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Projection {
    Perspective {
        /// Vertical field of view in radians.
        fov_y: f32,
        /// `None` keeps the aspect ratio in sync with the render target.
        aspect: Option<f32>,
        near: f32,
        far: f32,
    },
    Orthographic {
        /// Half height of the view volume; the width follows the aspect ratio.
        height: f32,
        aspect: Option<f32>,
        near: f32,
        far: f32,
    },
}

impl Default for Projection {
    fn default() -> Self {
        Projection::Perspective {
            fov_y: std::f32::consts::FRAC_PI_4,
            aspect: None,
            near: 0.1,
            far: 1000.0,
        }
    }
}

#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct Camera {
    pub projection: Projection,
    /// Viewport in normalised coordinates (x, y, width, height).
    pub viewport: Option<[f32; 4]>,
}

impl Camera {
    pub fn perspective(fov_y: f32, near: f32, far: f32) -> Self {
        Self {
            projection: Projection::Perspective {
                fov_y,
                aspect: None,
                near,
                far,
            },
            viewport: None,
        }
    }

    pub fn orthographic(height: f32, near: f32, far: f32) -> Self {
        Self {
            projection: Projection::Orthographic {
                height,
                aspect: None,
                near,
                far,
            },
            viewport: None,
        }
    }

    #[inline]
    pub fn near(&self) -> f32 {
        match self.projection {
            Projection::Perspective { near, .. } | Projection::Orthographic { near, .. } => near,
        }
    }

    #[inline]
    pub fn far(&self) -> f32 {
        match self.projection {
            Projection::Perspective { far, .. } | Projection::Orthographic { far, .. } => far,
        }
    }

    /// Builds the projection matrix for a target of `target_aspect` (w / h).
    /// The matrix maps to the wgpu clip space (depth `0..1`, `+Y` up).
    pub fn projection_matrix(&self, target_aspect: f32) -> Mat4 {
        match self.projection {
            Projection::Perspective {
                fov_y,
                aspect,
                near,
                far,
            } => {
                let aspect = aspect.unwrap_or(target_aspect).max(1e-4);
                // The `directx` projections use the `0..1` depth range and the
                // Y-up NDC convention that wgpu expects.
                if far.is_infinite() {
                    glam::camera::rh::proj::directx::perspective_infinite(fov_y, aspect, near)
                } else {
                    glam::camera::rh::proj::directx::perspective(fov_y, aspect, near, far)
                }
            }
            Projection::Orthographic {
                height,
                aspect,
                near,
                far,
            } => {
                let aspect = aspect.unwrap_or(target_aspect).max(1e-4);
                let half_h = height * 0.5;
                let half_w = half_h * aspect;
                glam::camera::rh::proj::directx::orthographic(
                    -half_w, half_w, -half_h, half_h, near, far,
                )
            }
        }
    }

    /// Ray direction (world space, normalised) through a normalised device
    /// coordinate in `[-1, 1]`, given the camera world matrix.
    pub fn ray_direction(&self, ndc: (f32, f32), world: &Mat4, target_aspect: f32) -> Vec3 {
        let proj = self.projection_matrix(target_aspect);
        let inv_proj = proj.inverse();
        let near_point = inv_proj * crate::math::Vec4::new(ndc.0, ndc.1, 0.0, 1.0);
        let far_point = inv_proj * crate::math::Vec4::new(ndc.0, ndc.1, 1.0, 1.0);
        let near_view = near_point.truncate() / near_point.w;
        let far_view = far_point.truncate() / far_point.w;
        let dir_view = (far_view - near_view).normalize_or(Vec3::NEG_Z);
        crate::math::Mat3::from_mat4(*world)
            .mul_vec3(dir_view)
            .normalize_or(Vec3::NEG_Z)
    }

    /// Ray origin in world space for a normalised device coordinate.
    pub fn ray_origin(&self, ndc: (f32, f32), world: &Mat4, target_aspect: f32) -> Vec3 {
        match self.projection {
            Projection::Perspective { .. } => world.w_axis.truncate(),
            Projection::Orthographic { height, aspect, .. } => {
                let aspect = aspect.unwrap_or(target_aspect).max(1e-4);
                let half_h = height * 0.5;
                let local = Vec3::new(ndc.0 * half_h * aspect, ndc.1 * half_h, 0.0);
                world.transform_point3(local)
            }
        }
    }
}
