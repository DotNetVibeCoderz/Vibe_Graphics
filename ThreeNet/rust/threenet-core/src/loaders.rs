//! Asset importers. glTF 2.0 / GLB is the primary format; OBJ is supported for
//! simple static meshes.

use std::collections::HashMap;
use std::path::Path;

use crate::error::{Error, Result};
use crate::geometry::{Geometry, Vertex};
use crate::light::{Light, LightKind};
use crate::material::{AlphaMode, CullMode, Material, ShadingModel};
use crate::math::{Quat, Transform, Vec2, Vec3, Vec4};
use crate::scene::{NodeId, Scene};
use crate::texture::{SamplerDesc, Texture, TextureFormat, WrapMode};

/// Result of importing a file: the root node plus what was created, so callers
/// can post-process (e.g. re-centre or rescale) the import.
#[derive(Debug, Clone, Default)]
pub struct ImportResult {
    pub root: NodeId,
    pub nodes: Vec<NodeId>,
    pub geometries: Vec<u32>,
    pub materials: Vec<u32>,
    pub textures: Vec<u32>,
}

/// Imports a `.gltf` or `.glb` file into `scene` under `parent`.
pub fn load_gltf(scene: &mut Scene, path: &str, parent: Option<NodeId>) -> Result<ImportResult> {
    let (document, buffers, images) =
        gltf::import(path).map_err(|e| Error::Asset(format!("glTF import failed: {e}")))?;
    let base = Path::new(path)
        .file_name()
        .map(|n| n.to_string_lossy().to_string())
        .unwrap_or_default();
    build_gltf(scene, document, buffers, images, parent, &base)
}

/// Imports glTF / GLB from memory (no external file references are resolved).
pub fn load_gltf_from_slice(
    scene: &mut Scene,
    bytes: &[u8],
    parent: Option<NodeId>,
) -> Result<ImportResult> {
    let (document, buffers, images) = gltf::import_slice(bytes)
        .map_err(|e| Error::Asset(format!("glTF import failed: {e}")))?;
    build_gltf(scene, document, buffers, images, parent, "gltf")
}

