//! Ray casting used for object picking. The broad phase tests transformed
//! bounding boxes, the narrow phase runs Moeller-Trumbore per triangle.

use crate::geometry::Topology;
use crate::math::{Mat3, Vec2, Vec3};
use crate::scene::{NodeId, Scene};

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Ray {
    pub origin: Vec3,
    pub direction: Vec3,
}

impl Ray {
    pub fn new(origin: Vec3, direction: Vec3) -> Self {
        Self {
            origin,
            direction: direction.normalize_or(Vec3::NEG_Z),
        }
    }

    #[inline]
    pub fn at(&self, distance: f32) -> Vec3 {
        self.origin + self.direction * distance
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RayHit {
    pub node: NodeId,
    /// Distance along the ray in world units.
    pub distance: f32,
    pub point: Vec3,
    pub normal: Vec3,
    /// Index of the hit triangle within the geometry.
    pub triangle: u32,
    /// Barycentric coordinates, useful to interpolate vertex attributes.
    pub barycentric: Vec2,
}

/// Options narrowing which nodes take part in a cast.
#[derive(Debug, Clone, Copy)]
pub struct RaycastOptions {
    pub max_distance: f32,
    /// Only nodes whose layer mask overlaps take part.
    pub layers: u32,
    /// Skip nodes hidden by [`crate::scene::Node::visible`].
    pub visible_only: bool,
    /// Also report hits on back faces.
    pub include_back_faces: bool,
}

impl Default for RaycastOptions {
    fn default() -> Self {
        Self {
            max_distance: f32::INFINITY,
            layers: u32::MAX,
            visible_only: true,
            include_back_faces: false,
        }
    }
}

/// Casts `ray` against every mesh in the scene, returning hits sorted by
/// distance (nearest first).
pub fn raycast(scene: &mut Scene, ray: &Ray, options: &RaycastOptions) -> Vec<RayHit> {
    scene.update_world_transforms();
    let mut hits = Vec::new();

    let candidates: Vec<NodeId> = scene
        .nodes()
        .filter(|(_, node)| node.mesh.is_some() && node.layers & options.layers != 0)
        .map(|(id, _)| id)
        .collect();

    for id in candidates {
        if options.visible_only && !scene.is_visible_in_hierarchy(id) {
            continue;
        }
        let Some(node) = scene.node(id) else { continue };
        let Some(binding) = node.mesh else { continue };
        let Some(geometry) = scene.geometry(binding.geometry) else {
            continue;
        };
        if geometry.topology != Topology::TriangleList || geometry.vertices.is_empty() {
            continue;
        }

        let world = node.world_matrix();
        // Broad phase in world space.
        let bounds = geometry.bounds.transformed(&world);
        match bounds.ray_intersection(ray.origin, ray.direction) {
            Some(distance) if distance <= options.max_distance => {}
            _ => continue,
        }

        // Narrow phase in object space: transforming the ray is cheaper than
        // transforming every vertex.
        let inverse = world.inverse();
        let local_origin = inverse.transform_point3(ray.origin);
        let local_direction = Mat3::from_mat4(inverse).mul_vec3(ray.direction);
        // Object space direction is not normalised, so distances come out in
        // world units directly.

        let indices: Vec<u32> = if geometry.indices.is_empty() {
            (0..geometry.vertices.len() as u32).collect()
        } else {
            geometry.indices.clone()
        };

        for (triangle, tri) in indices.chunks_exact(3).enumerate() {
            let v0 = Vec3::from_array(geometry.vertices[tri[0] as usize].position);
            let v1 = Vec3::from_array(geometry.vertices[tri[1] as usize].position);
            let v2 = Vec3::from_array(geometry.vertices[tri[2] as usize].position);

            let Some((distance, u, v)) = intersect_triangle(
                local_origin,
                local_direction,
                v0,
                v1,
                v2,
                options.include_back_faces,
            ) else {
                continue;
            };
            if distance < 0.0 || distance > options.max_distance {
                continue;
            }

            let local_point = local_origin + local_direction * distance;
            let point = world.transform_point3(local_point);
            let local_normal = (v1 - v0).cross(v2 - v0).normalize_or(Vec3::Y);
            let normal = Mat3::from_mat4(world)
                .inverse()
                .transpose()
                .mul_vec3(local_normal)
                .normalize_or(Vec3::Y);
            hits.push(RayHit {
                node: id,
                distance,
                point,
                normal,
                triangle: triangle as u32,
                barycentric: Vec2::new(u, v),
            });
        }
    }

    hits.sort_by(|a, b| a.distance.total_cmp(&b.distance));
    hits
}

/// Moeller-Trumbore. Returns `(distance, u, v)` in the triangle plane.
fn intersect_triangle(
    origin: Vec3,
    direction: Vec3,
    v0: Vec3,
    v1: Vec3,
    v2: Vec3,
    include_back_faces: bool,
) -> Option<(f32, f32, f32)> {
    const EPSILON: f32 = 1e-7;
    let edge1 = v1 - v0;
    let edge2 = v2 - v0;
    let h = direction.cross(edge2);
    let determinant = edge1.dot(h);

    if include_back_faces {
        if determinant.abs() < EPSILON {
            return None;
        }
    } else if determinant < EPSILON {
        // Negative determinant means the ray hits the back face.
        return None;
    }

    let inv_determinant = 1.0 / determinant;
    let s = origin - v0;
    let u = s.dot(h) * inv_determinant;
    if !(0.0..=1.0).contains(&u) {
        return None;
    }
    let q = s.cross(edge1);
    let v = direction.dot(q) * inv_determinant;
    if v < 0.0 || u + v > 1.0 {
        return None;
    }
    let distance = edge2.dot(q) * inv_determinant;
    Some((distance, u, v))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::geometry::primitives;
    use crate::material::Material;

    #[test]
    fn ray_hits_a_cube_in_front_of_the_camera() {
        let mut scene = Scene::new();
        let geometry = scene.add_geometry(primitives::cuboid(1.0, 1.0, 1.0, 1));
        let material = scene.add_material(Material::default());
        let node = scene.add_mesh(None, geometry, material).unwrap();

        let ray = Ray::new(Vec3::new(0.0, 0.0, 5.0), Vec3::NEG_Z);
        let hits = raycast(&mut scene, &ray, &RaycastOptions::default());
        assert!(!hits.is_empty());
        assert_eq!(hits[0].node, node);
        assert!((hits[0].distance - 4.5).abs() < 1e-3);
    }

    #[test]
    fn ray_misses_when_pointing_away() {
        let mut scene = Scene::new();
        let geometry = scene.add_geometry(primitives::cuboid(1.0, 1.0, 1.0, 1));
        let material = scene.add_material(Material::default());
        scene.add_mesh(None, geometry, material).unwrap();

        let ray = Ray::new(Vec3::new(0.0, 0.0, 5.0), Vec3::Z);
        assert!(raycast(&mut scene, &ray, &RaycastOptions::default()).is_empty());
    }
}
