//! MagicaVoxel `.vox` import.
//!
//! The file is a tree of chunks: a `SIZE` and an `XYZI` per model, and one
//! `RGBA` palette for all of them. Each voxel carries a palette index rather
//! than a colour.
//!
//! Two decisions shape what comes out:
//!
//! - **Hidden faces are not emitted.** A voxel face with another voxel against
//!   it can never be seen, and a solid 32 cubed model is 32768 voxels of which
//!   only the shell is visible. Dropping the interior is the difference between
//!   a few thousand triangles and nearly two hundred thousand.
//! - **The palette becomes a texture, not materials.** Every voxel points at a
//!   256 by 1 image through its UV, so a model of any number of colours is one
//!   geometry with one material. The alternative - a mesh per colour - would
//!   turn a colourful model into dozens of draw calls.

use std::path::Path;

use crate::error::{Error, Result};
use crate::geometry::{Geometry, Vertex};
use crate::loaders::ImportResult;
use crate::material::{Material, MaterialTextures};
use crate::math::{Vec2, Vec3};
use crate::scene::{NodeId, Scene};
use crate::texture::{Texture, TextureFormat};

/// One parsed model: its extent and the voxels in it.
pub struct VoxModel {
    pub size: [u32; 3],
    /// `x`, `y`, `z`, palette index.
    pub voxels: Vec<[u8; 4]>,
}

/// Everything a `.vox` file holds.
pub struct VoxFile {
    pub models: Vec<VoxModel>,
    /// 256 colours, RGBA. Index 0 is never used by a voxel.
    pub palette: Vec<[u8; 4]>,
}

/// The palette MagicaVoxel uses when a file carries none of its own.
fn default_palette() -> Vec<[u8; 4]> {
    // Generated rather than tabulated: the default is a regular sweep through
    // the colour cube, and six hundred lines of constants would say less.
    let mut palette = vec![[0u8, 0, 0, 0]; 256];
    let levels = [255u8, 204, 153, 102, 51, 0];
    let mut at = 1usize;
    for red in levels {
        for green in levels {
            for blue in levels {
                if at < 256 {
                    palette[at] = [red, green, blue, 255];
                    at += 1;
                }
            }
        }
    }
    palette
}

fn read_u32(bytes: &[u8], at: usize) -> Result<u32> {
    if at + 4 > bytes.len() {
        return Err(Error::Asset("the VOX file ended inside a value".into()));
    }
    Ok(u32::from_le_bytes([
        bytes[at],
        bytes[at + 1],
        bytes[at + 2],
        bytes[at + 3],
    ]))
}

/// Parses a `.vox` file from memory.
pub fn parse_vox(bytes: &[u8]) -> Result<VoxFile> {
    if bytes.len() < 8 || &bytes[..4] != b"VOX " {
        return Err(Error::Asset("not a VOX file".into()));
    }

    let mut models: Vec<VoxModel> = Vec::new();
    let mut palette: Option<Vec<[u8; 4]>> = None;
    let mut pending_size: Option<[u32; 3]> = None;

    // Chunks start after the magic and the version. MAIN wraps the rest, and
    // because every chunk declares the size of its children this walks the
    // whole tree without needing to know which chunks nest.
    let mut at = 8usize;
    while at + 12 <= bytes.len() {
        let id = &bytes[at..at + 4];
        let content = read_u32(bytes, at + 4)? as usize;
        let children = read_u32(bytes, at + 8)? as usize;
        let body = at + 12;
        if body + content > bytes.len() {
            return Err(Error::Asset("a VOX chunk runs past the end of the file".into()));
        }
        let data = &bytes[body..body + content];

        match id {
            b"SIZE" => {
                pending_size = Some([read_u32(data, 0)?, read_u32(data, 4)?, read_u32(data, 8)?]);
            }
            b"XYZI" => {
                let count = read_u32(data, 0)? as usize;
                if 4 + count * 4 > data.len() {
                    return Err(Error::Asset("a VOX model claims more voxels than it holds".into()));
                }
                let voxels = (0..count)
                    .map(|index| {
                        let at = 4 + index * 4;
                        [data[at], data[at + 1], data[at + 2], data[at + 3]]
                    })
                    .collect();
                models.push(VoxModel {
                    size: pending_size.take().unwrap_or([0, 0, 0]),
                    voxels,
                });
            }
            b"RGBA" => {
                if data.len() < 256 * 4 {
                    return Err(Error::Asset("the VOX palette is short".into()));
                }
                // The chunk holds colours 1..=255 in slots 0..254, so it is
                // shifted by one against the indices voxels use.
                let mut table = vec![[0u8, 0, 0, 0]; 256];
                for index in 0..255usize {
                    let at = index * 4;
                    table[index + 1] = [data[at], data[at + 1], data[at + 2], data[at + 3]];
                }
                palette = Some(table);
            }
            // MAIN has no content of its own, and anything else - the node
            // graph, materials, layers - is stepped over. Walking by the
            // declared sizes is what makes that safe.
            _ => {}
        }

        at = body + content;
        if id == b"MAIN" {
            // MAIN's children are the rest of the file, so step into them
            // rather than over them.
            let _ = children;
        } else {
            at += children;
        }
    }

    if models.is_empty() {
        return Err(Error::Asset("the VOX file holds no models".into()));
    }
    Ok(VoxFile {
        models,
        palette: palette.unwrap_or_else(default_palette),
    })
}