fn build_gltf(
    scene: &mut Scene,
    document: gltf::Document,
    buffers: Vec<gltf::buffer::Data>,
    images: Vec<gltf::image::Data>,
    parent: Option<NodeId>,
    name: &str,
) -> Result<ImportResult> {
    let root = scene.create_node(parent)?;
    if let Some(node) = scene.node_mut(root) {
        node.name = name.to_string();
    }
    let mut result = ImportResult {
        root,
        ..Default::default()
    };

    // ------------------------------------------------------------- textures
    // glTF images are shared between materials; upload each one once and let
    // the colour space depend on the slot it is used in.
    let mut texture_cache: HashMap<(usize, bool), u32> = HashMap::new();

    // ------------------------------------------------------------ materials
    let mut materials: Vec<u32> = Vec::with_capacity(document.materials().len());
    for material in document.materials() {
        let pbr = material.pbr_metallic_roughness();
        let base_color = pbr.base_color_factor();
        let emissive = material.emissive_factor();
        let mut converted = Material {
            name: material.name().unwrap_or_default().to_string(),
            shading: ShadingModel::Pbr,
            base_color: Vec4::from_array(base_color),
            emissive: Vec3::from_array(emissive),
            metallic: pbr.metallic_factor(),
            roughness: pbr.roughness_factor(),
            alpha_mode: match material.alpha_mode() {
                gltf::material::AlphaMode::Opaque => AlphaMode::Opaque,
                gltf::material::AlphaMode::Mask => AlphaMode::Mask,
                gltf::material::AlphaMode::Blend => AlphaMode::Blend,
            },
            alpha_cutoff: material.alpha_cutoff().unwrap_or(0.5),
            cull_mode: if material.double_sided() {
                CullMode::None
            } else {
                CullMode::Back
            },
            ..Default::default()
        };

        let mut import = |texture: gltf::Texture, srgb: bool| -> Option<u32> {
            import_gltf_texture(scene, &images, &mut texture_cache, &mut result.textures, texture, srgb)
        };

        converted.textures.base_color = pbr
            .base_color_texture()
            .and_then(|info| import(info.texture(), true));
        converted.textures.metallic_roughness = pbr
            .metallic_roughness_texture()
            .and_then(|info| import(info.texture(), false));
        if let Some(info) = material.normal_texture() {
            converted.normal_scale = info.scale();
            converted.textures.normal = import(info.texture(), false);
        }
        if let Some(info) = material.occlusion_texture() {
            converted.occlusion_strength = info.strength();
            converted.textures.occlusion = import(info.texture(), false);
        }
        converted.textures.emissive = material
            .emissive_texture()
            .and_then(|info| import(info.texture(), true));
        drop(import);

        let id = scene.add_material(converted);
        result.materials.push(id);
        materials.push(id);
    }
    let fallback_material = scene.add_material(Material::default());
    result.materials.push(fallback_material);

    // ----------------------------------------------------------- geometries
    // One glTF mesh can hold several primitives; each becomes its own geometry.
    let mut meshes: Vec<Vec<(u32, u32)>> = Vec::with_capacity(document.meshes().len());
    for mesh in document.meshes() {
        let mut primitives = Vec::new();
        for primitive in mesh.primitives() {
            if primitive.mode() != gltf::mesh::Mode::Triangles {
                log::warn!(
                    "skipping glTF primitive with unsupported mode {:?}",
                    primitive.mode()
                );
                continue;
            }
            let reader = primitive.reader(|buffer| buffers.get(buffer.index()).map(|b| &b.0[..]));
            let Some(positions) = reader.read_positions() else {
                continue;
            };
            let positions: Vec<[f32; 3]> = positions.collect();
            let normals: Option<Vec<[f32; 3]>> = reader.read_normals().map(|n| n.collect());
            let uvs: Option<Vec<[f32; 2]>> =
                reader.read_tex_coords(0).map(|uv| uv.into_f32().collect());
            let tangents: Option<Vec<[f32; 4]>> = reader.read_tangents().map(|t| t.collect());

            let vertices: Vec<Vertex> = positions
                .iter()
                .enumerate()
                .map(|(i, position)| Vertex {
                    position: *position,
                    normal: normals
                        .as_ref()
                        .and_then(|n| n.get(i).copied())
                        .unwrap_or([0.0, 1.0, 0.0]),
                    uv: uvs.as_ref().and_then(|u| u.get(i).copied()).unwrap_or([0.0; 2]),
                    tangent: tangents
                        .as_ref()
                        .and_then(|t| t.get(i).copied())
                        .unwrap_or([1.0, 0.0, 0.0, 1.0]),
                })
                .collect();
            let indices: Vec<u32> = reader
                .read_indices()
                .map(|i| i.into_u32().collect())
                .unwrap_or_default();

            let mut geometry = Geometry::new(vertices, indices);
            geometry.name = mesh.name().unwrap_or_default().to_string();
            if normals.is_none() {
                geometry.compute_normals();
            }
            if tangents.is_none() {
                geometry.compute_tangents();
            }
            let geometry_id = scene.add_geometry(geometry);
            result.geometries.push(geometry_id);

            let material_id = primitive
                .material()
                .index()
                .and_then(|i| materials.get(i).copied())
                .unwrap_or(fallback_material);
            primitives.push((geometry_id, material_id));
        }
        meshes.push(primitives);
    }

    // ---------------------------------------------------------------- nodes
    let gltf_scene = document
        .default_scene()
        .or_else(|| document.scenes().next())
        .ok_or_else(|| Error::Asset("the glTF file has no scene".into()))?;
    for node in gltf_scene.nodes() {
        import_gltf_node(scene, &node, root, &meshes, &mut result)?;
    }

    Ok(result)
}

fn import_gltf_node(
    scene: &mut Scene,
    node: &gltf::Node,
    parent: NodeId,
    meshes: &[Vec<(u32, u32)>],
    result: &mut ImportResult,
) -> Result<()> {
    let id = scene.create_node(Some(parent))?;
    result.nodes.push(id);

    let (translation, rotation, scale) = node.transform().decomposed();
    if let Some(target) = scene.node_mut(id) {
        target.name = node.name().unwrap_or_default().to_string();
        target.transform = Transform {
            translation: Vec3::from_array(translation),
            rotation: Quat::from_array(rotation),
            scale: Vec3::from_array(scale),
        };
    }

    if let Some(mesh) = node.mesh()
        && let Some(primitives) = meshes.get(mesh.index())
    {
        match primitives.len() {
            0 => {}
            // A single primitive lives directly on the node.
            1 => {
                let (geometry, material) = primitives[0];
                if let Some(target) = scene.node_mut(id) {
                    target.mesh = Some(crate::scene::MeshBinding {
                        geometry,
                        material,
                        cast_shadow: true,
                        receive_shadow: true,
                    });
                }
            }
            // Several primitives become child nodes so each keeps its material.
            _ => {
                for (geometry, material) in primitives.iter().copied() {
                    let child = scene.add_mesh(Some(id), geometry, material)?;
                    result.nodes.push(child);
                }
            }
        }
    }

    if let Some(camera) = node.camera() {
        let converted = match camera.projection() {
            gltf::camera::Projection::Perspective(p) => crate::camera::Camera {
                projection: crate::camera::Projection::Perspective {
                    fov_y: p.yfov(),
                    aspect: p.aspect_ratio(),
                    near: p.znear(),
                    far: p.zfar().unwrap_or(f32::INFINITY),
                },
                viewport: None,
            },
            gltf::camera::Projection::Orthographic(o) => crate::camera::Camera {
                projection: crate::camera::Projection::Orthographic {
                    height: o.ymag() * 2.0,
                    aspect: None,
                    near: o.znear(),
                    far: o.zfar(),
                },
                viewport: None,
            },
        };
        if let Some(target) = scene.node_mut(id) {
            target.camera = Some(converted);
        }
    }

    #[cfg(feature = "gltf-lights")]
    if let Some(light) = node.light() {
        let converted = Light {
            kind: match light.kind() {
                gltf::khr_lights_punctual::Kind::Directional => LightKind::Directional,
                gltf::khr_lights_punctual::Kind::Point => LightKind::Point,
                gltf::khr_lights_punctual::Kind::Spot { .. } => LightKind::Spot,
            },
            color: Vec3::from_array(light.color()),
            intensity: light.intensity(),
            range: light.range().unwrap_or(0.0),
            ..Default::default()
        };
        if let Some(target) = scene.node_mut(id) {
            target.light = Some(converted);
        }
    }

    for child in node.children() {
        import_gltf_node(scene, &child, id, meshes, result)?;
    }
    Ok(())
}

