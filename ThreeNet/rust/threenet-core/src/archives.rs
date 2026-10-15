//! The two formats that are a zip archive with something familiar inside.
//!
//! - **KMZ** is Google Earth's wrapper: a zip holding a Collada document and
//!   its textures. Unwrapping it is the whole job, because the model inside is
//!   read by the Collada loader.
//! - **3MF** is the 3D printing format: a zip holding its own XML, which
//!   describes vertices and triangles directly and with no indirection at all -
//!   the opposite of Collada in that respect.

use std::io::Read;
use std::path::Path;

use quick_xml::events::Event;
use quick_xml::Reader;

use crate::collada::{place, ColladaMesh};
use crate::error::{Error, Result};
use crate::geometry::{Geometry, Vertex};
use crate::loaders::ImportResult;
use crate::math::{Vec2, Vec3};
use crate::scene::{NodeId, Scene};

/// Reads one named entry out of a zip archive.
fn read_entry(bytes: &[u8], wanted: impl Fn(&str) -> bool) -> Result<(String, Vec<u8>)> {
    let cursor = std::io::Cursor::new(bytes);
    let mut archive = zip::ZipArchive::new(cursor)
        .map_err(|error| Error::Asset(format!("not a readable archive: {error}")))?;

    // Collected first because the borrow of the archive has to end before the
    // file can be opened by index.
    let names: Vec<String> = (0..archive.len())
        .filter_map(|index| archive.by_index(index).ok().map(|file| file.name().to_string()))
        .collect();

    let Some(name) = names.into_iter().find(|name| wanted(name)) else {
        return Err(Error::Asset("the archive holds nothing this loader can read".into()));
    };
    let mut file = archive
        .by_name(&name)
        .map_err(|error| Error::Asset(format!("could not open {name}: {error}")))?;
    let mut out = Vec::new();
    file.read_to_end(&mut out)?;
    Ok((name, out))
}

/// Imports a `.kmz` file: a zip with a Collada document inside.
pub fn load_kmz(scene: &mut Scene, path: &str, parent: Option<NodeId>) -> Result<ImportResult> {
    let bytes = std::fs::read(path)?;
    let (_, document) = read_entry(&bytes, |name| {
        let lower = name.to_ascii_lowercase();
        lower.ends_with(".dae")
    })?;
    let text = String::from_utf8_lossy(&document).into_owned();
    let meshes = crate::collada::parse_collada(&text)?;
    let name = Path::new(path)
        .file_stem()
        .map(|n| n.to_string_lossy().to_string())
        .unwrap_or_default();
    place(scene, parent, &name, meshes)
}

/// Imports a `.3mf` file: a zip with the 3D Manufacturing Format XML inside.
pub fn load_3mf(scene: &mut Scene, path: &str, parent: Option<NodeId>) -> Result<ImportResult> {
    let bytes = std::fs::read(path)?;
    let (_, document) = read_entry(&bytes, |name| {
        let lower = name.to_ascii_lowercase();
        lower.ends_with(".model")
    })?;
    let text = String::from_utf8_lossy(&document).into_owned();
    let meshes = parse_3mf(&text)?;
    let name = Path::new(path)
        .file_stem()
        .map(|n| n.to_string_lossy().to_string())
        .unwrap_or_default();
    place(scene, parent, &name, meshes)
}

fn attribute(event: &quick_xml::events::BytesStart<'_>, name: &str) -> Option<String> {
    event.attributes().flatten().find_map(|attribute| {
        if attribute.key.as_ref() == name.as_bytes() {
            Some(String::from_utf8_lossy(&attribute.value).into_owned())
        } else {
            None
        }
    })
}

