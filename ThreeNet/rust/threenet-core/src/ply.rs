//! PLY import: ASCII and binary, little and big endian.
//!
//! PLY describes its own layout in a text header - a list of elements, each with
//! a list of typed properties - and then writes the rows in exactly that order.
//! So the reader is really a header parser plus a loop that walks whatever the
//! header said, which is what makes the format worth supporting: a scanner that
//! emits positions, normals, colours and confidence values in some order of its
//! own choosing is still readable.
//!
//! Positions are required. Normals are used when present and computed from the
//! faces when not. UVs are read from any of the three spellings in common use.
//! Vertex colours are read but have nowhere to go yet, since the vertex layout
//! has no colour channel.

use std::path::Path;

use crate::error::{Error, Result};
use crate::geometry::{Geometry, Vertex};
use crate::loaders::ImportResult;
use crate::material::Material;
use crate::math::{Vec2, Vec3};
use crate::scene::{NodeId, Scene};

/// Imports a `.ply` file into `scene` under `parent`.
pub fn load_ply(scene: &mut Scene, path: &str, parent: Option<NodeId>) -> Result<ImportResult> {
    let bytes = std::fs::read(path)?;
    let geometry = parse_ply(&bytes)?;
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

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Scalar {
    I8,
    U8,
    I16,
    U16,
    I32,
    U32,
    F32,
    F64,
}

impl Scalar {
    fn parse(name: &str) -> Option<Self> {
        Some(match name {
            "char" | "int8" => Self::I8,
            "uchar" | "uint8" => Self::U8,
            "short" | "int16" => Self::I16,
            "ushort" | "uint16" => Self::U16,
            "int" | "int32" => Self::I32,
            "uint" | "uint32" => Self::U32,
            "float" | "float32" => Self::F32,
            "double" | "float64" => Self::F64,
            _ => return None,
        })
    }

    fn size(self) -> usize {
        match self {
            Self::I8 | Self::U8 => 1,
            Self::I16 | Self::U16 => 2,
            Self::I32 | Self::U32 | Self::F32 => 4,
            Self::F64 => 8,
        }
    }
}

#[derive(Debug, Clone)]
struct Property {
    name: String,
    scalar: Scalar,
    /// `Some(count type)` when the property is a list, as faces always are.
    list: Option<Scalar>,
}

#[derive(Debug, Clone)]
struct Element {
    name: String,
    count: usize,
    properties: Vec<Property>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Encoding {
    Ascii,
    BinaryLittleEndian,
    BinaryBigEndian,
}

/// Parses PLY from memory.
pub fn parse_ply(bytes: &[u8]) -> Result<Geometry> {
    let (encoding, elements, body) = parse_header(bytes)?;
    let (vertices, indices) = match encoding {
        Encoding::Ascii => read_ascii(body, &elements)?,
        _ => read_binary(body, &elements, encoding == Encoding::BinaryBigEndian)?,
    };

    if vertices.is_empty() {
        return Err(Error::Asset("the PLY file holds no vertices".into()));
    }

    // A PLY with no faces is a point cloud. Keep the vertices and let the
    // caller draw them as points rather than refusing the file.
    let had_normals = elements
        .iter()
        .any(|e| e.name == "vertex" && e.properties.iter().any(|p| p.name == "nx"));
    let mut geometry = Geometry::new(vertices, indices);
    if !had_normals && !geometry.indices.is_empty() {
        geometry.compute_normals();
    }
    geometry.compute_tangents();
    Ok(geometry)
}

/// Splits the text header from the body and describes the rows that follow.
fn parse_header(bytes: &[u8]) -> Result<(Encoding, Vec<Element>, &[u8])> {
    // The header is ASCII and ends at "end_header"; the body may be binary, so
    // only the header is ever treated as text.
    let marker = b"end_header";
    let Some(at) = bytes
        .windows(marker.len())
        .position(|window| window == marker)
    else {
        return Err(Error::Asset("the PLY header has no end_header line".into()));
    };
    let mut body_start = at + marker.len();
    // Step over the newline, in either of its spellings.
    if bytes.get(body_start) == Some(&b'\r') {
        body_start += 1;
    }
    if bytes.get(body_start) == Some(&b'\n') {
        body_start += 1;
    }

    let header = std::str::from_utf8(&bytes[..at])
        .map_err(|_| Error::Asset("the PLY header is not valid UTF-8".into()))?;

    let mut encoding = None;
    let mut elements: Vec<Element> = Vec::new();
    for line in header.lines() {
        let mut parts = line.split_whitespace();
        match parts.next() {
            Some("ply") | Some("comment") | Some("obj_info") | None => {}
            Some("format") => {
                encoding = match parts.next() {
                    Some("ascii") => Some(Encoding::Ascii),
                    Some("binary_little_endian") => Some(Encoding::BinaryLittleEndian),
                    Some("binary_big_endian") => Some(Encoding::BinaryBigEndian),
                    other => {
                        return Err(Error::Asset(format!(
                            "unknown PLY format '{}'",
                            other.unwrap_or("")
                        )));
                    }
                };
            }
            Some("element") => {
                let name = parts.next().unwrap_or_default().to_string();
                let count = parts
                    .next()
                    .and_then(|v| v.parse::<usize>().ok())
                    .ok_or_else(|| Error::Asset(format!("malformed PLY element: '{line}'")))?;
                elements.push(Element {
                    name,
                    count,
                    properties: Vec::new(),
                });
            }
            Some("property") => {
                let Some(element) = elements.last_mut() else {
                    return Err(Error::Asset(
                        "a PLY property appeared before any element".into(),
                    ));
                };
                let kind = parts.next().unwrap_or_default();
                let property = if kind == "list" {
                    let count = parts.next().and_then(Scalar::parse);
                    let scalar = parts.next().and_then(Scalar::parse);
                    let (Some(count), Some(scalar)) = (count, scalar) else {
                        return Err(Error::Asset(format!("malformed PLY list: '{line}'")));
                    };
                    Property {
                        name: parts.next().unwrap_or_default().to_string(),
                        scalar,
                        list: Some(count),
                    }
                } else {
                    let Some(scalar) = Scalar::parse(kind) else {
                        return Err(Error::Asset(format!("unknown PLY type '{kind}'")));
                    };
                    Property {
                        name: parts.next().unwrap_or_default().to_string(),
                        scalar,
                        list: None,
                    }
                };
                element.properties.push(property);
            }
            _ => {}
        }
    }

    let Some(encoding) = encoding else {
        return Err(Error::Asset("the PLY header has no format line".into()));
    };
    Ok((encoding, elements, &bytes[body_start..]))
}

/// Picks the vertex fields out of one row of named values.
fn vertex_from(named: &[(String, f64)]) -> Vertex {
    let get = |want: &[&str]| -> Option<f64> {
        named
            .iter()
            .find(|(name, _)| want.contains(&name.as_str()))
            .map(|(_, value)| *value)
    };
    let position = Vec3::new(
        get(&["x"]).unwrap_or(0.0) as f32,
        get(&["y"]).unwrap_or(0.0) as f32,
        get(&["z"]).unwrap_or(0.0) as f32,
    );
    let normal = Vec3::new(
        get(&["nx"]).unwrap_or(0.0) as f32,
        get(&["ny"]).unwrap_or(0.0) as f32,
        get(&["nz"]).unwrap_or(0.0) as f32,
    );
    // Three spellings in the wild, and nothing says which a given exporter
    // picked.
    let uv = Vec2::new(
        get(&["s", "u", "texture_u"]).unwrap_or(0.0) as f32,
        get(&["t", "v", "texture_v"]).unwrap_or(0.0) as f32,
    );
    let normal = if normal.length_squared() > 1.0e-12 {
        normal.normalize()
    } else {
        Vec3::Y
    };
    Vertex::new(position, normal, uv)
}

/// Turns one face of any size into triangles with a fan.
fn push_face(indices: &mut Vec<u32>, corners: &[u32]) {
    for i in 1..corners.len().saturating_sub(1) {
        indices.push(corners[0]);
        indices.push(corners[i]);
        indices.push(corners[i + 1]);
    }
}

fn read_ascii(body: &[u8], elements: &[Element]) -> Result<(Vec<Vertex>, Vec<u32>)> {
    let text = std::str::from_utf8(body)
        .map_err(|_| Error::Asset("the PLY body is not valid UTF-8".into()))?;
    let mut tokens = text.split_whitespace();
    let mut vertices = Vec::new();
    let mut indices = Vec::new();

    let mut next = || -> Result<f64> {
        tokens
            .next()
            .and_then(|token| token.parse::<f64>().ok())
            .ok_or_else(|| Error::Asset("the PLY body ended early".into()))
    };

    for element in elements {
        for _ in 0..element.count {
            let mut named: Vec<(String, f64)> = Vec::new();
            let mut face: Vec<u32> = Vec::new();
            for property in &element.properties {
                if property.list.is_some() {
                    let count = next()? as usize;
                    for _ in 0..count {
                        face.push(next()? as u32);
                    }
                } else {
                    named.push((property.name.clone(), next()?));
                }
            }
            match element.name.as_str() {
                "vertex" => vertices.push(vertex_from(&named)),
                "face" => push_face(&mut indices, &face),
                // Anything else - edges, materials, a scanner's own elements -
                // has still been stepped over, which is what keeps the rows
                // after it aligned.
                _ => {}
            }
        }
    }
    Ok((vertices, indices))
}

fn read_binary(body: &[u8], elements: &[Element], big_endian: bool) -> Result<(Vec<Vertex>, Vec<u32>)> {
    let mut at = 0usize;
    let mut vertices = Vec::new();
    let mut indices = Vec::new();

    let read = |scalar: Scalar, at: &mut usize| -> Result<f64> {
        let size = scalar.size();
        if *at + size > body.len() {
            return Err(Error::Asset("the PLY body ended early".into()));
        }
        let raw = &body[*at..*at + size];
        *at += size;
        let mut buffer = [0u8; 8];
        buffer[..size].copy_from_slice(raw);
        if big_endian {
            buffer[..size].reverse();
        }
        Ok(match scalar {
            Scalar::I8 => buffer[0] as i8 as f64,
            Scalar::U8 => buffer[0] as f64,
            Scalar::I16 => i16::from_le_bytes([buffer[0], buffer[1]]) as f64,
            Scalar::U16 => u16::from_le_bytes([buffer[0], buffer[1]]) as f64,
            Scalar::I32 => i32::from_le_bytes([buffer[0], buffer[1], buffer[2], buffer[3]]) as f64,
            Scalar::U32 => u32::from_le_bytes([buffer[0], buffer[1], buffer[2], buffer[3]]) as f64,
            Scalar::F32 => f32::from_le_bytes([buffer[0], buffer[1], buffer[2], buffer[3]]) as f64,
            Scalar::F64 => f64::from_le_bytes(buffer),
        })
    };

    for element in elements {
        for _ in 0..element.count {
            let mut named: Vec<(String, f64)> = Vec::new();
            let mut face: Vec<u32> = Vec::new();
            for property in &element.properties {
                if let Some(count_type) = property.list {
                    let count = read(count_type, &mut at)? as usize;
                    for _ in 0..count {
                        face.push(read(property.scalar, &mut at)? as u32);
                    }
                } else {
                    named.push((property.name.clone(), read(property.scalar, &mut at)?));
                }
            }
            match element.name.as_str() {
                "vertex" => vertices.push(vertex_from(&named)),
                "face" => push_face(&mut indices, &face),
                _ => {}
            }
        }
    }
    Ok((vertices, indices))
}

#[cfg(test)]
mod tests {
    use super::*;

    const ASCII: &str = "ply\n\
         format ascii 1.0\n\
         comment made by a test\n\
         element vertex 3\n\
         property float x\n\
         property float y\n\
         property float z\n\
         element face 1\n\
         property list uchar int vertex_indices\n\
         end_header\n\
         0 0 0\n\
         1 0 0\n\
         0 1 0\n\
         3 0 1 2\n";

    #[test]
    fn reads_an_ascii_triangle() {
        let geometry = parse_ply(ASCII.as_bytes()).unwrap();
        assert_eq!(geometry.vertices.len(), 3);
        assert_eq!(geometry.indices, vec![0, 1, 2]);
        // No normals in the file, so they come from the face.
        assert_eq!(geometry.vertices[0].normal, [0.0, 0.0, 1.0]);
    }

    #[test]
    fn a_quad_is_fanned_into_triangles() {
        let text = ASCII
            .replace("element vertex 3", "element vertex 4")
            .replace("0 1 0\n3 0 1 2\n", "1 1 0\n0 1 0\n4 0 1 2 3\n");
        let geometry = parse_ply(text.as_bytes()).unwrap();
        assert_eq!(geometry.indices.len(), 6);
    }

    #[test]
    fn properties_are_read_in_whatever_order_the_header_gives() {
        // Normals before positions, and a confidence value in between that the
        // reader has to step over without losing its place.
        let text = "ply\n\
             format ascii 1.0\n\
             element vertex 1\n\
             property float nx\n\
             property float ny\n\
             property float nz\n\
             property float confidence\n\
             property float x\n\
             property float y\n\
             property float z\n\
             end_header\n\
             0 1 0 0.5 7 8 9\n";
        let geometry = parse_ply(text.as_bytes()).unwrap();
        assert_eq!(geometry.vertices[0].position, [7.0, 8.0, 9.0]);
        assert_eq!(geometry.vertices[0].normal, [0.0, 1.0, 0.0]);
    }

    #[test]
    fn reads_a_little_endian_binary_triangle() {
        let mut bytes = b"ply\nformat binary_little_endian 1.0\n\
             element vertex 3\n\
             property float x\nproperty float y\nproperty float z\n\
             element face 1\n\
             property list uchar int vertex_indices\n\
             end_header\n"
            .to_vec();
        for value in [0.0f32, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0] {
            bytes.extend_from_slice(&value.to_le_bytes());
        }
        bytes.push(3);
        for index in [0i32, 1, 2] {
            bytes.extend_from_slice(&index.to_le_bytes());
        }
        let geometry = parse_ply(&bytes).unwrap();
        assert_eq!(geometry.vertices.len(), 3);
        assert_eq!(geometry.vertices[1].position, [1.0, 0.0, 0.0]);
        assert_eq!(geometry.indices, vec![0, 1, 2]);
    }

    #[test]
    fn reads_a_big_endian_binary_triangle() {
        let mut bytes = b"ply\nformat binary_big_endian 1.0\n\
             element vertex 3\n\
             property float x\nproperty float y\nproperty float z\n\
             element face 1\n\
             property list uchar int vertex_indices\n\
             end_header\n"
            .to_vec();
        for value in [0.0f32, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0] {
            bytes.extend_from_slice(&value.to_be_bytes());
        }
        bytes.push(3);
        for index in [0i32, 1, 2] {
            bytes.extend_from_slice(&index.to_be_bytes());
        }
        let geometry = parse_ply(&bytes).unwrap();
        assert_eq!(geometry.vertices[1].position, [1.0, 0.0, 0.0]);
        assert_eq!(geometry.indices, vec![0, 1, 2]);
    }

    #[test]
    fn a_file_with_no_faces_is_a_point_cloud() {
        let text = "ply\n\
             format ascii 1.0\n\
             element vertex 2\n\
             property float x\nproperty float y\nproperty float z\n\
             end_header\n\
             0 0 0\n\
             1 2 3\n";
        let geometry = parse_ply(text.as_bytes()).unwrap();
        assert_eq!(geometry.vertices.len(), 2);
        assert!(geometry.indices.is_empty());
    }

    #[test]
    fn a_truncated_body_is_an_error() {
        let text = ASCII.replace("3 0 1 2\n", "");
        assert!(parse_ply(text.as_bytes()).is_err());
    }
}