/// The six faces of a voxel: the neighbour offset, the outward normal, and the
/// four corners in winding order.
const FACES: [([i32; 3], [f32; 3], [[f32; 3]; 4]); 6] = [
    // +X
    ([1, 0, 0], [1.0, 0.0, 0.0],
     [[1.0, 0.0, 1.0], [1.0, 0.0, 0.0], [1.0, 1.0, 0.0], [1.0, 1.0, 1.0]]),
    // -X
    ([-1, 0, 0], [-1.0, 0.0, 0.0],
     [[0.0, 0.0, 0.0], [0.0, 0.0, 1.0], [0.0, 1.0, 1.0], [0.0, 1.0, 0.0]]),
    // +Y
    ([0, 1, 0], [0.0, 1.0, 0.0],
     [[0.0, 1.0, 1.0], [1.0, 1.0, 1.0], [1.0, 1.0, 0.0], [0.0, 1.0, 0.0]]),
    // -Y
    ([0, -1, 0], [0.0, -1.0, 0.0],
     [[0.0, 0.0, 0.0], [1.0, 0.0, 0.0], [1.0, 0.0, 1.0], [0.0, 0.0, 1.0]]),
    // +Z
    ([0, 0, 1], [0.0, 0.0, 1.0],
     [[0.0, 0.0, 1.0], [1.0, 0.0, 1.0], [1.0, 1.0, 1.0], [0.0, 1.0, 1.0]]),
    // -Z
    ([0, 0, -1], [0.0, 0.0, -1.0],
     [[1.0, 0.0, 0.0], [0.0, 0.0, 0.0], [0.0, 1.0, 0.0], [1.0, 1.0, 0.0]]),
];

