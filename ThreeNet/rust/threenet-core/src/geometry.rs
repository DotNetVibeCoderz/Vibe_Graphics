//! CPU side geometry: the interleaved vertex format and the primitive builders.

use bytemuck::{Pod, Zeroable};

use crate::math::{Aabb, Vec2, Vec3};

/// Interleaved vertex layout shared by every pipeline (48 bytes).
#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
pub struct Vertex {
    pub position: [f32; 3],
    pub normal: [f32; 3],
    pub uv: [f32; 2],
    /// `xyz` = tangent, `w` = handedness of the bitangent (+1 / -1).
    pub tangent: [f32; 4],
}

impl Default for Vertex {
    fn default() -> Self {
        Self {
            position: [0.0; 3],
            normal: [0.0, 1.0, 0.0],
            uv: [0.0; 2],
            tangent: [1.0, 0.0, 0.0, 1.0],
        }
    }
}

impl Vertex {
    pub const LAYOUT: wgpu::VertexBufferLayout<'static> = wgpu::VertexBufferLayout {
        array_stride: size_of::<Vertex>() as wgpu::BufferAddress,
        step_mode: wgpu::VertexStepMode::Vertex,
        attributes: &wgpu::vertex_attr_array![
            0 => Float32x3, // position
            1 => Float32x3, // normal
            2 => Float32x2, // uv
            3 => Float32x4, // tangent
        ],
    };

    #[inline]
    pub fn new(position: Vec3, normal: Vec3, uv: Vec2) -> Self {
        Self {
            position: position.to_array(),
            normal: normal.to_array(),
            uv: uv.to_array(),
            ..Default::default()
        }
    }
}

/// How the index buffer of a [`Geometry`] is stored.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Topology {
    TriangleList,
    LineList,
    PointList,
}

impl Topology {
    pub(crate) fn to_wgpu(self) -> wgpu::PrimitiveTopology {
        match self {
            Topology::TriangleList => wgpu::PrimitiveTopology::TriangleList,
            Topology::LineList => wgpu::PrimitiveTopology::LineList,
            Topology::PointList => wgpu::PrimitiveTopology::PointList,
        }
    }
}

/// CPU geometry. Uploaded to the GPU lazily by the renderer and re-uploaded
/// whenever [`Geometry::version`] changes.
#[derive(Debug, Clone)]
pub struct Geometry {
    pub name: String,
    pub vertices: Vec<Vertex>,
    pub indices: Vec<u32>,
    pub topology: Topology,
    pub bounds: Aabb,
    /// Joint indices, weights and bind pose for skinned meshes.
    pub skin: Option<crate::animation::SkinWeights>,
    pub(crate) version: u32,
}

impl Default for Geometry {
    fn default() -> Self {
        Self {
            name: String::new(),
            vertices: Vec::new(),
            indices: Vec::new(),
            topology: Topology::TriangleList,
            bounds: Aabb::EMPTY,
            skin: None,
            version: 1,
        }
    }
}

impl Geometry {
    pub fn new(vertices: Vec<Vertex>, indices: Vec<u32>) -> Self {
        let mut geometry = Self {
            vertices,
            indices,
            ..Default::default()
        };
        geometry.compute_bounds();
        geometry
    }

    /// Marks the geometry dirty so the renderer re-uploads it on the next frame.
    #[inline]
    pub fn touch(&mut self) {
        self.version = self.version.wrapping_add(1).max(1);
    }

    #[inline]
    pub fn version(&self) -> u32 {
        self.version
    }

    #[inline]
    pub fn index_count(&self) -> u32 {
        if self.indices.is_empty() {
            self.vertices.len() as u32
        } else {
            self.indices.len() as u32
        }
    }

    pub fn compute_bounds(&mut self) {
        let mut bounds = Aabb::EMPTY;
        for v in &self.vertices {
            bounds.expand(Vec3::from_array(v.position));
        }
        self.bounds = bounds;
    }

