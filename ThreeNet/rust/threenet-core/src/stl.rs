//! STL import, binary and ASCII.
//!
//! STL is a triangle soup and nothing else: no indices, no UVs, no materials,
//! no names. Every triangle carries its own three vertices and a face normal,
//! which is why an STL of a sphere has three times the vertices a glTF of the
//! same sphere would. That is the format, not a shortcoming of the reader.

use std::path::Path;

use crate::error::{Error, Result};
use crate::geometry::{Geometry, Vertex};
use crate::loaders::ImportResult;
use crate::material::Material;
use crate::math::{Vec2, Vec3};
use crate::scene::{NodeId, Scene};

/// Imports a `.stl` file into `scene` under `parent`.
pub fn load_stl(scene: &mut Scene, path: &str, parent: Option<NodeId>) -> Result<ImportResult> {
    let bytes = std::fs::read(path)?;
    let geometry = parse_stl(&bytes)?;
    let material = scene.add_material(Material::default());
    let geometry_id = scene.add_geometry(geometry);
    let node = scene.add_mesh(parent, geometry_id, material)?;
    if let Some(target) = scene.node_mut(node) {
        target.name = Path::new(path)
            .file_stem()
            .map(|n| n.to_string_lossy().to_string())
            .unwrap_or_default();
    }
    Ok(ImportResult {
        root: node,
        nodes: vec![node],
        geometries: vec![geometry_id],
        materials: vec![material],
        textures: Vec::new(),
        animations: Vec::new(),
        skins: Vec::new(),
    })
}

/// True when the buffer is binary STL.
///
/// The usual test - does it start with "solid"? - is wrong often enough to
/// matter, because plenty of exporters write "solid" into the 80 byte binary
/// header too. The length is decisive instead: a binary file is exactly 84
/// bytes of preamble plus 50 per triangle, and nothing else can be that size
/// by accident.
fn is_binary(bytes: &[u8]) -> bool {
    if bytes.len() < 84 {
        return false;
    }
    let count = u32::from_le_bytes([bytes[80], bytes[81], bytes[82], bytes[83]]) as usize;
    bytes.len() == 84 + count * 50
}

/// Parses STL from memory, choosing binary or ASCII by the content.
pub fn parse_stl(bytes: &[u8]) -> Result<Geometry> {
    if is_binary(bytes) {
        parse_binary(bytes)
    } else {
        let text = std::str::from_utf8(bytes)
            .map_err(|_| Error::Asset("STL is neither valid binary nor UTF-8 text".into()))?;
        parse_ascii(text)
    }
}

/// Builds the three vertices of one facet, falling back to the winding when the
/// stored normal is missing - which exporters write as zeroes more often than
/// not.
fn push_facet(vertices: &mut Vec<Vertex>, normal: Vec3, corners: [Vec3; 3]) {
    let normal = if normal.length_squared() > 1.0e-12 {
        normal.normalize()
    } else {
        let edge1 = corners[1] - corners[0];
        let edge2 = corners[2] - corners[0];
        let cross = edge1.cross(edge2);
        if cross.length_squared() > 1.0e-20 {
            cross.normalize()
        } else {
            // A degenerate triangle has no normal to compute. Keep it rather
            // than dropping it, so the triangle count still matches the file.
            Vec3::Y
        }
    };
    for corner in corners {
        vertices.push(Vertex::new(corner, normal, Vec2::ZERO));
    }
}

fn parse_binary(bytes: &[u8]) -> Result<Geometry> {
    let count = u32::from_le_bytes([bytes[80], bytes[81], bytes[82], bytes[83]]) as usize;
    let mut vertices: Vec<Vertex> = Vec::with_capacity(count * 3);
    let float = |at: usize| -> f32 {
        f32::from_le_bytes([bytes[at], bytes[at + 1], bytes[at + 2], bytes[at + 3]])
    };
    for triangle in 0..count {
        let base = 84 + triangle * 50;
        let read = |offset: usize| Vec3::new(float(base + offset), float(base + offset + 4), float(base + offset + 8));
        push_facet(
            &mut vertices,
            read(0),
            [read(12), read(24), read(36)],
        );
    }
    Ok(finish(vertices))
}

