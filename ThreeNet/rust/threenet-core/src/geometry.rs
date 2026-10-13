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
    /// Blend shapes and the rest pose they move away from.
    pub morph: Option<MorphTargets>,
    pub(crate) version: u32,
}

/// One blend shape: what it adds to each vertex at full weight.
///
/// Deltas are sparse in practice but stored densely, one entry per vertex, which
/// keeps the deformation a flat loop. Normals are optional; without them the
/// shading follows the rest pose.
#[derive(Debug, Clone, Default)]
pub struct MorphTarget {
    pub name: String,
    pub positions: Vec<Vec3>,
    pub normals: Vec<Vec3>,
}

/// The blend shapes of one geometry, plus the pose they deform from.
#[derive(Debug, Clone, Default)]
pub struct MorphTargets {
    /// Vertices with every weight at zero. Skinning, when present, runs after
    /// this and uses its own bind pose.
    pub base: Vec<Vertex>,
    pub targets: Vec<MorphTarget>,
    /// Weights the vertices currently hold, so an unchanged pose costs nothing.
    pub(crate) applied: Vec<f32>,
}

impl MorphTargets {
    /// Blend shapes over a rest pose. The pose is what every weight of zero
    /// returns to, so it must be the vertices as they were authored.
    pub fn new(base: Vec<Vertex>, targets: Vec<MorphTarget>) -> Self {
        Self { base, targets, applied: Vec::new() }
    }

    #[inline]
    pub fn len(&self) -> usize {
        self.targets.len()
    }

    #[inline]
    pub fn is_empty(&self) -> bool {
        self.targets.is_empty()
    }

    pub fn name(&self, index: usize) -> &str {
        self.targets.get(index).map_or("", |target| target.name.as_str())
    }
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
            morph: None,
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