    /// Recomputes smooth per-vertex normals by area weighted face averaging.
    pub fn compute_normals(&mut self) {
        if self.topology != Topology::TriangleList {
            return;
        }
        for v in &mut self.vertices {
            v.normal = [0.0; 3];
        }
        let indices: Vec<u32> = if self.indices.is_empty() {
            (0..self.vertices.len() as u32).collect()
        } else {
            self.indices.clone()
        };
        for tri in indices.chunks_exact(3) {
            let (i0, i1, i2) = (tri[0] as usize, tri[1] as usize, tri[2] as usize);
            let p0 = Vec3::from_array(self.vertices[i0].position);
            let p1 = Vec3::from_array(self.vertices[i1].position);
            let p2 = Vec3::from_array(self.vertices[i2].position);
            // Not normalized on purpose: the magnitude is twice the triangle
            // area, which gives the area weighting for free.
            let face = (p1 - p0).cross(p2 - p0);
            for &i in &[i0, i1, i2] {
                let n = Vec3::from_array(self.vertices[i].normal) + face;
                self.vertices[i].normal = n.to_array();
            }
        }
        for v in &mut self.vertices {
            let n = Vec3::from_array(v.normal);
            v.normal = n.normalize_or(Vec3::Y).to_array();
        }
        self.touch();
    }

    /// Generates per-vertex tangents from the UV parameterisation (Lengyel).
    pub fn compute_tangents(&mut self) {
        if self.topology != Topology::TriangleList {
            return;
        }
        let indices: Vec<u32> = if self.indices.is_empty() {
            (0..self.vertices.len() as u32).collect()
        } else {
            self.indices.clone()
        };
        let mut tan = vec![Vec3::ZERO; self.vertices.len()];
        let mut bitan = vec![Vec3::ZERO; self.vertices.len()];
        for tri in indices.chunks_exact(3) {
            let (i0, i1, i2) = (tri[0] as usize, tri[1] as usize, tri[2] as usize);
            let p0 = Vec3::from_array(self.vertices[i0].position);
            let p1 = Vec3::from_array(self.vertices[i1].position);
            let p2 = Vec3::from_array(self.vertices[i2].position);
            let w0 = Vec2::from_array(self.vertices[i0].uv);
            let w1 = Vec2::from_array(self.vertices[i1].uv);
            let w2 = Vec2::from_array(self.vertices[i2].uv);
            let e1 = p1 - p0;
            let e2 = p2 - p0;
            let d1 = w1 - w0;
            let d2 = w2 - w0;
            let det = d1.x * d2.y - d2.x * d1.y;
            if det.abs() <= 1e-12 {
                continue;
            }
            let r = 1.0 / det;
            let t = (e1 * d2.y - e2 * d1.y) * r;
            let b = (e2 * d1.x - e1 * d2.x) * r;
            for &i in &[i0, i1, i2] {
                tan[i] += t;
                bitan[i] += b;
            }
        }
        for (i, v) in self.vertices.iter_mut().enumerate() {
            let n = Vec3::from_array(v.normal);
            // Gram-Schmidt orthogonalisation against the vertex normal.
            let t = (tan[i] - n * n.dot(tan[i])).normalize_or(
                n.cross(Vec3::Z)
                    .normalize_or(n.cross(Vec3::X).normalize_or(Vec3::X)),
            );
            let w = if n.cross(t).dot(bitan[i]) < 0.0 {
                -1.0
            } else {
                1.0
            };
            v.tangent = [t.x, t.y, t.z, w];
        }
        self.touch();
    }
}

/// Built-in primitive geometries, mirroring the Three.js catalogue.
pub mod primitives {
    use super::{Geometry, Vertex};
    use crate::math::{Vec2, Vec3};
    use std::f32::consts::{PI, TAU};

    fn finish(vertices: Vec<Vertex>, indices: Vec<u32>) -> Geometry {
        let mut geometry = Geometry::new(vertices, indices);
        geometry.compute_tangents();
        geometry
    }

    /// Subdivided plane on the XY plane facing `+Z`.
    pub fn plane(width: f32, height: f32, width_segments: u32, height_segments: u32) -> Geometry {
        let (sx, sy) = (width_segments.max(1), height_segments.max(1));
        let mut vertices = Vec::with_capacity(((sx + 1) * (sy + 1)) as usize);
        for y in 0..=sy {
            let v = y as f32 / sy as f32;
            for x in 0..=sx {
                let u = x as f32 / sx as f32;
                vertices.push(Vertex::new(
                    Vec3::new((u - 0.5) * width, (0.5 - v) * height, 0.0),
                    Vec3::Z,
                    Vec2::new(u, v),
                ));
            }
        }
        let mut indices = Vec::with_capacity((sx * sy * 6) as usize);
        for y in 0..sy {
            for x in 0..sx {
                let a = y * (sx + 1) + x;
                let b = a + 1;
                let c = a + sx + 1;
                let d = c + 1;
                indices.extend_from_slice(&[a, c, b, b, c, d]);
            }
        }
        finish(vertices, indices)
    }

