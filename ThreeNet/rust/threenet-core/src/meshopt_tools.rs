//! Mesh optimisation and simplification, on meshoptimizer.
//!
//! Two different jobs that happen to live in the same library:
//!
//! - **Optimise** reorders a mesh without changing what it looks like. Indices
//!   are reordered so that triangles sharing vertices are drawn near each other
//!   and the GPU's post-transform cache keeps hitting, and then the vertices
//!   are reordered to match the order the indices ask for them in, so fetching
//!   them walks memory forwards. Same triangles, same picture, fewer stalls.
//! - **Simplify** collapses edges to reach a triangle budget while holding the
//!   silhouette, which is how the levels for [`crate::scene::Node`]'s level of
//!   detail support get made without anyone modelling them by hand.
//!
//! Simplification is lossy and the amount of loss is not knowable in advance:
//! a mesh with no redundancy in it cannot be halved, and meshoptimizer will
//! return more triangles than asked for rather than destroy the shape. The
//! caller is told what it actually got.

use meshopt::{SimplifyOptions, VertexDataAdapter};

use crate::error::{Error, Result};
use crate::geometry::{Geometry, Vertex};

/// Position is the first field of a `Vertex`, so its offset is zero.
const POSITION_OFFSET: usize = 0;

fn adapter(vertices: &[Vertex]) -> Result<VertexDataAdapter<'_>> {
    let bytes: &[u8] = bytemuck::cast_slice(vertices);
    VertexDataAdapter::new(bytes, size_of::<Vertex>(), POSITION_OFFSET)
        .map_err(|error| Error::InvalidArgument(format!("cannot read the vertices: {error}")))
}

/// Reorders a mesh for the GPU without changing what it draws.
///
/// Returns false when there is nothing to do - a mesh with no index buffer is
/// already in the only order it has.
pub fn optimize(geometry: &mut Geometry) -> bool {
    if geometry.indices.is_empty() || geometry.vertices.is_empty() {
        return false;
    }

    // Cache order first: it decides the order vertices are first referenced in,
    // which is what the fetch pass then matches the vertex buffer to. Doing
    // them the other way round would leave the fetch order chasing an index
    // order that is about to change.
    let reordered = meshopt::optimize_vertex_cache(&geometry.indices, geometry.vertices.len());
    geometry.indices = reordered;

    let vertices = meshopt::optimize_vertex_fetch(&mut geometry.indices, &geometry.vertices);
    geometry.vertices = vertices;

    geometry.touch();
    true
}

/// What a simplification actually achieved.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SimplifyResult {
    pub triangles_before: usize,
    pub triangles_after: usize,
    /// How far the simplified mesh strays from the original, as a fraction of
    /// its size. Meshoptimizer's own estimate.
    pub error: f32,
}

