//! Draco decoding.
//!
//! Draco compresses a mesh by quantising its attributes and then coding the
//! connectivity with edgebreaker, which is why a Draco buffer is often a tenth
//! the size of the triangles it holds. This decodes one back into geometry.
//!
//! The decoder is `draco-core`, which is a pure Rust implementation rather
//! than a binding - so unlike `basis-universal` this brings no C++ toolchain
//! with it. Only the decoder half is compiled in; nothing here writes
//! compressed meshes.
//!
//! Decoding is lossy in a way the format chooses and not the reader: positions
//! come back on the quantisation grid the encoder picked, so a mesh encoded at
//! eleven bits has eleven bits of position. That is the trade Draco exists to
//! make.

use draco_core::geometry_attribute::GeometryAttributeType;
use draco_core::mesh::Mesh as DracoMesh;
use draco_core::mesh_decoder::MeshDecoder;

use crate::error::{Error, Result};
use crate::geometry::{Geometry, Vertex};
use crate::math::{Vec2, Vec3};

/// Decodes a Draco buffer into geometry.
///
/// The buffer is a whole Draco file - the one that sits in a glTF
/// `KHR_draco_mesh_compression` buffer view, or that `draco_encoder` writes.
pub fn decode(bytes: &[u8]) -> Result<Geometry> {
    if bytes.len() < 5 || &bytes[..5] != b"DRACO" {
        return Err(Error::Asset("not a Draco buffer".into()));
    }

    let mut buffer = draco_core::decoder_buffer::DecoderBuffer::new(bytes);
    let mut mesh = DracoMesh::new();
    let mut decoder = MeshDecoder::new();
    // Status is a Result, so the decoder's own error carries through rather
    // than being flattened into "it failed".
    decoder
        .decode(&mut buffer, &mut mesh)
        .map_err(|error| Error::Asset(format!("the Draco buffer did not decode: {error:?}")))?;

    let points = mesh.num_points();
    if points == 0 || mesh.num_faces() == 0 {
        return Err(Error::Asset("the Draco buffer holds no triangles".into()));
    }

    // Attributes are looked up by what they mean rather than by order, because
    // an encoder is free to write them in any.
    let read = |kind: GeometryAttributeType, components: usize| -> Option<Vec<f32>> {
        let id = mesh.named_attribute_id(kind);
        if id < 0 {
            return None;
        }
        Some(mesh.attribute(id).read_f32s(points, components))
    };

    let positions = read(GeometryAttributeType::Position, 3)
        .ok_or_else(|| Error::Asset("the Draco buffer has no positions".into()))?;
    let normals = read(GeometryAttributeType::Normal, 3);
    let uvs = read(GeometryAttributeType::TexCoord, 2);

    let vertices: Vec<Vertex> = (0..points)
        .map(|index| {
            let position = Vec3::new(
                positions.get(index * 3).copied().unwrap_or(0.0),
                positions.get((index * 3) + 1).copied().unwrap_or(0.0),
                positions.get((index * 3) + 2).copied().unwrap_or(0.0),
            );
            let normal = normals
                .as_ref()
                .map(|values| {
                    Vec3::new(
                        values.get(index * 3).copied().unwrap_or(0.0),
                        values.get((index * 3) + 1).copied().unwrap_or(0.0),
                        values.get((index * 3) + 2).copied().unwrap_or(0.0),
                    )
                })
                .filter(|normal| normal.length_squared() > 1.0e-12)
                .map(|normal| normal.normalize())
                .unwrap_or(Vec3::Y);
            let uv = uvs
                .as_ref()
                .map(|values| {
                    Vec2::new(
                        values.get(index * 2).copied().unwrap_or(0.0),
                        values.get((index * 2) + 1).copied().unwrap_or(0.0),
                    )
                })
                .unwrap_or(Vec2::ZERO);
            Vertex::new(position, normal, uv)
        })
        .collect();

    let limit = points as u32;
    let mut indices = Vec::with_capacity(mesh.num_faces() * 3);
    for face in 0..mesh.num_faces() {
        let corners = mesh.face(draco_core::geometry_indices::FaceIndex(face as u32));
        let triangle = [corners[0].0, corners[1].0, corners[2].0];
        // A face past the point count would be a decoder bug rather than a
        // broken file, but it is still not something to index memory with.
        if triangle.iter().all(|index| *index < limit) {
            indices.extend_from_slice(&triangle);
        }
    }
    if indices.is_empty() {
        return Err(Error::Asset("the Draco mesh decoded to no usable faces".into()));
    }

    let had_normals = normals.is_some();
    let mut geometry = Geometry::new(vertices, indices);
    if !had_normals {
        geometry.compute_normals();
    }
    geometry.compute_tangents();
    Ok(geometry)
}

