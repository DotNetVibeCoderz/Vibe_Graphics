//! Autodesk 3D Studio `.3ds` import.
//!
//! Another tree of chunks, like VOX: a two byte id, a four byte length that
//! counts its own header, and then either data or more chunks. Walking it means
//! stepping into the containers you know and over the ones you do not, which is
//! what lets a reader this small survive files full of material, keyframe and
//! camera chunks it has no use for.
//!
//! The format is old enough to carry its own limits: vertex and face counts are
//! sixteen bit, so no single mesh can hold more than 65535 of either, and there
//! are no normals in the file at all - they are computed from the faces here.

use std::path::Path;

use crate::error::{Error, Result};
use crate::geometry::{Geometry, Vertex};
use crate::loaders::ImportResult;
use crate::material::Material;
use crate::math::{Vec2, Vec3};
use crate::scene::{NodeId, Scene};

const MAIN: u16 = 0x4D4D;
const EDITOR: u16 = 0x3D3D;
const OBJECT: u16 = 0x4000;
const TRIMESH: u16 = 0x4100;
const VERTICES: u16 = 0x4110;
const FACES: u16 = 0x4120;
const UVS: u16 = 0x4140;

/// One named mesh out of the file.
pub struct TdsMesh {
    pub name: String,
    pub geometry: Geometry,
}

struct Reader<'a> {
    bytes: &'a [u8],
    at: usize,
}

impl<'a> Reader<'a> {
    fn u16(&mut self) -> Result<u16> {
        if self.at + 2 > self.bytes.len() {
            return Err(Error::Asset("the 3DS file ended inside a value".into()));
        }
        let value = u16::from_le_bytes([self.bytes[self.at], self.bytes[self.at + 1]]);
        self.at += 2;
        Ok(value)
    }

    fn u32(&mut self) -> Result<u32> {
        if self.at + 4 > self.bytes.len() {
            return Err(Error::Asset("the 3DS file ended inside a value".into()));
        }
        let value = u32::from_le_bytes([
            self.bytes[self.at],
            self.bytes[self.at + 1],
            self.bytes[self.at + 2],
            self.bytes[self.at + 3],
        ]);
        self.at += 4;
        Ok(value)
    }

    fn f32(&mut self) -> Result<f32> {
        Ok(f32::from_bits(self.u32()?))
    }

    /// A null terminated name, which is how 3DS labels an object.
    fn name(&mut self) -> Result<String> {
        let start = self.at;
        while self.at < self.bytes.len() && self.bytes[self.at] != 0 {
            self.at += 1;
        }
        if self.at >= self.bytes.len() {
            return Err(Error::Asset("a 3DS name was never terminated".into()));
        }
        let text = String::from_utf8_lossy(&self.bytes[start..self.at]).into_owned();
        self.at += 1;
        Ok(text)
    }
}

/// Parses a `.3ds` file from memory into its meshes.
pub fn parse_tds(bytes: &[u8]) -> Result<Vec<TdsMesh>> {
    let mut reader = Reader { bytes, at: 0 };
    let id = reader.u16()?;
    if id != MAIN {
        return Err(Error::Asset("not a 3DS file".into()));
    }
    let length = reader.u32()? as usize;
    if length < 6 {
        return Err(Error::Asset("the 3DS main chunk is malformed".into()));
    }

    let mut meshes: Vec<TdsMesh> = Vec::new();
    let end = length.min(bytes.len());
    read_children(&mut reader, end, &mut meshes, None)?;

    if meshes.is_empty() {
        return Err(Error::Asset("the 3DS file holds no meshes".into()));
    }
    Ok(meshes)
}

