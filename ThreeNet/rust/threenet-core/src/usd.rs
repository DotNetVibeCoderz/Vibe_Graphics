//! USD import: `.usda` text, and `.usdz` archives that carry one.
//!
//! USD is a scene description language with layers, references, variants,
//! composition arcs and two serialisations. This reads the one shape of it a
//! loader can honestly support without becoming an implementation of USD: a
//! `.usda` text file, and the `def Mesh` prims in it.
//!
//! What that gets you is the output of most exporters aimed at AR, where the
//! scene has already been flattened to meshes before it is written.
//!
//! **Binary `.usdc` is not read.** It is USD's own crystal format - a memory
//! mappable structure with its own compression and a table of contents - and
//! guessing at it would produce wrong geometry rather than an error. A `.usdz`
//! holding only `.usdc` reports that plainly, because many do: Reality
//! Composer and several Apple tools write binary by default, and `usdcat` will
//! convert one to text.

use std::io::Read;
use std::path::Path;

use crate::collada::{place, ColladaMesh};
use crate::error::{Error, Result};
use crate::geometry::{Geometry, Vertex};
use crate::loaders::ImportResult;
use crate::math::{Vec2, Vec3};
use crate::scene::{NodeId, Scene};

/// Reads the numbers out of `name = [ ... ]`, returning them and where the
/// value ended.
fn array_after(text: &str, name: &str) -> Option<(Vec<f32>, usize)> {
    let at = text.find(name)?;
    let open = text[at..].find('[')? + at;
    let close = text[open..].find(']')? + open;
    let body = &text[open + 1..close];
    let values = body
        .split(|c: char| c == ',' || c == '(' || c == ')' || c.is_whitespace())
        .filter(|part| !part.is_empty())
        .filter_map(|part| part.parse::<f32>().ok())
        .collect();
    Some((values, close))
}

/// Finds the body of each `def Mesh "name" { ... }` block.
fn mesh_blocks(text: &str) -> Vec<(String, String)> {
    let mut blocks = Vec::new();
    let mut search = 0usize;

    while let Some(found) = text[search..].find("def Mesh") {
        let start = search + found;
        // The name is the quoted token after the keyword.
        let header = &text[start..];
        let name = header
            .find('"')
            .and_then(|open| {
                let rest = &header[open + 1..];
                rest.find('"').map(|close| rest[..close].to_string())
            })
            .unwrap_or_else(|| format!("mesh{}", blocks.len()));

        let Some(brace) = header.find('{') else {
            break;
        };
        // Walk to the matching brace, because a prim can hold nested ones.
        let mut depth = 0i32;
        let mut end = None;
        for (offset, character) in header[brace..].char_indices() {
            match character {
                '{' => depth += 1,
                '}' => {
                    depth -= 1;
                    if depth == 0 {
                        end = Some(brace + offset);
                        break;
                    }
                }
                _ => {}
            }
        }
        let Some(end) = end else {
            break;
        };
        blocks.push((name, header[brace + 1..end].to_string()));
        search = start + end;
    }
    blocks
}

/// Parses `.usda` text into meshes.
pub fn parse_usda(text: &str) -> Result<Vec<ColladaMesh>> {
    let mut meshes = Vec::new();

    for (name, body) in mesh_blocks(text) {
        let Some((points, _)) = array_after(&body, "points") else {
            continue;
        };
        let Some((counts, _)) = array_after(&body, "faceVertexCounts") else {
            continue;
        };
        let Some((corners, _)) = array_after(&body, "faceVertexIndices") else {
            continue;
        };

        let positions: Vec<Vec3> = points
            .chunks_exact(3)
            .map(|chunk| Vec3::new(chunk[0], chunk[1], chunk[2]))
            .collect();
        if positions.is_empty() {
            continue;
        }

        // USD gives face sizes separately, so faces of any size are fanned.
        let mut indices: Vec<u32> = Vec::new();
        let mut at = 0usize;
        let limit = positions.len() as u32;
        for count in counts {
            let count = count as usize;
            if count < 3 || at + count > corners.len() {
                at += count;
                continue;
            }
            let face: Vec<u32> = corners[at..at + count].iter().map(|v| *v as u32).collect();
            if face.iter().all(|index| *index < limit) {
                for i in 1..count - 1 {
                    indices.extend_from_slice(&[face[0], face[i], face[i + 1]]);
                }
            }
            at += count;
        }
        if indices.is_empty() {
            continue;
        }

        let uvs = array_after(&body, "primvars:st")
            .map(|(values, _)| {
                values
                    .chunks_exact(2)
                    .map(|pair| Vec2::new(pair[0], pair[1]))
                    .collect::<Vec<_>>()
            })
            .unwrap_or_default();

        let vertices: Vec<Vertex> = positions
            .iter()
            .enumerate()
            .map(|(index, position)| {
                Vertex::new(*position, Vec3::Y, uvs.get(index).copied().unwrap_or(Vec2::ZERO))
            })
            .collect();

        let mut geometry = Geometry::new(vertices, indices);
        // USD can carry normals as a primvar with its own indexing; computing
        // them is both simpler and right for the smooth surfaces it describes.
        geometry.compute_normals();
        geometry.compute_tangents();
        meshes.push(ColladaMesh { name, geometry });
    }

    if meshes.is_empty() {
        return Err(Error::Asset("the USD file holds no readable Mesh prims".into()));
    }
    Ok(meshes)
}