    /// Box centred on the origin.
    pub fn cuboid(width: f32, height: f32, depth: f32, segments: u32) -> Geometry {
        let seg = segments.max(1);
        let (hw, hh, hd) = (width * 0.5, height * 0.5, depth * 0.5);
        let mut vertices = Vec::new();
        let mut indices = Vec::new();
        // (normal, u axis, v axis, half size along u/v, offset along normal)
        let faces = [
            (Vec3::X, Vec3::NEG_Z, Vec3::Y, hd, hh, hw),
            (Vec3::NEG_X, Vec3::Z, Vec3::Y, hd, hh, hw),
            (Vec3::Y, Vec3::X, Vec3::NEG_Z, hw, hd, hh),
            (Vec3::NEG_Y, Vec3::X, Vec3::Z, hw, hd, hh),
            (Vec3::Z, Vec3::X, Vec3::Y, hw, hh, hd),
            (Vec3::NEG_Z, Vec3::NEG_X, Vec3::Y, hw, hh, hd),
        ];
        for (normal, uaxis, vaxis, hu, hv, offset) in faces {
            let base = vertices.len() as u32;
            for y in 0..=seg {
                let fv = y as f32 / seg as f32;
                for x in 0..=seg {
                    let fu = x as f32 / seg as f32;
                    let position = uaxis * ((fu - 0.5) * 2.0 * hu)
                        + vaxis * ((0.5 - fv) * 2.0 * hv)
                        + normal * offset;
                    vertices.push(Vertex::new(position, normal, Vec2::new(fu, fv)));
                }
            }
            for y in 0..seg {
                for x in 0..seg {
                    let a = base + y * (seg + 1) + x;
                    let b = a + 1;
                    let c = a + seg + 1;
                    let d = c + 1;
                    indices.extend_from_slice(&[a, c, b, b, c, d]);
                }
            }
        }
        finish(vertices, indices)
    }

    /// UV sphere.
    pub fn sphere(radius: f32, width_segments: u32, height_segments: u32) -> Geometry {
        let (sx, sy) = (width_segments.max(3), height_segments.max(2));
        let mut vertices = Vec::with_capacity(((sx + 1) * (sy + 1)) as usize);
        for y in 0..=sy {
            let v = y as f32 / sy as f32;
            let phi = v * PI;
            let (sin_phi, cos_phi) = phi.sin_cos();
            for x in 0..=sx {
                let u = x as f32 / sx as f32;
                let theta = u * TAU;
                let (sin_theta, cos_theta) = theta.sin_cos();
                let normal = Vec3::new(-sin_phi * cos_theta, cos_phi, sin_phi * sin_theta);
                vertices.push(Vertex::new(normal * radius, normal, Vec2::new(u, v)));
            }
        }
        let mut indices = Vec::new();
        for y in 0..sy {
            for x in 0..sx {
                let a = y * (sx + 1) + x;
                let b = a + 1;
                let c = a + sx + 1;
                let d = c + 1;
                if y != 0 {
                    indices.extend_from_slice(&[a, c, b]);
                }
                if y != sy - 1 {
                    indices.extend_from_slice(&[b, c, d]);
                }
            }
        }
        finish(vertices, indices)
    }