/// Walks the chunks between the cursor and `end`.
///
/// `pending` carries the name of the object being built, because 3DS puts the
/// name on the container and the geometry in a child of it.
fn read_children(
    reader: &mut Reader<'_>,
    end: usize,
    meshes: &mut Vec<TdsMesh>,
    pending: Option<&str>,
) -> Result<()> {
    let mut positions: Vec<Vec3> = Vec::new();
    let mut uvs: Vec<Vec2> = Vec::new();
    let mut indices: Vec<u32> = Vec::new();
    let mut name = pending.unwrap_or_default().to_string();

    while reader.at + 6 <= end {
        let start = reader.at;
        let id = reader.u16()?;
        let length = reader.u32()? as usize;
        if length < 6 {
            return Err(Error::Asset("a 3DS chunk claims an impossible size".into()));
        }
        let stop = (start + length).min(end);

        match id {
            // Containers: step in.
            EDITOR => read_children(reader, stop, meshes, None)?,
            OBJECT => {
                let object_name = reader.name()?;
                read_children(reader, stop, meshes, Some(&object_name))?;
            }
            TRIMESH => {
                // The mesh chunks are children of this one, and the geometry is
                // assembled when it closes.
                let mut inner = Reader {
                    bytes: reader.bytes,
                    at: reader.at,
                };
                read_trimesh(&mut inner, stop, &mut positions, &mut uvs, &mut indices)?;
                if !positions.is_empty() {
                    meshes.push(TdsMesh {
                        name: std::mem::take(&mut name),
                        geometry: build(&positions, &uvs, &indices),
                    });
                }
                positions.clear();
                uvs.clear();
                indices.clear();
            }
            // Materials, keyframes, cameras and lights are stepped over. The
            // declared length is what makes that safe.
            _ => {}
        }

        reader.at = stop;
    }
    Ok(())
}

fn read_trimesh(
    reader: &mut Reader<'_>,
    end: usize,
    positions: &mut Vec<Vec3>,
    uvs: &mut Vec<Vec2>,
    indices: &mut Vec<u32>,
) -> Result<()> {
    while reader.at + 6 <= end {
        let start = reader.at;
        let id = reader.u16()?;
        let length = reader.u32()? as usize;
        if length < 6 {
            return Err(Error::Asset("a 3DS chunk claims an impossible size".into()));
        }
        let stop = (start + length).min(end);

        match id {
            VERTICES => {
                let count = reader.u16()? as usize;
                positions.reserve(count);
                for _ in 0..count {
                    let (x, y, z) = (reader.f32()?, reader.f32()?, reader.f32()?);
                    // 3DS is Z up; everything here is Y up.
                    positions.push(Vec3::new(x, z, -y));
                }
            }
            FACES => {
                let count = reader.u16()? as usize;
                indices.reserve(count * 3);
                for _ in 0..count {
                    let a = reader.u16()? as u32;
                    let b = reader.u16()? as u32;
                    let c = reader.u16()? as u32;
                    // The fourth value is an edge visibility flag, not an index.
                    let _flags = reader.u16()?;
                    indices.extend_from_slice(&[a, b, c]);
                }
            }
            UVS => {
                let count = reader.u16()? as usize;
                uvs.reserve(count);
                for _ in 0..count {
                    let (u, v) = (reader.f32()?, reader.f32()?);
                    // 3DS measures v from the bottom.
                    uvs.push(Vec2::new(u, 1.0 - v));
                }
            }
            _ => {}
        }

        reader.at = stop;
    }
    Ok(())
}

fn build(positions: &[Vec3], uvs: &[Vec2], indices: &[u32]) -> Geometry {
    let vertices: Vec<Vertex> = positions
        .iter()
        .enumerate()
        .map(|(index, position)| {
            let uv = uvs.get(index).copied().unwrap_or(Vec2::ZERO);
            // The normal is filled in below; 3DS carries none.
            Vertex::new(*position, Vec3::Y, uv)
        })
        .collect();

    // Drop any face that points outside the vertex list rather than letting it
    // index past the end later.
    let count = vertices.len() as u32;
    let safe: Vec<u32> = indices
        .chunks_exact(3)
        .filter(|face| face.iter().all(|index| *index < count))
        .flatten()
        .copied()
        .collect();

    let mut geometry = Geometry::new(vertices, safe);
    geometry.compute_normals();
    geometry.compute_tangents();
    geometry
}