fn parse_ascii(text: &str) -> Result<Geometry> {
    let mut vertices: Vec<Vertex> = Vec::new();
    let mut normal = Vec3::ZERO;
    let mut corners: Vec<Vec3> = Vec::with_capacity(3);

    for line in text.lines() {
        let mut parts = line.split_whitespace();
        match parts.next() {
            Some("facet") => {
                // "facet normal nx ny nz"
                let values: Vec<f32> = parts.skip(1).filter_map(|v| v.parse().ok()).collect();
                normal = match values.as_slice() {
                    [x, y, z] => Vec3::new(*x, *y, *z),
                    _ => Vec3::ZERO,
                };
                corners.clear();
            }
            Some("vertex") => {
                let values: Vec<f32> = parts.filter_map(|v| v.parse().ok()).collect();
                let [x, y, z] = values.as_slice() else {
                    return Err(Error::Asset(format!("malformed STL vertex: '{line}'")));
                };
                corners.push(Vec3::new(*x, *y, *z));
            }
            Some("endfacet") => {
                // Anything other than a triangle is not an STL facet; skipping
                // it quietly would turn a corrupt file into a silently wrong
                // mesh.
                let [a, b, c] = corners.as_slice() else {
                    return Err(Error::Asset(format!(
                        "an STL facet needs three vertices, found {}",
                        corners.len()
                    )));
                };
                push_facet(&mut vertices, normal, [*a, *b, *c]);
                corners.clear();
            }
            _ => {}
        }
    }

    if vertices.is_empty() {
        return Err(Error::Asset("the STL file holds no triangles".into()));
    }
    Ok(finish(vertices))
}

fn finish(vertices: Vec<Vertex>) -> Geometry {
    // No index buffer: every triangle in an STL owns its corners outright, and
    // inventing shared vertices would smooth the flat shading the format is
    // meant to carry.
    let indices: Vec<u32> = (0..vertices.len() as u32).collect();
    let mut geometry = Geometry::new(vertices, indices);
    geometry.compute_tangents();
    geometry
}

#[cfg(test)]
mod tests {
    use super::*;

    fn binary_cube_facet() -> Vec<u8> {
        let mut bytes = vec![0u8; 80];
        bytes.extend_from_slice(&1u32.to_le_bytes());
        for value in [
            0.0f32, 0.0, 1.0, // normal
            0.0, 0.0, 0.0, // a
            1.0, 0.0, 0.0, // b
            0.0, 1.0, 0.0, // c
        ] {
            bytes.extend_from_slice(&value.to_le_bytes());
        }
        bytes.extend_from_slice(&0u16.to_le_bytes());
        bytes
    }

    #[test]
    fn reads_a_binary_triangle() {
        let geometry = parse_stl(&binary_cube_facet()).unwrap();
        assert_eq!(geometry.vertices.len(), 3);
        assert_eq!(geometry.indices, vec![0, 1, 2]);
        assert_eq!(geometry.vertices[0].normal, [0.0, 0.0, 1.0]);
    }

    #[test]
    fn a_binary_header_saying_solid_is_still_binary() {
        // The trap every naive STL reader falls into.
        let mut bytes = binary_cube_facet();
        bytes[..5].copy_from_slice(b"solid");
        assert!(is_binary(&bytes));
        assert_eq!(parse_stl(&bytes).unwrap().vertices.len(), 3);
    }

    #[test]
    fn reads_an_ascii_triangle() {
        let text = "solid test\n\
             facet normal 0 0 1\n\
             outer loop\n\
             vertex 0 0 0\n\
             vertex 1 0 0\n\
             vertex 0 1 0\n\
             endloop\n\
             endfacet\n\
             endsolid test\n";
        let geometry = parse_stl(text.as_bytes()).unwrap();
        assert_eq!(geometry.vertices.len(), 3);
        assert_eq!(geometry.vertices[2].position, [0.0, 1.0, 0.0]);
    }

    #[test]
    fn a_missing_normal_is_taken_from_the_winding() {
        let text = "solid test\n\
             facet normal 0 0 0\n\
             outer loop\n\
             vertex 0 0 0\n\
             vertex 1 0 0\n\
             vertex 0 1 0\n\
             endloop\n\
             endfacet\n\
             endsolid test\n";
        let geometry = parse_stl(text.as_bytes()).unwrap();
        assert_eq!(geometry.vertices[0].normal, [0.0, 0.0, 1.0]);
    }

    #[test]
    fn a_facet_short_of_a_vertex_is_an_error() {
        let text = "solid test\n\
             facet normal 0 0 1\n\
             outer loop\n\
             vertex 0 0 0\n\
             vertex 1 0 0\n\
             endloop\n\
             endfacet\n\
             endsolid test\n";
        assert!(parse_stl(text.as_bytes()).is_err());
    }
}
