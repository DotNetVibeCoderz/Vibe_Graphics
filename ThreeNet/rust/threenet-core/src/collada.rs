//! Collada (`.dae`) import.
//!
//! Collada stores geometry as a bag of named float arrays plus a description of
//! how to read them. A `<source>` holds the raw numbers; an `<accessor>` says
//! how wide a stride they come in; `<vertices>` names the position source; and
//! `<triangles>` or `<polylist>` gives one index per input per corner, so a
//! triangle with positions, normals and UVs spends nine indices.
//!
//! That indirection is the whole job. The reader resolves it into the flat
//! interleaved vertices the renderer wants, and welds corners that share all
//! their indices so a cube is eight vertices and not thirty six.
//!
//! Materials and the node hierarchy are not read. A Collada file that carries
//! its own scene graph flattens to one node per geometry, which is enough to
//! look at the mesh and not enough to drive a rig.

use std::collections::HashMap;
use std::path::Path;

use quick_xml::events::Event;
use quick_xml::Reader;

use crate::error::{Error, Result};
use crate::geometry::{Geometry, Vertex};
use crate::loaders::ImportResult;
use crate::material::Material;
use crate::math::{Vec2, Vec3};
use crate::scene::{NodeId, Scene};

/// One mesh pulled out of the document.
#[derive(Debug)]
pub struct ColladaMesh {
    pub name: String,
    pub geometry: Geometry,
}

/// A `<source>`: the numbers and how wide a step through them is.
#[derive(Default, Clone)]
struct Source {
    values: Vec<f32>,
    stride: usize,
}

