//! VRML 2.0 / VRML97 (`.wrl`) import.
//!
//! VRML is not XML, whatever its age suggests - it is its own curly brace
//! syntax, so this needs no parser dependency, only a tokeniser.
//!
//! Only the geometry is read, and only `IndexedFaceSet`, which is where VRML
//! puts meshes: a `Coordinate { point [...] }` of positions and a `coordIndex`
//! of faces terminated by -1. Everything else in the language - transforms,
//! sensors, scripts, interpolators, inlined files - is skipped, because a
//! viewer for that is a browser and not a loader.

use std::path::Path;

use crate::error::{Error, Result};
use crate::geometry::{Geometry, Vertex};
use crate::loaders::ImportResult;
use crate::material::Material;
use crate::math::{Vec2, Vec3};
use crate::scene::{NodeId, Scene};

/// One mesh out of the file.
pub struct VrmlMesh {
    pub name: String,
    pub geometry: Geometry,
}

/// Splits VRML into words, treating its punctuation as separate tokens and
/// dropping comments and commas, which VRML allows anywhere as whitespace.
fn tokenise(text: &str) -> Vec<String> {
    let mut tokens = Vec::new();
    let mut current = String::new();
    let mut in_comment = false;

    for character in text.chars() {
        if in_comment {
            if character == '\n' {
                in_comment = false;
            }
            continue;
        }
        match character {
            '#' => {
                // A hash starts a comment, except in the header line, which is
                // skipped wholesale before this runs.
                if !current.is_empty() {
                    tokens.push(std::mem::take(&mut current));
                }
                in_comment = true;
            }
            '{' | '}' | '[' | ']' => {
                if !current.is_empty() {
                    tokens.push(std::mem::take(&mut current));
                }
                tokens.push(character.to_string());
            }
            ',' => {
                // A comma is whitespace in VRML, not a separator that means
                // anything.
                if !current.is_empty() {
                    tokens.push(std::mem::take(&mut current));
                }
            }
            c if c.is_whitespace() => {
                if !current.is_empty() {
                    tokens.push(std::mem::take(&mut current));
                }
            }
            c => current.push(c),
        }
    }
    if !current.is_empty() {
        tokens.push(current);
    }
    tokens
}

/// Reads the numbers out of a `[ ... ]` block starting at `at`, which must be
/// the index of the opening bracket. Returns the values and where to continue.
fn read_block(tokens: &[String], at: usize) -> (Vec<String>, usize) {
    let mut values = Vec::new();
    let mut index = at;
    if tokens.get(index).map(String::as_str) != Some("[") {
        // A single valued field, which VRML allows without brackets.
        if let Some(token) = tokens.get(index) {
            values.push(token.clone());
        }
        return (values, index + 1);
    }
    index += 1;
    while index < tokens.len() && tokens[index] != "]" {
        values.push(tokens[index].clone());
        index += 1;
    }
    (values, index + 1)
}

/// Parses VRML from memory.
pub fn parse_vrml(text: &str) -> Result<Vec<VrmlMesh>> {
    if !text.trim_start().starts_with("#VRML") {
        return Err(Error::Asset("not a VRML file".into()));
    }
    // The header is the one line where a hash is not a comment, so it goes
    // before the tokeniser sees anything.
    let body = text.split_once('\n').map(|(_, rest)| rest).unwrap_or("");
    let tokens = tokenise(body);

    let mut meshes = Vec::new();
    let mut positions: Vec<Vec3> = Vec::new();
    let mut faces: Vec<i64>;
    let mut name = String::new();
    let mut index = 0usize;

    while index < tokens.len() {
        match tokens[index].as_str() {
            "DEF" => {
                // "DEF name Node {" - the name is worth keeping.
                name = tokens.get(index + 1).cloned().unwrap_or_default();
                index += 1;
            }
            "point" => {
                let (values, next) = read_block(&tokens, index + 1);
                let numbers: Vec<f32> = values.iter().filter_map(|v| v.parse().ok()).collect();
                positions = numbers
                    .chunks_exact(3)
                    .map(|chunk| Vec3::new(chunk[0], chunk[1], chunk[2]))
                    .collect();
                index = next;
                continue;
            }
            "coordIndex" => {
                let (values, next) = read_block(&tokens, index + 1);
                faces = values.iter().filter_map(|v| v.parse().ok()).collect();
                index = next;

                if !positions.is_empty() && !faces.is_empty() {
                    meshes.push(VrmlMesh {
                        name: if name.is_empty() {
                            format!("mesh{}", meshes.len())
                        } else {
                            std::mem::take(&mut name)
                        },
                        geometry: build(&positions, &faces),
                    });
                    positions.clear();
                }
                continue;
            }
            _ => {}
        }
        index += 1;
    }

    if meshes.is_empty() {
        return Err(Error::Asset("the VRML file holds no IndexedFaceSet".into()));
    }
    Ok(meshes)
}