/// Imports a `.usda` or `.usdz` file into `scene` under `parent`.
pub fn load_usd(scene: &mut Scene, path: &str, parent: Option<NodeId>) -> Result<ImportResult> {
    let lower = path.to_ascii_lowercase();
    let text = if lower.ends_with(".usdz") {
        let bytes = std::fs::read(path)?;
        let cursor = std::io::Cursor::new(&bytes[..]);
        let mut archive = zip::ZipArchive::new(cursor)
            .map_err(|error| Error::Asset(format!("not a readable USDZ archive: {error}")))?;

        let names: Vec<String> = (0..archive.len())
            .filter_map(|index| archive.by_index(index).ok().map(|f| f.name().to_string()))
            .collect();
        let wanted = names
            .iter()
            .find(|name| name.to_ascii_lowercase().ends_with(".usda"))
            .cloned();

        let Some(wanted) = wanted else {
            let binary = names
                .iter()
                .any(|name| name.to_ascii_lowercase().ends_with(".usdc"));
            return Err(Error::Asset(if binary {
                "this USDZ holds binary .usdc, which this loader does not read; \
                 convert it with `usdcat -o out.usda in.usdc` and load that"
                    .into()
            } else {
                "this USDZ holds no USD layer".into()
            }));
        };

        let mut file = archive
            .by_name(&wanted)
            .map_err(|error| Error::Asset(format!("could not open {wanted}: {error}")))?;
        let mut out = String::new();
        file.read_to_string(&mut out)?;
        out
    } else {
        std::fs::read_to_string(path)?
    };

    let meshes = parse_usda(&text)?;
    let name = Path::new(path)
        .file_stem()
        .map(|n| n.to_string_lossy().to_string())
        .unwrap_or_default();
    place(scene, parent, &name, meshes)
}

#[cfg(test)]
mod tests {
    use super::*;

    const USDA: &str = r##"#usda 1.0
def Xform "root"
{
    def Mesh "Plane"
    {
        int[] faceVertexCounts = [4]
        int[] faceVertexIndices = [0, 1, 2, 3]
        point3f[] points = [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)]
        texCoord2f[] primvars:st = [(0, 0), (1, 0), (1, 1), (0, 1)]
    }
}
"##;

    #[test]
    fn reads_a_mesh_prim() {
        let meshes = parse_usda(USDA).unwrap();
        assert_eq!(meshes.len(), 1);
        assert_eq!(meshes[0].name, "Plane");
        assert_eq!(meshes[0].geometry.vertices.len(), 4);
        // A quad is fanned into two triangles.
        assert_eq!(meshes[0].geometry.indices.len(), 6);
    }

    #[test]
    fn uvs_are_read_when_present() {
        let meshes = parse_usda(USDA).unwrap();
        assert_eq!(meshes[0].geometry.vertices[2].uv, [1.0, 1.0]);
    }

    #[test]
    fn nested_prims_do_not_end_the_block_early() {
        // A Mesh holding a child prim has a brace in the middle of it; stopping
        // at the first closing brace would lose everything after it.
        let nested = USDA.replace(
            "        texCoord2f[] primvars:st",
            "        def Scope \"extra\" { }\n        texCoord2f[] primvars:st",
        );
        let meshes = parse_usda(&nested).unwrap();
        assert_eq!(meshes[0].geometry.vertices[2].uv, [1.0, 1.0]);
    }

    #[test]
    fn two_meshes_both_arrive() {
        let two = USDA.replace("}\n\"##", "}\n}");
        let doubled = format!(
            "{}\ndef Mesh \"Second\"\n{{\n    int[] faceVertexCounts = [3]\n    \
             int[] faceVertexIndices = [0, 1, 2]\n    \
             point3f[] points = [(0, 0, 0), (1, 0, 0), (0, 1, 0)]\n}}\n",
            two
        );
        let meshes = parse_usda(&doubled).unwrap();
        assert_eq!(meshes.len(), 2);
        assert_eq!(meshes[1].name, "Second");
    }

    #[test]
    fn a_face_pointing_past_the_points_is_dropped() {
        let broken = USDA.replace("[0, 1, 2, 3]", "[0, 1, 2, 99]");
        // The whole face goes, rather than indexing past the end later.
        assert!(parse_usda(&broken).is_err());
    }

    #[test]
    fn a_file_with_no_meshes_is_an_error() {
        assert!(parse_usda("#usda 1.0\ndef Xform \"empty\" { }\n").is_err());
    }
}