    /// Cylinder / cone / truncated cone depending on the two radii.
    pub fn cylinder(
        radius_top: f32,
        radius_bottom: f32,
        height: f32,
        radial_segments: u32,
        height_segments: u32,
        capped: bool,
    ) -> Geometry {
        let radial = radial_segments.max(3);
        let heights = height_segments.max(1);
        let half = height * 0.5;
        let slope = (radius_bottom - radius_top) / height;
        let mut vertices = Vec::new();
        let mut indices = Vec::new();

        for y in 0..=heights {
            let v = y as f32 / heights as f32;
            let radius = radius_bottom + (radius_top - radius_bottom) * v;
            for x in 0..=radial {
                let u = x as f32 / radial as f32;
                let theta = u * TAU;
                let (sin_theta, cos_theta) = theta.sin_cos();
                let position = Vec3::new(radius * sin_theta, -half + v * height, radius * cos_theta);
                let normal = Vec3::new(sin_theta, slope, cos_theta).normalize();
                vertices.push(Vertex::new(position, normal, Vec2::new(u, 1.0 - v)));
            }
        }
        for y in 0..heights {
            for x in 0..radial {
                let a = y * (radial + 1) + x;
                let b = a + 1;
                let c = a + radial + 1;
                let d = c + 1;
                indices.extend_from_slice(&[a, b, c, b, d, c]);
            }
        }

        if capped {
            for (radius, y, normal) in [
                (radius_top, half, Vec3::Y),
                (radius_bottom, -half, Vec3::NEG_Y),
            ] {
                if radius <= 0.0 {
                    continue;
                }
                let center = vertices.len() as u32;
                vertices.push(Vertex::new(
                    Vec3::new(0.0, y, 0.0),
                    normal,
                    Vec2::new(0.5, 0.5),
                ));
                for x in 0..=radial {
                    let theta = x as f32 / radial as f32 * TAU;
                    let (sin_theta, cos_theta) = theta.sin_cos();
                    vertices.push(Vertex::new(
                        Vec3::new(radius * sin_theta, y, radius * cos_theta),
                        normal,
                        Vec2::new(sin_theta * 0.5 + 0.5, cos_theta * 0.5 + 0.5),
                    ));
                }
                for x in 0..radial {
                    let a = center + 1 + x;
                    let b = a + 1;
                    if normal.y > 0.0 {
                        indices.extend_from_slice(&[center, a, b]);
                    } else {
                        indices.extend_from_slice(&[center, b, a]);
                    }
                }
            }
        }
        finish(vertices, indices)
    }

    pub fn cone(radius: f32, height: f32, radial_segments: u32) -> Geometry {
        cylinder(0.0, radius, height, radial_segments, 1, true)
    }

    pub fn torus(
        radius: f32,
        tube: f32,
        radial_segments: u32,
        tubular_segments: u32,
        arc: f32,
    ) -> Geometry {
        let radial = radial_segments.max(3);
        let tubular = tubular_segments.max(3);
        let mut vertices = Vec::new();
        for j in 0..=radial {
            let v = j as f32 / radial as f32 * TAU;
            let (sin_v, cos_v) = v.sin_cos();
            for i in 0..=tubular {
                let u = i as f32 / tubular as f32 * arc;
                let (sin_u, cos_u) = u.sin_cos();
                let position = Vec3::new(
                    (radius + tube * cos_v) * cos_u,
                    (radius + tube * cos_v) * sin_u,
                    tube * sin_v,
                );
                let center = Vec3::new(radius * cos_u, radius * sin_u, 0.0);
                vertices.push(Vertex::new(
                    position,
                    (position - center).normalize(),
                    Vec2::new(i as f32 / tubular as f32, j as f32 / radial as f32),
                ));
            }
        }
        let mut indices = Vec::new();
        for j in 1..=radial {
            for i in 1..=tubular {
                let a = (tubular + 1) * j + i - 1;
                let b = (tubular + 1) * (j - 1) + i - 1;
                let c = (tubular + 1) * (j - 1) + i;
                let d = (tubular + 1) * j + i;
                indices.extend_from_slice(&[a, b, d, b, c, d]);
            }
        }
        finish(vertices, indices)
    }

    /// Line segments forming a unit grid on the XZ plane, handy for editors.
    pub fn grid(size: f32, divisions: u32) -> Geometry {
        let divisions = divisions.max(1);
        let half = size * 0.5;
        let step = size / divisions as f32;
        let mut vertices = Vec::new();
        for i in 0..=divisions {
            let offset = -half + i as f32 * step;
            for (a, b) in [
                (
                    Vec3::new(-half, 0.0, offset),
                    Vec3::new(half, 0.0, offset),
                ),
                (
                    Vec3::new(offset, 0.0, -half),
                    Vec3::new(offset, 0.0, half),
                ),
            ] {
                vertices.push(Vertex::new(a, Vec3::Y, Vec2::ZERO));
                vertices.push(Vertex::new(b, Vec3::Y, Vec2::ONE));
            }
        }
        let mut geometry = Geometry::new(vertices, Vec::new());
        geometry.topology = super::Topology::LineList;
        geometry
    }
}
