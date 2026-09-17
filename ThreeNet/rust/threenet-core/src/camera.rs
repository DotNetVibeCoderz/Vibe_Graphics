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
    /// Asymmetric perspective given by the four half angles of the frustum
    /// (radians, OpenXR convention: `left` and `down` are negative). Used for
    /// stereo eyes and head mounted displays; ignores the target aspect.
    OffAxis {
        left: f32,
        right: f32,
        up: f32,
        down: f32,
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

    /// Off-axis perspective from frustum half angles (see [`Projection::OffAxis`]).
    pub fn off_axis(left: f32, right: f32, up: f32, down: f32, near: f32, far: f32) -> Self {
        Self {
            projection: Projection::OffAxis { left, right, up, down, near, far },
            viewport: None,
        }
    }

    #[inline]
    pub fn near(&self) -> f32 {
        match self.projection {
            Projection::Perspective { near, .. } | Projection::Orthographic { near, .. } | Projection::OffAxis { near, .. } => near,
        }
    }

    #[inline]
    pub fn far(&self) -> f32 {
        match self.projection {
            Projection::Perspective { far, .. } | Projection::Orthographic { far, .. } | Projection::OffAxis { far, .. } => far,
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
            Projection::OffAxis { left, right, up, down, near, far } => off_axis_matrix(left, right, up, down, near, far),
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
            Projection::Perspective { .. } | Projection::OffAxis { .. } => world.w_axis.truncate(),
            Projection::Orthographic { height, aspect, .. } => {
                let aspect = aspect.unwrap_or(target_aspect).max(1e-4);
                let half_h = height * 0.5;
                let local = Vec3::new(ndc.0 * half_h * aspect, ndc.1 * half_h, 0.0);
                world.transform_point3(local)
            }
        }
    }
}

/// Right handed off-centre perspective with a `0..1` depth range.
pub fn off_axis_matrix(left: f32, right: f32, up: f32, down: f32, near: f32, far: f32) -> Mat4 {
    let near = near.max(1e-4);
    let l = near * left.tan();
    let r = near * right.tan();
    let t = near * up.tan();
    let b = near * down.tan();
    let width = (r - l).max(1e-6);
    let height = (t - b).max(1e-6);
    let (depth_scale, depth_offset) = if far.is_infinite() {
        (-1.0, -near)
    } else {
        (far / (near - far), near * far / (near - far))
    };
    Mat4::from_cols(
        crate::math::Vec4::new(2.0 * near / width, 0.0, 0.0, 0.0),
        crate::math::Vec4::new(0.0, 2.0 * near / height, 0.0, 0.0),
        crate::math::Vec4::new((r + l) / width, (t + b) / height, depth_scale, -1.0),
        crate::math::Vec4::new(0.0, 0.0, depth_offset, 0.0),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn symmetric_off_axis_matches_perspective() {
        let fov = 1.0f32;
        let a = off_axis_matrix(-fov / 2.0, fov / 2.0, fov / 2.0, -fov / 2.0, 0.1, 100.0);
        let b = Camera::perspective(fov, 0.1, 100.0).projection_matrix(1.0);
        assert!(a.abs_diff_eq(b, 1e-4), "{a:?} vs {b:?}");
    }
}