/// One `<input>` on a primitive: which source, which slot, which semantic.
#[derive(Clone)]
struct Input {
    semantic: String,
    source: String,
    offset: usize,
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

/// Collada references everything by `#id`; the hash is not part of the id.
fn strip_hash(reference: &str) -> String {
    reference.trim_start_matches('#').to_string()
}

fn parse_floats(text: &str) -> Vec<f32> {
    text.split_whitespace().filter_map(|v| v.parse().ok()).collect()
}

fn parse_usizes(text: &str) -> Vec<usize> {
    text.split_whitespace().filter_map(|v| v.parse().ok()).collect()
}

/// Parses a Collada document from memory.
pub fn parse_collada(text: &str) -> Result<Vec<ColladaMesh>> {
    let mut reader = Reader::from_str(text);
    reader.config_mut().trim_text(true);

    let mut meshes: Vec<ColladaMesh> = Vec::new();
    let mut sources: HashMap<String, Source> = HashMap::new();
    // `<vertices id=.. ><input source=.. />` is a level of indirection that
    // exists only to be followed.
    let mut vertex_alias: HashMap<String, String> = HashMap::new();
    let mut inputs: Vec<Input> = Vec::new();
    let mut indices: Vec<usize> = Vec::new();
    let mut vcounts: Vec<usize> = Vec::new();

    let mut source_id = String::new();
    let mut vertices_id = String::new();
    let mut geometry_name = String::new();
    let mut in_float_array = false;
    let mut in_p = false;
    let mut in_vcount = false;
    let mut pending_floats: Vec<f32> = Vec::new();

    loop {
        match reader.read_event() {
            Err(error) => return Err(Error::Asset(format!("malformed Collada: {error}"))),
            Ok(Event::Eof) => break,
            // Start and Empty together: <input/> and <accessor/> are
            // self closing, so quick-xml reports them as Empty and a reader
            // watching only for Start never sees the half of Collada that
            // describes how to read the other half.
            Ok(Event::Start(event)) | Ok(Event::Empty(event)) => match event.name().as_ref() {
                b"geometry" => {
                    geometry_name = attribute(&event, "name")
                        .or_else(|| attribute(&event, "id"))
                        .unwrap_or_else(|| format!("mesh{}", meshes.len()));
                    sources.clear();
                    vertex_alias.clear();
                }
                b"source" => {
                    source_id = attribute(&event, "id").unwrap_or_default();
                }
                b"float_array" => {
                    in_float_array = true;
                    pending_floats.clear();
                }
                b"accessor" => {
                    let stride = attribute(&event, "stride")
                        .and_then(|v| v.parse().ok())
                        .unwrap_or(1);
                    // The accessor closes the source it belongs to.
                    if let Some(source) = sources.get_mut(&source_id) {
                        source.stride = stride;
                    }
                }
                b"vertices" => {
                    vertices_id = attribute(&event, "id").unwrap_or_default();
                }
                b"input" => {
                    let semantic = attribute(&event, "semantic").unwrap_or_default();
                    let source = attribute(&event, "source").map(|v| strip_hash(&v)).unwrap_or_default();
                    if !vertices_id.is_empty() {
                        // Inside <vertices>: this is the alias, not a primitive input.
                        vertex_alias.insert(vertices_id.clone(), source);
                    } else {
                        let offset = attribute(&event, "offset")
                            .and_then(|v| v.parse().ok())
                            .unwrap_or(0);
                        inputs.push(Input {
                            semantic,
                            source,
                            offset,
                        });
                    }
                }
                b"triangles" | b"polylist" => {
                    inputs.clear();
                    indices.clear();
                    vcounts.clear();
                }
                b"p" => {
                    in_p = true;
                }
                b"vcount" => {
                    in_vcount = true;
                }
                _ => {}
            },
            Ok(Event::Text(text)) => {
                let body = text.unescape().unwrap_or_default();
                if in_float_array {
                    pending_floats = parse_floats(&body);
                } else if in_p {
                    indices.extend(parse_usizes(&body));
                } else if in_vcount {
                    vcounts.extend(parse_usizes(&body));
                }
            }
            Ok(Event::End(event)) => match event.name().as_ref() {
                b"float_array" => {
                    in_float_array = false;
                    sources.insert(
                        source_id.clone(),
                        Source {
                            values: std::mem::take(&mut pending_floats),
                            stride: 1,
                        },
                    );
                }
                b"vertices" => vertices_id.clear(),
                b"p" => in_p = false,
                b"vcount" => in_vcount = false,
                b"triangles" | b"polylist" => {
                    if let Some(geometry) =
                        build(&sources, &vertex_alias, &inputs, &indices, &vcounts)
                    {
                        meshes.push(ColladaMesh {
                            name: geometry_name.clone(),
                            geometry,
                        });
                    }
                }
                _ => {}
            },
            _ => {}
        }
    }

    if meshes.is_empty() {
        return Err(Error::Asset("the Collada file holds no triangles".into()));
    }
    Ok(meshes)
}

/// Resolves the indices into interleaved vertices.
fn build(
    sources: &HashMap<String, Source>,
    vertex_alias: &HashMap<String, String>,
    inputs: &[Input],
    indices: &[usize],
    vcounts: &[usize],
) -> Option<Geometry> {
    if inputs.is_empty() || indices.is_empty() {
        return None;
    }
    // One index per input per corner, so the stride is the highest offset + 1
    // rather than the number of inputs - two inputs can share an offset.
    let stride = inputs.iter().map(|input| input.offset).max()? + 1;
    if stride == 0 || indices.len() % stride != 0 {
        return None;
    }
    let corner_count = indices.len() / stride;

    let resolve = |input: &Input| -> Option<&Source> {
        // A POSITION input points at <vertices>, which points at the real source.
        let id = vertex_alias.get(&input.source).unwrap_or(&input.source);
        sources.get(id)
    };

    let position = inputs.iter().find(|input| input.semantic == "VERTEX")
        .or_else(|| inputs.iter().find(|input| input.semantic == "POSITION"))?;
    let positions = resolve(position)?;
    let normal = inputs.iter().find(|input| input.semantic == "NORMAL");
    let uv = inputs.iter().find(|input| input.semantic == "TEXCOORD");

    let read = |source: &Source, index: usize, count: usize| -> Vec<f32> {
        let stride = source.stride.max(count);
        let start = index * stride;
        (0..count)
            .map(|offset| source.values.get(start + offset).copied().unwrap_or(0.0))
            .collect()
    };

    // Corners that agree on every index are the same vertex. Without this a
    // cube arrives as thirty six vertices instead of eight.
    let mut seen: HashMap<Vec<usize>, u32> = HashMap::new();
    let mut vertices: Vec<Vertex> = Vec::new();
    let mut corners: Vec<u32> = Vec::with_capacity(corner_count);

    for corner in 0..corner_count {
        let slice = &indices[corner * stride..(corner + 1) * stride];
        let key = slice.to_vec();
        let next = match seen.get(&key) {
            Some(existing) => *existing,
            None => {
                let p = read(positions, slice[position.offset], 3);
                let n = normal
                    .and_then(|input| resolve(input).map(|s| read(s, slice[input.offset], 3)))
                    .unwrap_or_else(|| vec![0.0, 0.0, 0.0]);
                let t = uv
                    .and_then(|input| resolve(input).map(|s| read(s, slice[input.offset], 2)))
                    .unwrap_or_else(|| vec![0.0, 0.0]);

                let normal_vector = Vec3::new(n[0], n[1], n[2]);
                vertices.push(Vertex::new(
                    Vec3::new(p[0], p[1], p[2]),
                    if normal_vector.length_squared() > 1.0e-12 {
                        normal_vector.normalize()
                    } else {
                        Vec3::Y
                    },
                    // Collada measures v from the bottom of the image.
                    Vec2::new(t[0], 1.0 - t[1]),
                ));
                let id = vertices.len() as u32 - 1;
                seen.insert(key, id);
                id
            }
        };
        corners.push(next);
    }

    // <triangles> is already triangles; <polylist> gives face sizes and the
    // faces have to be fanned.
    let mut triangles: Vec<u32> = Vec::new();
    if vcounts.is_empty() {
        triangles = corners;
    } else {
        let mut at = 0usize;
        for count in vcounts {
            if at + count > corners.len() {
                break;
            }
            for i in 1..count.saturating_sub(1) {
                triangles.push(corners[at]);
                triangles.push(corners[at + i]);
                triangles.push(corners[at + i + 1]);
            }
            at += count;
        }
    }

    if triangles.is_empty() {
        return None;
    }
    let had_normals = normal.is_some();
    let mut geometry = Geometry::new(vertices, triangles);
    if !had_normals {
        geometry.compute_normals();
    }
    geometry.compute_tangents();
    Some(geometry)
}

/// Imports a `.dae` file into `scene` under `parent`.
pub fn load_collada(scene: &mut Scene, path: &str, parent: Option<NodeId>) -> Result<ImportResult> {
    let text = std::fs::read_to_string(path)?;
    let meshes = parse_collada(&text)?;
    let name = Path::new(path)
        .file_stem()
        .map(|n| n.to_string_lossy().to_string())
        .unwrap_or_default();
    place(scene, parent, &name, meshes)
}

/// Builds the nodes for a set of parsed meshes.
pub(crate) fn place(
    scene: &mut Scene,
    parent: Option<NodeId>,
    name: &str,
    meshes: Vec<ColladaMesh>,
) -> Result<ImportResult> {
    let material = scene.add_material(Material::default());
    let root = scene.create_node(parent)?;
    if let Some(node) = scene.node_mut(root) {
        node.name = name.to_string();
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

    const TRIANGLE: &str = r##"<?xml version="1.0"?>
<COLLADA>
  <library_geometries>
    <geometry id="g1" name="Triangle">
      <mesh>
        <source id="pos">
          <float_array id="pos-array" count="9">0 0 0  1 0 0  0 1 0</float_array>
          <technique_common><accessor source="#pos-array" count="3" stride="3"/></technique_common>
        </source>
        <vertices id="verts"><input semantic="POSITION" source="#pos"/></vertices>
        <triangles count="1">
          <input semantic="VERTEX" source="#verts" offset="0"/>
          <p>0 1 2</p>
        </triangles>
      </mesh>
    </geometry>
  </library_geometries>
</COLLADA>"##;

    #[test]
    fn reads_a_triangle() {
        let meshes = parse_collada(TRIANGLE).unwrap();
        assert_eq!(meshes.len(), 1);
        assert_eq!(meshes[0].name, "Triangle");
        assert_eq!(meshes[0].geometry.vertices.len(), 3);
        assert_eq!(meshes[0].geometry.indices, vec![0, 1, 2]);
    }

    #[test]
    fn the_vertices_indirection_is_followed() {
        // POSITION points at <vertices>, which points at the real source. A
        // reader that stopped at the first hop would find no numbers at all.
        let meshes = parse_collada(TRIANGLE).unwrap();
        assert_eq!(meshes[0].geometry.vertices[1].position, [1.0, 0.0, 0.0]);
    }

    #[test]
    fn corners_sharing_every_index_become_one_vertex() {
        // Two triangles over four corners: the shared pair must be welded, or a
        // cube arrives with thirty six vertices instead of eight.
        let quad = TRIANGLE
            .replace(
                r##"<float_array id="pos-array" count="9">0 0 0  1 0 0  0 1 0</float_array>"##,
                r##"<float_array id="pos-array" count="12">0 0 0  1 0 0  0 1 0  1 1 0</float_array>"##,
            )
            .replace("<p>0 1 2</p>", "<p>0 1 2 1 3 2</p>");
        let meshes = parse_collada(&quad).unwrap();
        assert_eq!(meshes[0].geometry.vertices.len(), 4);
        assert_eq!(meshes[0].geometry.indices.len(), 6);
    }

    #[test]
    fn separate_normal_indices_are_resolved() {
        let with_normals = TRIANGLE
            .replace(
                "<vertices id=\"verts\">",
                r##"<source id="nrm">
          <float_array id="nrm-array" count="3">0 0 1</float_array>
          <technique_common><accessor source="#nrm-array" count="1" stride="3"/></technique_common>
        </source>
        <vertices id="verts">"##,
            )
            .replace(
                r##"<input semantic="VERTEX" source="#verts" offset="0"/>"##,
                r##"<input semantic="VERTEX" source="#verts" offset="0"/>
          <input semantic="NORMAL" source="#nrm" offset="1"/>"##,
            )
            .replace("<p>0 1 2</p>", "<p>0 0 1 0 2 0</p>");
        let meshes = parse_collada(&with_normals).unwrap();
        assert_eq!(meshes[0].geometry.vertices.len(), 3);
        assert_eq!(meshes[0].geometry.vertices[0].normal, [0.0, 0.0, 1.0]);
    }

    #[test]
    fn a_polylist_quad_is_fanned() {
        let polylist = TRIANGLE
            .replace(
                r##"<float_array id="pos-array" count="9">0 0 0  1 0 0  0 1 0</float_array>"##,
                r##"<float_array id="pos-array" count="12">0 0 0  1 0 0  1 1 0  0 1 0</float_array>"##,
            )
            .replace("<triangles count=\"1\">", "<polylist count=\"1\">")
            .replace("</triangles>", "</polylist>")
            .replace("<p>0 1 2</p>", "<vcount>4</vcount><p>0 1 2 3</p>");
        let meshes = parse_collada(&polylist).unwrap();
        assert_eq!(meshes[0].geometry.indices.len(), 6);
    }

    #[test]
    fn a_file_with_no_triangles_is_an_error() {
        assert!(parse_collada("<COLLADA></COLLADA>").is_err());
        assert!(parse_collada("not xml at all").is_err());
    }
}