/// Collapses a mesh towards `ratio` of its triangles, holding the silhouette.
///
/// `ratio` is a target rather than a promise. A mesh with nothing redundant in
/// it will come back larger than asked for, because the alternative is to
/// destroy its shape; the returned [`SimplifyResult`] says what happened.
///
/// `error_limit` caps how far the result may stray, as a fraction of the mesh
/// size - 0.01 is a tight budget and 0.1 a loose one. Simplification stops at
/// whichever limit is reached first.
pub fn simplify(geometry: &Geometry, ratio: f32, error_limit: f32) -> Result<(Geometry, SimplifyResult)> {
    if geometry.indices.is_empty() {
        return Err(Error::InvalidArgument(
            "a mesh with no index buffer cannot be simplified; its triangles are not shared".into(),
        ));
    }
    if !(0.0..=1.0).contains(&ratio) {
        return Err(Error::InvalidArgument(format!(
            "a simplification ratio is a fraction of the triangles to keep, got {ratio}"
        )));
    }

    let triangles_before = geometry.indices.len() / 3;
    let target = ((triangles_before as f32 * ratio).round() as usize).max(1) * 3;

    let mut error = 0.0f32;
    let indices = {
        let vertices = adapter(&geometry.vertices)?;
        meshopt::simplify(
            &geometry.indices,
            &vertices,
            target,
            error_limit.max(0.0),
            SimplifyOptions::None,
            Some(&mut error),
        )
    };

    // The simplifier keeps the original vertex buffer and just stops referring
    // to some of it, so a fetch pass is what actually makes the mesh smaller.
    let mut simplified = Geometry::new(geometry.vertices.clone(), indices);
    optimize(&mut simplified);
    simplified.compute_bounds();

    let result = SimplifyResult {
        triangles_before,
        triangles_after: simplified.indices.len() / 3,
        error,
    };
    Ok((simplified, result))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::geometry::primitives;

    #[test]
    fn optimising_keeps_every_triangle() {
        let mut geometry = primitives::sphere(1.0, 32, 24);
        let before = geometry.indices.len();
        let before_vertices = geometry.vertices.len();

        assert!(optimize(&mut geometry));

        // Reordering is not removal: every triangle has to survive.
        assert_eq!(geometry.indices.len(), before);
        // Vertices can go, though. The fetch pass compacts the buffer, so a
        // vertex no index refers to is dropped - which is a saving, not a loss,
        // and a sphere's poles leave a couple behind.
        assert!(
            geometry.vertices.len() <= before_vertices,
            "the fetch pass invented vertices: {before_vertices} then {}",
            geometry.vertices.len()
        );
    }

    #[test]
    fn optimising_keeps_the_shape() {
        let original = primitives::sphere(1.0, 24, 16);
        let mut optimised = original.clone();
        optimize(&mut optimised);

        // Every triangle of the original must still be present, as a set of
        // positions. A reorder that moved a vertex would fail this while
        // keeping the counts right.
        let gather = |geometry: &Geometry| {
            let mut faces: Vec<[[i32; 3]; 3]> = geometry
                .indices
                .chunks_exact(3)
                .map(|face| {
                    let mut corners: Vec<[i32; 3]> = face
                        .iter()
                        .map(|index| {
                            let p = geometry.vertices[*index as usize].position;
                            // Quantised, because the positions are carried
                            // through arithmetic and compared for identity.
                            [
                                (p[0] * 4096.0).round() as i32,
                                (p[1] * 4096.0).round() as i32,
                                (p[2] * 4096.0).round() as i32,
                            ]
                        })
                        .collect();
                    corners.sort_unstable();
                    [corners[0], corners[1], corners[2]]
                })
                .collect();
            faces.sort_unstable();
            faces
        };
        assert_eq!(gather(&original), gather(&optimised));
    }

    #[test]
    fn simplifying_removes_triangles() {
        let geometry = primitives::sphere(1.0, 48, 32);
        let (simplified, result) = simplify(&geometry, 0.25, 0.05).unwrap();

        assert!(
            result.triangles_after < result.triangles_before,
            "nothing was removed: {result:?}"
        );
        // A sphere is redundant enough that a quarter is reachable.
        assert!(
            result.triangles_after < result.triangles_before / 2,
            "barely simplified: {result:?}"
        );
        assert_eq!(simplified.indices.len() / 3, result.triangles_after);
    }

    #[test]
    fn simplifying_keeps_the_mesh_about_the_same_size() {
        let geometry = primitives::sphere(1.0, 48, 32);
        let (simplified, _) = simplify(&geometry, 0.3, 0.05).unwrap();

        // Holding the silhouette is the whole claim. A simplifier that
        // collapsed the mesh towards its centre would pass a triangle count
        // test and fail this one.
        let extent = |geometry: &Geometry| {
            let mut max = 0.0f32;
            for vertex in &geometry.vertices {
                let p = vertex.position;
                max = max.max((p[0] * p[0] + p[1] * p[1] + p[2] * p[2]).sqrt());
            }
            max
        };
        let original = extent(&geometry);
        let after = extent(&simplified);
        assert!(
            (after - original).abs() < original * 0.1,
            "the silhouette moved: {original} then {after}"
        );
    }

    #[test]
    fn an_unsimplifiable_mesh_comes_back_larger_than_asked_for() {
        // One triangle cannot become a quarter of a triangle. The honest
        // outcome is to return what survived, not to return nothing.
        let geometry = primitives::plane(1.0, 1.0, 1, 1);
        let (simplified, result) = simplify(&geometry, 0.1, 0.01).unwrap();
        assert!(result.triangles_after >= 1);
        assert!(!simplified.indices.is_empty());
    }

    #[test]
    fn the_arguments_are_checked() {
        let geometry = primitives::sphere(1.0, 8, 6);
        assert!(simplify(&geometry, -0.5, 0.01).is_err());
        assert!(simplify(&geometry, 2.0, 0.01).is_err());

        // No index buffer means no shared triangles to collapse.
        let loose = Geometry::new(geometry.vertices.clone(), Vec::new());
        assert!(simplify(&loose, 0.5, 0.01).is_err());
    }
}