/// Builds the visible shell of one model.
///
/// Voxel space is Z up; the mesh is built Y up, because everything else in the
/// library is and a model that loads lying on its side is a worse default than
/// one the caller can rotate back.
pub fn build_geometry(model: &VoxModel) -> Geometry {
    let [sx, sy, sz] = model.size;
    let index_of = |x: i32, y: i32, z: i32| -> Option<usize> {
        if x < 0 || y < 0 || z < 0 || x >= sx as i32 || y >= sy as i32 || z >= sz as i32 {
            return None;
        }
        Some(((z as usize * sy as usize) + y as usize) * sx as usize + x as usize)
    };

    let mut filled = vec![false; (sx as usize).max(1) * (sy as usize).max(1) * (sz as usize).max(1)];
    for voxel in &model.voxels {
        if let Some(at) = index_of(voxel[0] as i32, voxel[1] as i32, voxel[2] as i32)
            && at < filled.len()
        {
            filled[at] = true;
        }
    }

    let mut vertices: Vec<Vertex> = Vec::new();
    let mut indices: Vec<u32> = Vec::new();
    // Centre the model on its own footprint, so a loaded model sits on the
    // origin rather than growing out of a corner.
    let centre = Vec3::new(sx as f32 * 0.5, sy as f32 * 0.5, 0.0);

    for voxel in &model.voxels {
        let (x, y, z) = (voxel[0] as i32, voxel[1] as i32, voxel[2] as i32);
        // The palette index picks a texel out of a 256 wide strip.
        let u = (voxel[3] as f32 + 0.5) / 256.0;

        for (offset, normal, corners) in FACES {
            let neighbour = index_of(x + offset[0], y + offset[1], z + offset[2]);
            if neighbour.is_some_and(|at| filled.get(at).copied().unwrap_or(false)) {
                // Something is pressed against this face; it can never be seen.
                continue;
            }

            let base = vertices.len() as u32;
            for corner in corners {
                let vx = x as f32 + corner[0] - centre.x;
                let vy = y as f32 + corner[1] - centre.y;
                let vz = z as f32 + corner[2];
                // Z up becomes Y up, keeping the handedness.
                vertices.push(Vertex::new(
                    Vec3::new(vx, vz, -vy),
                    Vec3::new(normal[0], normal[2], -normal[1]),
                    Vec2::new(u, 0.5),
                ));
            }
            indices.extend_from_slice(&[base, base + 1, base + 2, base, base + 2, base + 3]);
        }
    }

    let mut geometry = Geometry::new(vertices, indices);
    geometry.compute_tangents();
    geometry
}