/// Imports a `.3ds` file into `scene` under `parent`.
pub fn load_tds(scene: &mut Scene, path: &str, parent: Option<NodeId>) -> Result<ImportResult> {
    let bytes = std::fs::read(path)?;
    let meshes = parse_tds(&bytes)?;
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

    fn chunk(id: u16, body: &[u8]) -> Vec<u8> {
        let mut out = id.to_le_bytes().to_vec();
        out.extend_from_slice(&((body.len() + 6) as u32).to_le_bytes());
        out.extend_from_slice(body);
        out
    }

    /// A file with one triangle in one named object.
    fn triangle_file(name: &str) -> Vec<u8> {
        let mut vertices = 3u16.to_le_bytes().to_vec();
        // A floor in 3DS space: z is up there, so a flat triangle holds z at 0
        // and spreads over x and y.
        for position in [[0.0f32, 0.0, 0.0], [1.0, 0.0, 0.0], [0.0, 1.0, 0.0]] {
            for value in position {
                vertices.extend_from_slice(&value.to_le_bytes());
            }
        }

        let mut faces = 1u16.to_le_bytes().to_vec();
        for value in [0u16, 1, 2, 0] {
            faces.extend_from_slice(&value.to_le_bytes());
        }

        let mut uvs = 3u16.to_le_bytes().to_vec();
        for pair in [[0.0f32, 0.0], [1.0, 0.0], [0.0, 1.0]] {
            for value in pair {
                uvs.extend_from_slice(&value.to_le_bytes());
            }
        }

        let mut trimesh = chunk(VERTICES, &vertices);
        trimesh.extend(chunk(FACES, &faces));
        trimesh.extend(chunk(UVS, &uvs));

        let mut object = name.as_bytes().to_vec();
        object.push(0);
        object.extend(chunk(TRIMESH, &trimesh));

        let editor = chunk(OBJECT, &object);
        chunk(MAIN, &chunk(EDITOR, &editor))
    }

    #[test]
    fn reads_a_triangle() {
        let meshes = parse_tds(&triangle_file("box01")).unwrap();
        assert_eq!(meshes.len(), 1);
        assert_eq!(meshes[0].name, "box01");
        assert_eq!(meshes[0].geometry.vertices.len(), 3);
        assert_eq!(meshes[0].geometry.indices, vec![0, 1, 2]);
    }

    #[test]
    fn z_up_becomes_y_up() {
        // The third vertex is at y = 1 in the file. In 3DS that is forwards,
        // not up, so it must not arrive here pointing at the sky.
        let meshes = parse_tds(&triangle_file("a")).unwrap();
        let third = meshes[0].geometry.vertices[2].position;
        assert_eq!(third, [0.0, 0.0, -1.0]);
    }

    #[test]
    fn v_is_flipped() {
        let meshes = parse_tds(&triangle_file("a")).unwrap();
        // v = 0 in the file is the bottom of the image, which is v = 1 here.
        assert_eq!(meshes[0].geometry.vertices[0].uv, [0.0, 1.0]);
    }

    #[test]
    fn normals_are_computed_because_the_format_has_none() {
        let meshes = parse_tds(&triangle_file("a")).unwrap();
        let normal = meshes[0].geometry.vertices[0].normal;
        // A floor in the file is a floor here, so its normal runs along Y.
        assert!(normal[1].abs() > 0.99, "expected a vertical normal, got {normal:?}");
    }

    #[test]
    fn unknown_chunks_are_stepped_over() {
        // A material chunk in the middle has to leave the mesh after it intact,
        // which only works if the walk trusts the declared lengths.
        let mut editor = chunk(0xAFFF, &[1, 2, 3, 4, 5]);
        let mut object = b"mesh".to_vec();
        object.push(0);

        let mut vertices = 1u16.to_le_bytes().to_vec();
        for value in [0.0f32, 0.0, 0.0] {
            vertices.extend_from_slice(&value.to_le_bytes());
        }
        object.extend(chunk(TRIMESH, &chunk(VERTICES, &vertices)));
        editor.extend(chunk(OBJECT, &object));

        let meshes = parse_tds(&chunk(MAIN, &chunk(EDITOR, &editor))).unwrap();
        assert_eq!(meshes.len(), 1);
        assert_eq!(meshes[0].name, "mesh");
    }

    #[test]
    fn a_face_pointing_past_the_vertices_is_dropped() {
        let mut vertices = 1u16.to_le_bytes().to_vec();
        for value in [0.0f32, 0.0, 0.0] {
            vertices.extend_from_slice(&value.to_le_bytes());
        }
        let mut faces = 1u16.to_le_bytes().to_vec();
        for value in [0u16, 5, 9, 0] {
            faces.extend_from_slice(&value.to_le_bytes());
        }

        let mut trimesh = chunk(VERTICES, &vertices);
        trimesh.extend(chunk(FACES, &faces));
        let mut object = b"bad".to_vec();
        object.push(0);
        object.extend(chunk(TRIMESH, &trimesh));

        let meshes = parse_tds(&chunk(MAIN, &chunk(EDITOR, &chunk(OBJECT, &object)))).unwrap();
        assert!(meshes[0].geometry.indices.is_empty());
    }

    #[test]
    fn a_file_that_is_not_3ds_is_an_error() {
        assert!(parse_tds(b"not a 3ds file").is_err());
        assert!(parse_tds(&[]).is_err());
    }
}