/// Parses the `.model` XML out of a 3MF archive.
///
/// 3MF says what it means: a list of `<vertex x= y= z=>` and a list of
/// `<triangle v1= v2= v3=>`. There is no source, accessor or input indirection
/// to resolve, which makes this the shortest of the XML readers here.
pub fn parse_3mf(text: &str) -> Result<Vec<ColladaMesh>> {
    let mut reader = Reader::from_str(text);
    reader.config_mut().trim_text(true);

    let mut meshes: Vec<ColladaMesh> = Vec::new();
    let mut positions: Vec<Vec3> = Vec::new();
    let mut indices: Vec<u32> = Vec::new();
    let mut object_name = String::new();

    loop {
        match reader.read_event() {
            Err(error) => return Err(Error::Asset(format!("malformed 3MF: {error}"))),
            Ok(Event::Eof) => break,
            Ok(Event::Start(event)) | Ok(Event::Empty(event)) => match event.name().as_ref() {
                b"object" => {
                    object_name = attribute(&event, "name")
                        .or_else(|| attribute(&event, "id"))
                        .unwrap_or_else(|| format!("object{}", meshes.len()));
                    positions.clear();
                    indices.clear();
                }
                b"vertex" => {
                    let read = |name: &str| {
                        attribute(&event, name)
                            .and_then(|v| v.parse::<f32>().ok())
                            .unwrap_or(0.0)
                    };
                    positions.push(Vec3::new(read("x"), read("y"), read("z")));
                }
                b"triangle" => {
                    let read = |name: &str| {
                        attribute(&event, name)
                            .and_then(|v| v.parse::<u32>().ok())
                            .unwrap_or(0)
                    };
                    indices.extend_from_slice(&[read("v1"), read("v2"), read("v3")]);
                }
                _ => {}
            },
            Ok(Event::End(event)) => {
                if event.name().as_ref() == b"object" && !positions.is_empty() {
                    let vertices: Vec<Vertex> = positions
                        .iter()
                        .map(|position| Vertex::new(*position, Vec3::Y, Vec2::ZERO))
                        .collect();
                    // A print file from a damaged slicer should not become an
                    // out of bounds index later.
                    let count = vertices.len() as u32;
                    let safe: Vec<u32> = indices
                        .chunks_exact(3)
                        .filter(|face| face.iter().all(|index| *index < count))
                        .flatten()
                        .copied()
                        .collect();

                    let mut geometry = Geometry::new(vertices, safe);
                    // 3MF carries no normals at all; a printed object is a
                    // solid, so they come from the faces.
                    geometry.compute_normals();
                    geometry.compute_tangents();
                    meshes.push(ColladaMesh {
                        name: std::mem::take(&mut object_name),
                        geometry,
                    });
                    positions.clear();
                    indices.clear();
                }
            }
            _ => {}
        }
    }

    if meshes.is_empty() {
        return Err(Error::Asset("the 3MF file holds no objects".into()));
    }
    Ok(meshes)
}

#[cfg(test)]
mod tests {
    use super::*;

    const MODEL: &str = r##"<?xml version="1.0"?>
<model unit="millimeter">
  <resources>
    <object id="1" name="Cube" type="model">
      <mesh>
        <vertices>
          <vertex x="0" y="0" z="0"/>
          <vertex x="1" y="0" z="0"/>
          <vertex x="0" y="1" z="0"/>
        </vertices>
        <triangles>
          <triangle v1="0" v2="1" v3="2"/>
        </triangles>
      </mesh>
    </object>
  </resources>
</model>"##;

    #[test]
    fn reads_a_3mf_triangle() {
        let meshes = parse_3mf(MODEL).unwrap();
        assert_eq!(meshes.len(), 1);
        assert_eq!(meshes[0].name, "Cube");
        assert_eq!(meshes[0].geometry.vertices.len(), 3);
        assert_eq!(meshes[0].geometry.indices, vec![0, 1, 2]);
    }

    #[test]
    fn normals_are_computed_because_3mf_has_none() {
        let meshes = parse_3mf(MODEL).unwrap();
        let normal = meshes[0].geometry.vertices[0].normal;
        // The triangle lies flat in z, so its normal runs along z.
        assert!(normal[2].abs() > 0.99, "expected a z normal, got {normal:?}");
    }

    #[test]
    fn a_triangle_pointing_past_the_vertices_is_dropped() {
        let broken = MODEL.replace(r#"v3="2""#, r#"v3="9""#);
        let meshes = parse_3mf(&broken).unwrap();
        assert!(meshes[0].geometry.indices.is_empty());
    }

    #[test]
    fn a_model_with_no_objects_is_an_error() {
        assert!(parse_3mf("<model></model>").is_err());
    }

    #[test]
    fn an_archive_without_what_we_want_reports() {
        // An empty zip is a valid archive and still has nothing to load.
        let mut buffer = Vec::new();
        {
            let mut writer = zip::ZipWriter::new(std::io::Cursor::new(&mut buffer));
            writer.finish().unwrap();
        }
        assert!(read_entry(&buffer, |name| name.ends_with(".dae")).is_err());
        assert!(read_entry(b"not a zip at all", |_| true).is_err());
    }
}