/// Imports a `.vox` file into `scene` under `parent`.
pub fn load_vox(scene: &mut Scene, path: &str, parent: Option<NodeId>) -> Result<ImportResult> {
    let bytes = std::fs::read(path)?;
    let file = parse_vox(&bytes)?;

    // One palette texture for every model in the file.
    let pixels: Vec<u8> = file.palette.iter().flat_map(|c| *c).collect();
    let palette = Texture::new(256, 1, TextureFormat::Rgba8UnormSrgb, pixels)?;
    let palette_id = scene.add_texture(palette);
    // Nearest, or a voxel would take a blend of its own colour and whatever
    // sits next to it in the strip.
    if let Some(texture) = scene.texture_mut(palette_id) {
        texture.sampler.linear_filter = false;
        texture.sampler.mipmaps = false;
    }

    let material = scene.add_material(Material {
        textures: MaterialTextures {
            base_color: Some(palette_id),
            ..Default::default()
        },
        roughness: 0.9,
        metallic: 0.0,
        ..Default::default()
    });

    let name = Path::new(path)
        .file_stem()
        .map(|n| n.to_string_lossy().to_string())
        .unwrap_or_default();

    let root = scene.create_node(parent)?;
    if let Some(node) = scene.node_mut(root) {
        node.name = name;
    }

    let mut nodes = vec![root];
    let mut geometries = Vec::new();
    for (index, model) in file.models.iter().enumerate() {
        let geometry_id = scene.add_geometry(build_geometry(model));
        let node = scene.add_mesh(Some(root), geometry_id, material)?;
        if let Some(target) = scene.node_mut(node) {
            target.name = format!("model{index}");
        }
        nodes.push(node);
        geometries.push(geometry_id);
    }

    Ok(ImportResult {
        root,
        nodes,
        geometries,
        materials: vec![material],
        textures: vec![palette_id],
        animations: Vec::new(),
        skins: Vec::new(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Builds a minimal VOX file holding the given voxels in a size cube.
    fn file(size: [u32; 3], voxels: &[[u8; 4]]) -> Vec<u8> {
        fn chunk(id: &[u8; 4], content: &[u8], children: &[u8]) -> Vec<u8> {
            let mut out = id.to_vec();
            out.extend_from_slice(&(content.len() as u32).to_le_bytes());
            out.extend_from_slice(&(children.len() as u32).to_le_bytes());
            out.extend_from_slice(content);
            out.extend_from_slice(children);
            out
        }

        let mut size_content = Vec::new();
        for value in size {
            size_content.extend_from_slice(&value.to_le_bytes());
        }
        let mut xyzi_content = (voxels.len() as u32).to_le_bytes().to_vec();
        for voxel in voxels {
            xyzi_content.extend_from_slice(voxel);
        }

        let mut children = chunk(b"SIZE", &size_content, &[]);
        children.extend(chunk(b"XYZI", &xyzi_content, &[]));

        let mut out = b"VOX ".to_vec();
        out.extend_from_slice(&150u32.to_le_bytes());
        out.extend(chunk(b"MAIN", &[], &children));
        out
    }

    #[test]
    fn reads_one_voxel() {
        let parsed = parse_vox(&file([1, 1, 1], &[[0, 0, 0, 1]])).unwrap();
        assert_eq!(parsed.models.len(), 1);
        assert_eq!(parsed.models[0].voxels.len(), 1);
        assert_eq!(parsed.models[0].size, [1, 1, 1]);
    }

    #[test]
    fn a_lone_voxel_is_a_whole_cube() {
        let parsed = parse_vox(&file([1, 1, 1], &[[0, 0, 0, 1]])).unwrap();
        let geometry = build_geometry(&parsed.models[0]);
        // Six faces, four vertices each, two triangles each.
        assert_eq!(geometry.vertices.len(), 24);
        assert_eq!(geometry.indices.len(), 36);
    }

    #[test]
    fn touching_voxels_drop_the_faces_between_them() {
        let parsed = parse_vox(&file([2, 1, 1], &[[0, 0, 0, 1], [1, 0, 0, 1]])).unwrap();
        let geometry = build_geometry(&parsed.models[0]);
        // Two cubes would be twelve faces; the pair that touch cannot be seen,
        // so ten remain.
        assert_eq!(geometry.indices.len() / 6, 10);
    }

    #[test]
    fn a_solid_block_keeps_only_its_shell() {
        let mut voxels = Vec::new();
        for x in 0..4u8 {
            for y in 0..4u8 {
                for z in 0..4u8 {
                    voxels.push([x, y, z, 1]);
                }
            }
        }
        let parsed = parse_vox(&file([4, 4, 4], &voxels)).unwrap();
        let geometry = build_geometry(&parsed.models[0]);
        // A 4 cube has 64 voxels and 384 faces, of which only the 96 on the
        // outside can be seen.
        assert_eq!(geometry.indices.len() / 6, 96);
    }

    #[test]
    fn the_model_stands_up_the_right_way() {
        // Voxel space is Z up; a voxel at z = 3 has to end up high in Y, not
        // deep in Z, or every model loads lying on its side.
        let parsed = parse_vox(&file([1, 1, 4], &[[0, 0, 3, 1]])).unwrap();
        let geometry = build_geometry(&parsed.models[0]);
        let highest = geometry
            .vertices
            .iter()
            .map(|v| v.position[1])
            .fold(f32::MIN, f32::max);
        assert!(highest > 3.5, "the model did not stand up: highest y was {highest}");
    }

    #[test]
    fn the_palette_index_picks_a_texel() {
        let parsed = parse_vox(&file([1, 1, 1], &[[0, 0, 0, 7]])).unwrap();
        let geometry = build_geometry(&parsed.models[0]);
        let expected = (7.0 + 0.5) / 256.0;
        assert!((geometry.vertices[0].uv[0] - expected).abs() < 1e-6);
    }

    #[test]
    fn a_file_that_is_not_vox_is_an_error() {
        assert!(parse_vox(b"not a vox file at all").is_err());
        assert!(parse_vox(&[]).is_err());
    }

    #[test]
    fn a_model_claiming_more_voxels_than_it_holds_is_an_error() {
        let mut bytes = file([1, 1, 1], &[[0, 0, 0, 1]]);
        // Claim a thousand voxels in a chunk that holds one.
        let at = bytes.len() - 8;
        bytes[at..at + 4].copy_from_slice(&1000u32.to_le_bytes());
        assert!(parse_vox(&bytes).is_err());
    }
}