/// Turns a `coordIndex` list into triangles. Faces are terminated by -1 and may
/// have any number of corners, so each is fanned.
fn build(positions: &[Vec3], faces: &[i64]) -> Geometry {
    let vertices: Vec<Vertex> = positions
        .iter()
        .map(|position| Vertex::new(*position, Vec3::Y, Vec2::ZERO))
        .collect();
    let count = vertices.len() as i64;

    let mut indices: Vec<u32> = Vec::new();
    let mut face: Vec<u32> = Vec::new();
    for value in faces {
        if *value < 0 {
            for i in 1..face.len().saturating_sub(1) {
                indices.push(face[0]);
                indices.push(face[i]);
                indices.push(face[i + 1]);
            }
            face.clear();
            continue;
        }
        if *value < count {
            face.push(*value as u32);
        }
    }
    // A file whose last face is not terminated is common enough to allow.
    for i in 1..face.len().saturating_sub(1) {
        indices.push(face[0]);
        indices.push(face[i]);
        indices.push(face[i + 1]);
    }

    let mut geometry = Geometry::new(vertices, indices);
    // VRML can carry normals, but only as another indexed set; computing them
    // is both simpler and right for the smooth shading VRML assumes.
    geometry.compute_normals();
    geometry.compute_tangents();
    geometry
}

/// Imports a `.wrl` file into `scene` under `parent`.
pub fn load_vrml(scene: &mut Scene, path: &str, parent: Option<NodeId>) -> Result<ImportResult> {
    let text = std::fs::read_to_string(path)?;
    let meshes = parse_vrml(&text)?;
    let material = scene.add_material(Material::default());

    let root = scene.create_node(parent)?;
    if let Some(node) = scene.node_mut(root) {
        node.name = Path::new(path)
            .file_stem()
            .map(|n| n.to_string_lossy().to_string())
            .unwrap_or_default();
    }

    let mut nodes = vec![root];
    let mut geometries = Vec::new();
    for mesh in meshes {
        let geometry_id = scene.add_geometry(mesh.geometry);
        let node = scene.add_mesh(Some(root), geometry_id, material)?;
        if let Some(target) = scene.node_mut(node) {
            target.name = mesh.name;
        }
        nodes.push(node);
        geometries.push(geometry_id);
    }

    Ok(ImportResult {
        root,
        nodes,
        geometries,
        materials: vec![material],
        textures: Vec::new(),
        animations: Vec::new(),
        skins: Vec::new(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    const TRIANGLE: &str = "#VRML V2.0 utf8\n\
        DEF Shape1 Shape {\n\
          geometry IndexedFaceSet {\n\
            coord Coordinate {\n\
              point [ 0 0 0, 1 0 0, 0 1 0 ]\n\
            }\n\
            coordIndex [ 0, 1, 2, -1 ]\n\
          }\n\
        }\n";

    #[test]
    fn reads_a_triangle() {
        let meshes = parse_vrml(TRIANGLE).unwrap();
        assert_eq!(meshes.len(), 1);
        assert_eq!(meshes[0].name, "Shape1");
        assert_eq!(meshes[0].geometry.vertices.len(), 3);
        assert_eq!(meshes[0].geometry.indices, vec![0, 1, 2]);
    }

    #[test]
    fn a_quad_face_is_fanned() {
        let quad = TRIANGLE
            .replace("point [ 0 0 0, 1 0 0, 0 1 0 ]", "point [ 0 0 0, 1 0 0, 1 1 0, 0 1 0 ]")
            .replace("coordIndex [ 0, 1, 2, -1 ]", "coordIndex [ 0, 1, 2, 3, -1 ]");
        let meshes = parse_vrml(&quad).unwrap();
        assert_eq!(meshes[0].geometry.indices.len(), 6);
    }

    #[test]
    fn comments_are_not_geometry() {
        // A hash inside the body starts a comment, and a comment holding
        // something that looks like a field must not be read as one.
        let commented = TRIANGLE.replace(
            "coordIndex [ 0, 1, 2, -1 ]",
            "# point [ 9 9 9 ]\n            coordIndex [ 0, 1, 2, -1 ]",
        );
        let meshes = parse_vrml(&commented).unwrap();
        assert_eq!(meshes[0].geometry.vertices.len(), 3);
        assert_eq!(meshes[0].geometry.vertices[0].position, [0.0, 0.0, 0.0]);
    }

    #[test]
    fn commas_are_whitespace() {
        // VRML allows commas anywhere or nowhere; both have to parse the same.
        let spaced = TRIANGLE
            .replace("point [ 0 0 0, 1 0 0, 0 1 0 ]", "point [ 0 0 0 1 0 0 0 1 0 ]")
            .replace("coordIndex [ 0, 1, 2, -1 ]", "coordIndex [ 0 1 2 -1 ]");
        let meshes = parse_vrml(&spaced).unwrap();
        assert_eq!(meshes[0].geometry.indices, vec![0, 1, 2]);
    }

    #[test]
    fn a_face_pointing_past_the_points_is_dropped() {
        let broken = TRIANGLE.replace("coordIndex [ 0, 1, 2, -1 ]", "coordIndex [ 0, 1, 7, -1 ]");
        let meshes = parse_vrml(&broken).unwrap();
        // Two surviving corners cannot make a triangle.
        assert!(meshes[0].geometry.indices.is_empty());
    }

    #[test]
    fn something_that_is_not_vrml_is_an_error() {
        assert!(parse_vrml("<COLLADA/>").is_err());
        assert!(parse_vrml("#VRML V2.0 utf8\nTransform { }\n").is_err());
    }
}