/// Uploads a glTF image once per (image, colour space) pair.
fn import_gltf_texture(
    scene: &mut Scene,
    images: &[gltf::image::Data],
    cache: &mut HashMap<(usize, bool), u32>,
    imported: &mut Vec<u32>,
    texture: gltf::Texture,
    srgb: bool,
) -> Option<u32> {
    let source = texture.source().index();
    if let Some(existing) = cache.get(&(source, srgb)) {
        return Some(*existing);
    }
    let data = images.get(source)?;
    let mut converted = gltf_image_to_texture(data, srgb)?;
    converted.name = texture.name().unwrap_or_default().to_string();
    converted.sampler = gltf_sampler(&texture);
    let id = scene.add_texture(converted);
    cache.insert((source, srgb), id);
    imported.push(id);
    Some(id)
}

fn gltf_sampler(texture: &gltf::Texture) -> SamplerDesc {
    let sampler = texture.sampler();
    let wrap = |mode: gltf::texture::WrappingMode| match mode {
        gltf::texture::WrappingMode::ClampToEdge => WrapMode::ClampToEdge,
        gltf::texture::WrappingMode::MirroredRepeat => WrapMode::MirrorRepeat,
        gltf::texture::WrappingMode::Repeat => WrapMode::Repeat,
    };
    SamplerDesc {
        wrap_u: wrap(sampler.wrap_s()),
        wrap_v: wrap(sampler.wrap_t()),
        linear_filter: !matches!(
            sampler.mag_filter(),
            Some(gltf::texture::MagFilter::Nearest)
        ),
        mipmaps: true,
        anisotropy: 8,
    }
}

fn gltf_image_to_texture(data: &gltf::image::Data, srgb: bool) -> Option<Texture> {
    use gltf::image::Format;
    let pixel_count = (data.width as usize) * (data.height as usize);
    let mut rgba = Vec::with_capacity(pixel_count * 4);
    match data.format {
        Format::R8G8B8A8 => rgba.extend_from_slice(&data.pixels),
        Format::R8G8B8 => {
            for chunk in data.pixels.chunks_exact(3) {
                rgba.extend_from_slice(&[chunk[0], chunk[1], chunk[2], 255]);
            }
        }
        Format::R8 => {
            for value in &data.pixels {
                rgba.extend_from_slice(&[*value, *value, *value, 255]);
            }
        }
        Format::R8G8 => {
            for chunk in data.pixels.chunks_exact(2) {
                rgba.extend_from_slice(&[chunk[0], chunk[1], 0, 255]);
            }
        }
        other => {
            log::warn!("unsupported glTF image format {other:?}");
            return None;
        }
    }
    Texture::new(
        data.width,
        data.height,
        if srgb {
            TextureFormat::Rgba8UnormSrgb
        } else {
            TextureFormat::Rgba8Unorm
        },
        rgba,
    )
    .ok()
}

/// Minimal Wavefront OBJ importer (positions, UVs, normals, triangulated
/// polygons). Materials are not read; a default material is assigned.
pub fn load_obj(scene: &mut Scene, path: &str, parent: Option<NodeId>) -> Result<ImportResult> {
    let text = std::fs::read_to_string(path)?;
    let geometry = parse_obj(&text)?;
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
    })
}