#[cfg(test)]
mod tests {
    use super::*;

    use draco_core::draco_types::DataType;
    use draco_core::geometry_attribute::PointAttribute;
    use draco_core::geometry_indices::PointIndex;

    /// Encodes a triangle so there is something real to decode.
    ///
    /// Without this the only tests would be the ones that reject bad input,
    /// which prove the guard works and say nothing about whether decoding
    /// does.
    fn encode_triangle(positions: &[[f32; 3]; 3]) -> Vec<u8> {
        let mut mesh = DracoMesh::new();
        mesh.set_num_points(3);
        mesh.set_num_faces(1);
        mesh.set_face(
            draco_core::geometry_indices::FaceIndex(0),
            [PointIndex(0), PointIndex(1), PointIndex(2)],
        );

        let mut attribute = PointAttribute::new();
        attribute.init(GeometryAttributeType::Position, 3, DataType::Float32, false, 3);
        for (point, position) in positions.iter().enumerate() {
            for (component, value) in position.iter().enumerate() {
                attribute
                    .buffer_mut()
                    .update(&value.to_le_bytes(), Some(((point * 3) + component) * 4));
            }
        }
        mesh.add_attribute(attribute);

        let mut encoder = draco_core::mesh_encoder::MeshEncoder::new();
        encoder.set_mesh(mesh);
        let mut out = draco_core::encoder_buffer::EncoderBuffer::new();
        encoder
            .encode(&draco_core::encoder_options::EncoderOptions::new(), &mut out)
            .expect("encode");
        out.data().to_vec()
    }

    #[test]
    fn a_real_buffer_decodes_back_to_its_triangle() {
        let positions = [[0.0f32, 0.0, 0.0], [1.0, 0.0, 0.0], [0.0, 1.0, 0.0]];
        let geometry = decode(&encode_triangle(&positions)).expect("decode");

        assert_eq!(geometry.vertices.len(), 3);
        assert_eq!(geometry.indices.len(), 3);

        // Draco quantises positions, so the corners come back on its grid
        // rather than exactly. A whole unit of tolerance would pass even if the
        // mesh were wrong, so this is tight enough to mean something.
        let mut corners: Vec<[f32; 3]> = geometry.vertices.iter().map(|v| v.position).collect();
        corners.sort_by(|a, b| a.partial_cmp(b).unwrap());
        let mut expected = positions;
        expected.sort_by(|a, b| a.partial_cmp(b).unwrap());
        for (got, want) in corners.iter().zip(expected.iter()) {
            for axis in 0..3 {
                assert!(
                    (got[axis] - want[axis]).abs() < 0.01,
                    "corner moved: {got:?} against {want:?}"
                );
            }
        }
    }

    #[test]
    fn normals_are_computed_when_the_buffer_carries_none() {
        let geometry = decode(&encode_triangle(&[
            [0.0, 0.0, 0.0],
            [1.0, 0.0, 0.0],
            [0.0, 1.0, 0.0],
        ]))
        .expect("decode");
        // The triangle lies in the XY plane, so its normal runs along z.
        let normal = geometry.vertices[0].normal;
        assert!(normal[2].abs() > 0.99, "expected a z normal, got {normal:?}");
    }

    #[test]
    fn something_that_is_not_draco_is_an_error() {
        // The magic is checked before anything else, so a wrong file says so
        // rather than failing somewhere inside the bitstream.
        assert!(decode(b"not draco at all").is_err());
        assert!(decode(&[]).is_err());
        assert!(decode(b"DRAC").is_err());
    }

    #[test]
    fn a_truncated_buffer_reports_rather_than_panicking() {
        // The header is right and the rest is missing, which is what a partial
        // download looks like.
        let mut bytes = b"DRACO".to_vec();
        bytes.extend_from_slice(&[0u8; 8]);
        assert!(decode(&bytes).is_err());
    }
}