    /// Blends the morph targets into the vertices with the given weights.
    ///
    /// Returns false when there is nothing to do, including when the weights
    /// have not changed since the last call - a 20k vertex face is not worth
    /// rebuilding every frame for a pose that is standing still.
    pub fn apply_morph(&mut self, weights: &[f32]) -> bool {
        let Geometry { vertices, morph, skin, .. } = self;
        let Some(morph) = morph.as_mut() else {
            return false;
        };
        if morph.targets.is_empty() || morph.base.is_empty() {
            return false;
        }
        if morph.applied.len() == weights.len()
            && morph
                .applied
                .iter()
                .zip(weights)
                .all(|(a, b)| (a - b).abs() < 1.0e-4)
        {
            return false;
        }

        // A skinned mesh morphs its bind pose, and the skinning pass deforms
        // that afterwards. The rest pose lives in `morph.base`, so overwriting
        // the bind pose here is safe: the next call starts from the base again.
        let destination: &mut [Vertex] = match skin.as_mut() {
            Some(skin) => &mut skin.bind_pose,
            None => vertices,
        };

        let count = morph.base.len().min(destination.len());
        for index in 0..count {
            let base = &morph.base[index];
            let mut position = Vec3::from_array(base.position);
            let mut normal = Vec3::from_array(base.normal);
            for (target, weight) in morph.targets.iter().zip(weights.iter()) {
                if weight.abs() < 1.0e-4 {
                    continue;
                }
                if let Some(delta) = target.positions.get(index) {
                    position += *delta * *weight;
                }
                if let Some(delta) = target.normals.get(index) {
                    normal += *delta * *weight;
                }
            }
            let vertex = &mut destination[index];
            vertex.position = position.to_array();
            vertex.normal = normal.normalize_or(Vec3::Y).to_array();
        }

        morph.applied.clear();
        morph.applied.extend_from_slice(weights);
        true
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

    /// Filled disc on the XY plane facing `+Z`, as a fan around its centre.
    pub fn circle(radius: f32, segments: u32, theta_start: f32, theta_length: f32) -> Geometry {
        let segments = segments.max(3);
        let mut vertices = vec![Vertex::new(Vec3::ZERO, Vec3::Z, Vec2::new(0.5, 0.5))];
        for i in 0..=segments {
            let theta = theta_start + i as f32 / segments as f32 * theta_length;
            let (sin, cos) = theta.sin_cos();
            vertices.push(Vertex::new(
                Vec3::new(radius * cos, radius * sin, 0.0),
                Vec3::Z,
                Vec2::new((cos + 1.0) * 0.5, (sin + 1.0) * 0.5),
            ));
        }
        let mut indices = Vec::with_capacity((segments * 3) as usize);
        for i in 1..=segments {
            indices.extend_from_slice(&[0, i, i + 1]);
        }
        finish(vertices, indices)
    }

    /// A cylinder closed by a hemisphere at each end, standing on `Y`.
    ///
    /// `height` is the length of the straight part, so the whole capsule stands
    /// `height + 2 * radius` tall - the same convention the physics capsule uses.
    pub fn capsule(radius: f32, height: f32, radial_segments: u32, cap_rings: u32) -> Geometry {
        let radial = radial_segments.max(3);
        let rings = cap_rings.max(1);
        let half = height.max(0.0) * 0.5;
        // One row per cap latitude, plus the two seams where the caps meet the
        // straight part. The seams share a position but not a v, so the texture
        // does not smear across the join.
        let rows = rings * 2 + 1;
        let mut vertices = Vec::new();

        for row in 0..=rows {
            // phi walks from the north pole to the south pole.
            let (phi, y_offset) = if row <= rings {
                let t = row as f32 / rings as f32;
                (t * std::f32::consts::FRAC_PI_2, half)
            } else {
                let t = (row - rings - 1) as f32 / rings as f32;
                (
                    std::f32::consts::FRAC_PI_2 + t * std::f32::consts::FRAC_PI_2,
                    -half,
                )
            };
            let (sin_phi, cos_phi) = phi.sin_cos();
            let v = row as f32 / rows as f32;
            for i in 0..=radial {
                let theta = i as f32 / radial as f32 * TAU;
                let (sin, cos) = theta.sin_cos();
                let normal = Vec3::new(sin_phi * cos, cos_phi, sin_phi * sin);
                vertices.push(Vertex::new(
                    Vec3::new(radius * sin_phi * cos, y_offset + radius * cos_phi, radius * sin_phi * sin),
                    normal,
                    Vec2::new(i as f32 / radial as f32, v),
                ));
            }
        }

        let stride = radial + 1;
        let mut indices = Vec::new();
        for row in 0..rows {
            for i in 0..radial {
                let a = row * stride + i;
                let b = a + 1;
                let c = a + stride;
                let d = c + 1;
                indices.extend_from_slice(&[a, c, b, b, c, d]);
            }
        }
        finish(vertices, indices)
    }

    /// A (p, q) torus knot: a curve that winds `q` times around the torus while
    /// passing `p` times through its hole, inflated into a tube.
    pub fn torus_knot(
        radius: f32,
        tube: f32,
        tubular_segments: u32,
        radial_segments: u32,
        p: u32,
        q: u32,
    ) -> Geometry {
        let tubular = tubular_segments.max(3);
        let radial = radial_segments.max(3);
        let (p, q) = (p.max(1) as f32, q.max(1) as f32);

        // The point on the knot curve at parameter u.
        let curve = |u: f32| {
            let qu = q * u;
            let r = radius * (2.0 + qu.cos()) * 0.5;
            Vec3::new(r * (p * u).cos(), r * (p * u).sin(), radius * qu.sin() * 0.5)
        };

        let mut vertices = Vec::new();
        for i in 0..=tubular {
            let u = i as f32 / tubular as f32 * TAU;
            let point = curve(u);
            // A frame from the curve itself: the tangent from a step ahead, and
            // a normal from where the curve is bending rather than the second
            // derivative, which is enough for a knot that never straightens.
            let ahead = curve(u + 0.01);
            let tangent = (ahead - point).normalize();
            let bend = (ahead + point).normalize();
            let normal = bend.cross(tangent).normalize();
            let binormal = tangent.cross(normal);

            for j in 0..=radial {
                let v = j as f32 / radial as f32 * TAU;
                let (sin_v, cos_v) = v.sin_cos();
                let offset = normal * cos_v + binormal * sin_v;
                vertices.push(Vertex::new(
                    point + offset * tube,
                    offset,
                    Vec2::new(i as f32 / tubular as f32, j as f32 / radial as f32),
                ));
            }
        }

        let stride = radial + 1;
        let mut indices = Vec::new();
        for i in 0..tubular {
            for j in 0..radial {
                let a = i * stride + j;
                let b = a + 1;
                let c = a + stride;
                let d = c + 1;
                indices.extend_from_slice(&[a, c, b, b, c, d]);
            }
        }
        finish(vertices, indices)
    }

    /// A tube of constant radius swept along a path.
    ///
    /// The frame is carried along the path by parallel transport rather than by
    /// the Frenet formula, because a Frenet frame flips wherever the curve stops
    /// bending - one straight run in the middle of a path would twist the tube
    /// inside out.
    pub fn tube(path: &[Vec3], radius: f32, radial_segments: u32, closed: bool) -> Geometry {
        if path.len() < 2 {
            return Geometry::new(Vec::new(), Vec::new());
        }
        let radial = radial_segments.max(3);
        let count = path.len();

        let tangent_at = |i: usize| -> Vec3 {
            let next = if i + 1 < count {
                path[i + 1]
            } else if closed {
                path[0]
            } else {
                path[i]
            };
            let previous = if i > 0 {
                path[i - 1]
            } else if closed {
                path[count - 1]
            } else {
                path[i]
            };
            let direction = next - previous;
            if direction.length_squared() > 1.0e-12 {
                direction.normalize()
            } else {
                Vec3::Z
            }
        };

        // Start with any vector not parallel to the first tangent, then turn the
        // frame by exactly as much as the tangent turns at every step.
        let first = tangent_at(0);
        let seed = if first.x.abs() < 0.9 { Vec3::X } else { Vec3::Y };
        let mut normal = seed.cross(first).normalize();

        let mut vertices = Vec::new();
        for i in 0..count {
            let tangent = tangent_at(i);
            if i > 0 {
                let previous = tangent_at(i - 1);
                let axis = previous.cross(tangent);
                if axis.length_squared() > 1.0e-12 {
                    let angle = previous.dot(tangent).clamp(-1.0, 1.0).acos();
                    normal = crate::math::Quat::from_axis_angle(axis.normalize(), angle) * normal;
                }
                // Keep it perpendicular; the error accumulates over a long path.
                normal = (normal - tangent * normal.dot(tangent)).normalize();
            }
            let binormal = tangent.cross(normal);

            for j in 0..=radial {
                let v = j as f32 / radial as f32 * TAU;
                let (sin_v, cos_v) = v.sin_cos();
                let offset = normal * cos_v + binormal * sin_v;
                vertices.push(Vertex::new(
                    path[i] + offset * radius,
                    offset,
                    Vec2::new(i as f32 / (count - 1) as f32, j as f32 / radial as f32),
                ));
            }
        }

        let stride = radial + 1;
        let rings = if closed { count } else { count - 1 };
        let mut indices = Vec::new();
        for i in 0..rings {
            let here = i as u32 * stride;
            let next = ((i + 1) % count) as u32 * stride;
            for j in 0..radial {
                indices.extend_from_slice(&[
                    here + j,
                    next + j,
                    here + j + 1,
                    here + j + 1,
                    next + j,
                    next + j + 1,
                ]);
            }
        }
        finish(vertices, indices)
    }

    /// A profile revolved around the `Y` axis: a vase, a bowl, a wine glass.
    /// The `x` of each profile point is its distance from the axis.
    pub fn lathe(profile: &[Vec2], segments: u32, phi_start: f32, phi_length: f32) -> Geometry {
        if profile.len() < 2 {
            return Geometry::new(Vec::new(), Vec::new());
        }
        let segments = segments.max(3);
        let mut vertices = Vec::new();
        for i in 0..=segments {
            let t = i as f32 / segments as f32;
            let phi = phi_start + t * phi_length;
            let (sin, cos) = phi.sin_cos();
            for (j, point) in profile.iter().enumerate() {
                vertices.push(Vertex::new(
                    Vec3::new(point.x * cos, point.y, -point.x * sin),
                    // Averaged from the triangles below: a revolved profile has
                    // creases and flat caps that the profile alone cannot tell.
                    Vec3::Y,
                    Vec2::new(t, j as f32 / (profile.len() - 1) as f32),
                ));
            }
        }

        let stride = profile.len() as u32;
        let mut indices = Vec::new();
        for i in 0..segments {
            for j in 0..stride - 1 {
                let a = i * stride + j;
                let b = a + stride;
                indices.extend_from_slice(&[a, a + 1, b, b, a + 1, b + 1]);
            }
        }

        let mut geometry = Geometry::new(vertices, indices);
        geometry.compute_normals();
        geometry.compute_tangents();
        geometry
    }

    /// Triangulates a simple polygon by ear clipping, returning indices into
    /// `polygon` wound counter clockwise.
    fn triangulate(polygon: &[Vec2]) -> Vec<u32> {
        let count = polygon.len();
        if count < 3 {
            return Vec::new();
        }

        let area: f32 = (0..count)
            .map(|i| {
                let a = polygon[i];
                let b = polygon[(i + 1) % count];
                a.x * b.y - b.x * a.y
            })
            .sum();
        // Work counter clockwise whichever way the caller wound it.
        let mut remaining: Vec<usize> = if area < 0.0 {
            (0..count).rev().collect()
        } else {
            (0..count).collect()
        };

        let cross =
            |a: Vec2, b: Vec2, c: Vec2| (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        let mut indices = Vec::with_capacity((count - 2) * 3);
        let mut guard = count * count;

        while remaining.len() > 2 && guard > 0 {
            guard -= 1;
            let n = remaining.len();
            let mut clipped = false;
            for i in 0..n {
                let (ia, ib, ic) = (
                    remaining[(i + n - 1) % n],
                    remaining[i],
                    remaining[(i + 1) % n],
                );
                let (a, b, c) = (polygon[ia], polygon[ib], polygon[ic]);
                if cross(a, b, c) <= 0.0 {
                    // A reflex corner is never an ear.
                    continue;
                }
                // An ear holds no other vertex of the polygon.
                let mut contains = false;
                for &other in &remaining {
                    if other == ia || other == ib || other == ic {
                        continue;
                    }
                    let p = polygon[other];
                    if cross(a, b, p) >= 0.0 && cross(b, c, p) >= 0.0 && cross(c, a, p) >= 0.0 {
                        contains = true;
                        break;
                    }
                }
                if contains {
                    continue;
                }
                indices.extend_from_slice(&[ia as u32, ib as u32, ic as u32]);
                remaining.remove(i);
                clipped = true;
                break;
            }
            if !clipped {
                // Degenerate or self intersecting: keep what was clipped.
                break;
            }
        }
        indices
    }

    /// A closed 2D outline given depth along `Z`, with both caps filled.
    pub fn extrude(shape: &[Vec2], depth: f32) -> Geometry {
        if shape.len() < 3 {
            return Geometry::new(Vec::new(), Vec::new());
        }
        let half = depth * 0.5;
        let cap = triangulate(shape);
        let mut vertices = Vec::new();
        let mut indices = Vec::new();

        for (sign, normal) in [(1.0_f32, Vec3::Z), (-1.0_f32, Vec3::NEG_Z)] {
            let base = vertices.len() as u32;
            for point in shape {
                vertices.push(Vertex::new(
                    Vec3::new(point.x, point.y, half * sign),
                    normal,
                    Vec2::new(point.x, point.y),
                ));
            }
            for triangle in cap.chunks_exact(3) {
                if sign > 0.0 {
                    indices.extend_from_slice(&[
                        base + triangle[0],
                        base + triangle[1],
                        base + triangle[2],
                    ]);
                } else {
                    // The back cap faces the other way, so it winds the other way.
                    indices.extend_from_slice(&[
                        base + triangle[2],
                        base + triangle[1],
                        base + triangle[0],
                    ]);
                }
            }
        }

        let count = shape.len();
        for i in 0..count {
            let a = shape[i];
            let b = shape[(i + 1) % count];
            let edge = b - a;
            let normal = Vec3::new(edge.y, -edge.x, 0.0).normalize_or_zero();
            let u = i as f32 / count as f32;
            let next_u = (i + 1) as f32 / count as f32;
            let base = vertices.len() as u32;
            vertices.push(Vertex::new(Vec3::new(a.x, a.y, half), normal, Vec2::new(u, 0.0)));
            vertices.push(Vertex::new(Vec3::new(b.x, b.y, half), normal, Vec2::new(next_u, 0.0)));
            vertices.push(Vertex::new(Vec3::new(b.x, b.y, -half), normal, Vec2::new(next_u, 1.0)));
            vertices.push(Vertex::new(Vec3::new(a.x, a.y, -half), normal, Vec2::new(u, 1.0)));
            indices.extend_from_slice(&[base, base + 1, base + 2, base, base + 2, base + 3]);
        }

        finish(vertices, indices)
    }

    /// Projects a triangle soup onto a sphere, subdividing every face `detail`
    /// times first. Every platonic solid below is this with a different seed.
    pub fn polyhedron(points: &[Vec3], faces: &[u32], radius: f32, detail: u32) -> Geometry {
        let detail = detail.min(5);
        let subdivisions = 1u32 << detail;
        let mut vertices = Vec::new();
        let mut indices = Vec::new();

        for face in faces.chunks_exact(3) {
            let (a, b, c) = (
                points[face[0] as usize],
                points[face[1] as usize],
                points[face[2] as usize],
            );
            // Walk the triangle in rows: row i holds `subdivisions - i + 1` points.
            let base = vertices.len() as u32;
            for i in 0..=subdivisions {
                let ti = i as f32 / subdivisions as f32;
                let left = a.lerp(c, ti);
                let right = b.lerp(c, ti);
                let row = subdivisions - i;
                for j in 0..=row {
                    let tj = if row == 0 { 0.0 } else { j as f32 / row as f32 };
                    let point = left.lerp(right, tj).normalize() * radius;
                    let normal = point.normalize();
                    // Equirectangular uv, which is as good as a sphere gets
                    // without cutting a seam.
                    vertices.push(Vertex::new(
                        point,
                        normal,
                        Vec2::new(
                            normal.z.atan2(normal.x) / TAU + 0.5,
                            normal.y.clamp(-1.0, 1.0).acos() / PI,
                        ),
                    ));
                }
            }

            let mut row_start = base;
            for i in 0..subdivisions {
                let row = subdivisions - i;
                let next_start = row_start + row + 1;
                for j in 0..row {
                    indices.extend_from_slice(&[row_start + j, row_start + j + 1, next_start + j]);
                    if j + 1 < row {
                        indices.extend_from_slice(&[
                            row_start + j + 1,
                            next_start + j + 1,
                            next_start + j,
                        ]);
                    }
                }
                row_start = next_start;
            }
        }

        finish(vertices, indices)
    }

    pub fn tetrahedron(radius: f32, detail: u32) -> Geometry {
        let points = [
            Vec3::new(1.0, 1.0, 1.0),
            Vec3::new(-1.0, -1.0, 1.0),
            Vec3::new(-1.0, 1.0, -1.0),
            Vec3::new(1.0, -1.0, -1.0),
        ];
        let faces = [2, 1, 0, 0, 3, 2, 1, 3, 0, 2, 3, 1];
        polyhedron(&points, &faces, radius, detail)
    }

    pub fn octahedron(radius: f32, detail: u32) -> Geometry {
        let points = [Vec3::X, Vec3::NEG_X, Vec3::Y, Vec3::NEG_Y, Vec3::Z, Vec3::NEG_Z];
        let faces = [
            0, 2, 4, 0, 4, 3, 0, 3, 5, 0, 5, 2, 1, 2, 5, 1, 5, 3, 1, 3, 4, 1, 4, 2,
        ];
        polyhedron(&points, &faces, radius, detail)
    }

    pub fn icosahedron(radius: f32, detail: u32) -> Geometry {
        // Three mutually perpendicular golden rectangles.
        let t = (1.0 + 5.0_f32.sqrt()) * 0.5;
        let points = [
            Vec3::new(-1.0, t, 0.0),
            Vec3::new(1.0, t, 0.0),
            Vec3::new(-1.0, -t, 0.0),
            Vec3::new(1.0, -t, 0.0),
            Vec3::new(0.0, -1.0, t),
            Vec3::new(0.0, 1.0, t),
            Vec3::new(0.0, -1.0, -t),
            Vec3::new(0.0, 1.0, -t),
            Vec3::new(t, 0.0, -1.0),
            Vec3::new(t, 0.0, 1.0),
            Vec3::new(-t, 0.0, -1.0),
            Vec3::new(-t, 0.0, 1.0),
        ];
        let faces = [
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7,
            6, 7, 1, 8, 3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10,
            8, 6, 7, 9, 8, 1,
        ];
        polyhedron(&points, &faces, radius, detail)
    }

    pub fn dodecahedron(radius: f32, detail: u32) -> Geometry {
        let t = (1.0 + 5.0_f32.sqrt()) * 0.5;
        let r = 1.0 / t;
        let points = [
            Vec3::new(-1.0, -1.0, -1.0),
            Vec3::new(-1.0, -1.0, 1.0),
            Vec3::new(-1.0, 1.0, -1.0),
            Vec3::new(-1.0, 1.0, 1.0),
            Vec3::new(1.0, -1.0, -1.0),
            Vec3::new(1.0, -1.0, 1.0),
            Vec3::new(1.0, 1.0, -1.0),
            Vec3::new(1.0, 1.0, 1.0),
            Vec3::new(0.0, -r, -t),
            Vec3::new(0.0, -r, t),
            Vec3::new(0.0, r, -t),
            Vec3::new(0.0, r, t),
            Vec3::new(-r, -t, 0.0),
            Vec3::new(-r, t, 0.0),
            Vec3::new(r, -t, 0.0),
            Vec3::new(r, t, 0.0),
            Vec3::new(-t, 0.0, -r),
            Vec3::new(t, 0.0, -r),
            Vec3::new(-t, 0.0, r),
            Vec3::new(t, 0.0, r),
        ];
        let faces = [
            3, 11, 7, 3, 7, 15, 3, 15, 13, 7, 19, 17, 7, 17, 6, 7, 6, 15, 17, 4, 8, 17, 8, 10, 17,
            10, 6, 8, 0, 16, 8, 16, 2, 8, 2, 10, 0, 12, 1, 0, 1, 18, 0, 18, 16, 6, 10, 2, 6, 2, 13,
            6, 13, 15, 2, 16, 18, 2, 18, 3, 2, 3, 13, 18, 1, 9, 18, 9, 11, 18, 11, 3, 4, 14, 12, 4,
            12, 0, 4, 0, 8, 11, 9, 5, 11, 5, 19, 11, 19, 7, 19, 5, 14, 19, 14, 4, 19, 4, 17, 1, 12,
            14, 1, 14, 5, 1, 5, 9,
        ];
        polyhedron(&points, &faces, radius, detail)
    }
}