pub fn parse_obj(text: &str) -> Result<Geometry> {
    let mut positions: Vec<Vec3> = Vec::new();
    let mut uvs: Vec<Vec2> = Vec::new();
    let mut normals: Vec<Vec3> = Vec::new();
    let mut vertices: Vec<Vertex> = Vec::new();
    let mut indices: Vec<u32> = Vec::new();
    // OBJ indexes each attribute separately; this maps a unique triple to a
    // single interleaved vertex.
    let mut cache: HashMap<(i64, i64, i64), u32> = HashMap::new();
    let mut has_normals = false;

    for line in text.lines() {
        let line = line.trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        let mut parts = line.split_whitespace();
        match parts.next() {
            Some("v") => {
                let values = parse_floats(parts, 3)?;
                positions.push(Vec3::new(values[0], values[1], values[2]));
            }
            Some("vt") => {
                let values = parse_floats(parts, 2)?;
                // OBJ texture space is bottom-up.
                uvs.push(Vec2::new(values[0], 1.0 - values[1]));
            }
            Some("vn") => {
                let values = parse_floats(parts, 3)?;
                normals.push(Vec3::new(values[0], values[1], values[2]));
            }
            Some("f") => {
                let face: Vec<&str> = parts.collect();
                if face.len() < 3 {
                    continue;
                }
                let mut face_indices = Vec::with_capacity(face.len());
                for token in &face {
                    let key = parse_obj_index(token, positions.len(), uvs.len(), normals.len())?;
                    let index = match cache.get(&key) {
                        Some(index) => *index,
                        None => {
                            let position = positions
                                .get(key.0 as usize)
                                .copied()
                                .ok_or_else(|| Error::Asset("OBJ vertex out of range".into()))?;
                            let uv = if key.1 >= 0 {
                                uvs.get(key.1 as usize).copied().unwrap_or(Vec2::ZERO)
                            } else {
                                Vec2::ZERO
                            };
                            let normal = if key.2 >= 0 {
                                has_normals = true;
                                normals.get(key.2 as usize).copied().unwrap_or(Vec3::Y)
                            } else {
                                Vec3::Y
                            };
                            let index = vertices.len() as u32;
                            vertices.push(Vertex::new(position, normal, uv));
                            cache.insert(key, index);
                            index
                        }
                    };
                    face_indices.push(index);
                }
                // Fan triangulation of the polygon.
                for i in 1..face_indices.len() - 1 {
                    indices.extend_from_slice(&[
                        face_indices[0],
                        face_indices[i],
                        face_indices[i + 1],
                    ]);
                }
            }
            _ => {}
        }
    }

    if vertices.is_empty() {
        return Err(Error::Asset("the OBJ file contains no geometry".into()));
    }
    let mut geometry = Geometry::new(vertices, indices);
    if !has_normals {
        geometry.compute_normals();
    }
    geometry.compute_tangents();
    Ok(geometry)
}

fn parse_floats<'a>(parts: impl Iterator<Item = &'a str>, expected: usize) -> Result<Vec<f32>> {
    let values: Vec<f32> = parts.filter_map(|p| p.parse::<f32>().ok()).collect();
    if values.len() < expected {
        return Err(Error::Asset(format!(
            "expected {expected} numbers in an OBJ line, found {}",
            values.len()
        )));
    }
    Ok(values)
}

/// Parses `v`, `v/vt`, `v//vn` or `v/vt/vn`, resolving negative (relative)
/// indices. Returns zero based indices, `-1` when the slot is absent.
fn parse_obj_index(
    token: &str,
    positions: usize,
    uvs: usize,
    normals: usize,
) -> Result<(i64, i64, i64)> {
    let resolve = |value: &str, count: usize| -> i64 {
        match value.parse::<i64>() {
            Ok(index) if index > 0 => index - 1,
            Ok(index) if index < 0 => count as i64 + index,
            _ => -1,
        }
    };
    let mut parts = token.split('/');
    let position = resolve(parts.next().unwrap_or(""), positions);
    if position < 0 {
        return Err(Error::Asset(format!("invalid OBJ face index '{token}'")));
    }
    let uv = parts.next().map(|v| resolve(v, uvs)).unwrap_or(-1);
    let normal = parts.next().map(|v| resolve(v, normals)).unwrap_or(-1);
    Ok((position, uv, normal))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_a_triangle_from_obj() {
        let obj = "v 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 0 1\nf 1/1 2/2 3/3\n";
        let geometry = parse_obj(obj).unwrap();
        assert_eq!(geometry.vertices.len(), 3);
        assert_eq!(geometry.indices, vec![0, 1, 2]);
    }

    #[test]
    fn triangulates_quads() {
        let obj = "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nf 1 2 3 4\n";
        let geometry = parse_obj(obj).unwrap();
        assert_eq!(geometry.indices.len(), 6);
    }
}
