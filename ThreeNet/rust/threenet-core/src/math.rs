//! Math helpers shared by the scene graph, the renderer and the C ABI.

pub use glam::{EulerRot, Mat3, Mat4, Quat, Vec2, Vec3, Vec4};

/// Position / rotation / scale triple used by every scene node.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Transform {
    pub translation: Vec3,
    pub rotation: Quat,
    pub scale: Vec3,
}

impl Default for Transform {
    fn default() -> Self {
        Self::IDENTITY
    }
}

impl Transform {
    pub const IDENTITY: Self = Self {
        translation: Vec3::ZERO,
        rotation: Quat::IDENTITY,
        scale: Vec3::ONE,
    };

    #[inline]
    pub fn from_translation(translation: Vec3) -> Self {
        Self {
            translation,
            ..Self::IDENTITY
        }
    }

    #[inline]
    pub fn matrix(&self) -> Mat4 {
        Mat4::from_scale_rotation_translation(self.scale, self.rotation, self.translation)
    }

    /// Rotate so that `-Z` points from `self.translation` towards `target`.
    pub fn look_at(&mut self, target: Vec3, up: Vec3) {
        let forward = target - self.translation;
        if forward.length_squared() <= f32::EPSILON {
            return;
        }
        // `look_to_quat` builds the view rotation; a node orientation is its inverse.
        let view = glam::camera::rh::view::look_to_quat(forward.normalize(), up);
        self.rotation = view.inverse().normalize();
    }

    /// Euler angles in radians using the `YXZ` convention (yaw, pitch, roll),
    /// which matches the ordering used by the managed API.
    #[inline]
    pub fn euler_angles(&self) -> Vec3 {
        let (y, x, z) = self.rotation.to_euler(EulerRot::YXZ);
        Vec3::new(x, y, z)
    }

    #[inline]
    pub fn set_euler_angles(&mut self, angles: Vec3) {
        self.rotation = Quat::from_euler(EulerRot::YXZ, angles.y, angles.x, angles.z);
    }
}

/// Axis aligned bounding box, used for culling and raycasting broad phase.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Aabb {
    pub min: Vec3,
    pub max: Vec3,
}

impl Default for Aabb {
    fn default() -> Self {
        Self::EMPTY
    }
}

impl Aabb {
    pub const EMPTY: Self = Self {
        min: Vec3::splat(f32::INFINITY),
        max: Vec3::splat(f32::NEG_INFINITY),
    };

    #[inline]
    pub fn is_empty(&self) -> bool {
        self.min.x > self.max.x || self.min.y > self.max.y || self.min.z > self.max.z
    }

    #[inline]
    pub fn expand(&mut self, point: Vec3) {
        self.min = self.min.min(point);
        self.max = self.max.max(point);
    }

    #[inline]
    pub fn union(&self, other: &Aabb) -> Aabb {
        if self.is_empty() {
            return *other;
        }
        if other.is_empty() {
            return *self;
        }
        Aabb {
            min: self.min.min(other.min),
            max: self.max.max(other.max),
        }
    }

    #[inline]
    pub fn center(&self) -> Vec3 {
        (self.min + self.max) * 0.5
    }

    #[inline]
    pub fn extents(&self) -> Vec3 {
        (self.max - self.min) * 0.5
    }

    #[inline]
    pub fn radius(&self) -> f32 {
        if self.is_empty() {
            0.0
        } else {
            self.extents().length()
        }
    }

    /// Transform the box by `matrix` and return the AABB of the result.
    pub fn transformed(&self, matrix: &Mat4) -> Aabb {
        if self.is_empty() {
            return *self;
        }
        let center = matrix.transform_point3(self.center());
        let e = self.extents();
        // Absolute value of the upper 3x3 block scaled by the extents.
        let m = Mat3::from_mat4(*matrix);
        let ex = m.x_axis.abs() * e.x;
        let ey = m.y_axis.abs() * e.y;
        let ez = m.z_axis.abs() * e.z;
        let extents = ex + ey + ez;
        Aabb {
            min: center - extents,
            max: center + extents,
        }
    }

    /// Slab test used by the raycaster. Returns the near hit distance.
    pub fn ray_intersection(&self, origin: Vec3, direction: Vec3) -> Option<f32> {
        let inv = direction.recip();
        let t1 = (self.min - origin) * inv;
        let t2 = (self.max - origin) * inv;
        let tmin = t1.min(t2);
        let tmax = t1.max(t2);
        let near = tmin.x.max(tmin.y).max(tmin.z);
        let far = tmax.x.min(tmax.y).min(tmax.z);
        if far < near.max(0.0) {
            None
        } else {
            Some(near.max(0.0))
        }
    }
}

/// Six view frustum planes in `Ax + By + Cz + D = 0` form, extracted from a
/// view-projection matrix (Gribb/Hartmann).
#[derive(Debug, Clone, Copy)]
pub struct Frustum {
    planes: [Vec4; 6],
}

impl Frustum {
    pub fn from_view_projection(view_projection: &Mat4) -> Self {
        let m = view_projection.to_cols_array_2d();
        // Row vectors of the matrix (glam stores columns).
        let row = |i: usize| Vec4::new(m[0][i], m[1][i], m[2][i], m[3][i]);
        let (r0, r1, r2, r3) = (row(0), row(1), row(2), row(3));
        let mut planes = [
            r3 + r0, // left
            r3 - r0, // right
            r3 + r1, // bottom
            r3 - r1, // top
            r2,      // near (wgpu depth range is 0..1)
            r3 - r2, // far
        ];
        for plane in &mut planes {
            let len = plane.truncate().length();
            if len > f32::EPSILON {
                *plane /= len;
            }
        }
        Self { planes }
    }

    /// Conservative sphere test; `false` means the sphere is fully outside.
    pub fn intersects_sphere(&self, center: Vec3, radius: f32) -> bool {
        self.planes
            .iter()
            .all(|p| p.truncate().dot(center) + p.w >= -radius)
    }

    pub fn intersects_aabb(&self, aabb: &Aabb) -> bool {
        !aabb.is_empty() && self.intersects_sphere(aabb.center(), aabb.radius())
    }
}
