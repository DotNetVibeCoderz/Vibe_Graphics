//! FBX importer (binary 7.x and ASCII).
//!
//! The file is parsed into a generic node tree, then the object graph
//! (models, meshes, materials, textures, skins and animation curves) is walked
//! through the `Connections` section. Supported: mesh geometry with normals,
//! UVs and per polygon materials (triangulated), the full FBX local transform
//! with pivots, offsets and pre/post rotation, geometric transforms, Lambert /
//! Phong materials with diffuse, normal and emissive textures (external files
//! or embedded `Video` content), skin clusters for skeletal animation, and
//! translation / rotation / scale animation curves. Axis and unit settings from
//! `GlobalSettings` are applied on the import root, so the result is Y-up metres.

use std::collections::HashMap;
use std::path::{Path, PathBuf};

use crate::animation::{AnimationClip, Channel, Interpolation, Skin, SkinWeights, TargetPath};
use crate::error::{Error, Result};
use crate::geometry::{Geometry, Vertex};
use crate::loaders::ImportResult;
use crate::material::{AlphaMode, CullMode, Material, ShadingModel};
use crate::math::{Mat4, Quat, Transform, Vec3, Vec4};
use crate::scene::{MeshBinding, NodeId, Scene};
use crate::texture::Texture;

/// FBX time units per second.
const KTIME_PER_SECOND: f64 = 46_186_158_000.0;

// =================================================================== document

#[derive(Debug, Clone, PartialEq)]
pub enum Property {
    Bool(bool),
    Int(i64),
    Float(f64),
    String(String),
    Raw(Vec<u8>),
    IntArray(Vec<i64>),
    FloatArray(Vec<f64>),
}

impl Property {
    pub fn as_i64(&self) -> Option<i64> {
        match self {
            Property::Int(v) => Some(*v),
            Property::Float(v) => Some(*v as i64),
            Property::Bool(v) => Some(i64::from(*v)),
            _ => None,
        }
    }

    pub fn as_f64(&self) -> Option<f64> {
        match self {
            Property::Float(v) => Some(*v),
            Property::Int(v) => Some(*v as f64),
            Property::Bool(v) => Some(f64::from(u8::from(*v))),
            _ => None,
        }
    }

    pub fn as_str(&self) -> Option<&str> {
        match self {
            Property::String(v) => Some(v),
            _ => None,
        }
    }
}

#[derive(Debug, Clone, Default)]
pub struct FbxNode {
    pub name: String,
    pub properties: Vec<Property>,
    pub children: Vec<FbxNode>,
}

impl FbxNode {
    pub fn child(&self, name: &str) -> Option<&FbxNode> {
        self.children.iter().find(|c| c.name == name)
    }

    pub fn children_named<'a>(&'a self, name: &'a str) -> impl Iterator<Item = &'a FbxNode> {
        self.children.iter().filter(move |c| c.name == name)
    }

    /// Numeric array stored either as an array property (binary) or in an
    /// `a:` child (ASCII).
    pub fn floats(&self) -> Vec<f64> {
        if let Some(values) = self.properties.iter().find_map(|p| match p {
            Property::FloatArray(v) => Some(v.clone()),
            Property::IntArray(v) => Some(v.iter().map(|x| *x as f64).collect()),
            _ => None,
        }) {
            return values;
        }
        if let Some(a) = self.child("a") {
            return a.properties.iter().filter_map(Property::as_f64).collect();
        }
        self.properties.iter().filter_map(Property::as_f64).collect()
    }

    pub fn ints(&self) -> Vec<i64> {
        if let Some(values) = self.properties.iter().find_map(|p| match p {
            Property::IntArray(v) => Some(v.clone()),
            Property::FloatArray(v) => Some(v.iter().map(|x| *x as i64).collect()),
            _ => None,
        }) {
            return values;
        }
        if let Some(a) = self.child("a") {
            return a.properties.iter().filter_map(Property::as_i64).collect();
        }
        self.properties.iter().filter_map(Property::as_i64).collect()
    }

    fn child_floats(&self, name: &str) -> Vec<f64> {
        self.child(name).map(FbxNode::floats).unwrap_or_default()
    }

    fn child_ints(&self, name: &str) -> Vec<i64> {
        self.child(name).map(FbxNode::ints).unwrap_or_default()
    }

    fn child_string(&self, name: &str) -> Option<String> {
        self.child(name)
            .and_then(|c| c.properties.first())
            .and_then(Property::as_str)
            .map(str::to_string)
    }

    /// `Properties70` value by name: the values after the four header fields.
    fn property70(&self, name: &str) -> Option<Vec<Property>> {
        let block = self.child("Properties70")?;
        block
            .children_named("P")
            .find(|p| p.properties.first().and_then(Property::as_str) == Some(name))
            .map(|p| p.properties.iter().skip(4).cloned().collect())
    }

    fn vec3_property(&self, name: &str, default: Vec3) -> Vec3 {
        match self.property70(name) {
            Some(values) if values.len() >= 3 => Vec3::new(
                values[0].as_f64().unwrap_or(0.0) as f32,
                values[1].as_f64().unwrap_or(0.0) as f32,
                values[2].as_f64().unwrap_or(0.0) as f32,
            ),
            _ => default,
        }
    }

    fn f64_property(&self, name: &str, default: f64) -> f64 {
        self.property70(name)
            .and_then(|v| v.first().and_then(Property::as_f64))
            .unwrap_or(default)
    }

}

// ============================================================ binary parser

struct BinaryReader<'a> {
    data: &'a [u8],
    position: usize,
    wide: bool,
}

impl BinaryReader<'_> {
    fn take(&mut self, count: usize) -> Result<&[u8]> {
        let end = self
            .position
            .checked_add(count)
            .filter(|end| *end <= self.data.len())
            .ok_or_else(|| Error::Asset("FBX file is truncated".into()))?;
        let slice = &self.data[self.position..end];
        self.position = end;
        Ok(slice)
    }

    fn u8(&mut self) -> Result<u8> {
        Ok(self.take(1)?[0])
    }

    fn u32(&mut self) -> Result<u32> {
        Ok(u32::from_le_bytes(self.take(4)?.try_into().unwrap()))
    }

    fn offset(&mut self) -> Result<u64> {
        if self.wide {
            Ok(u64::from_le_bytes(self.take(8)?.try_into().unwrap()))
        } else {
            Ok(self.u32()? as u64)
        }
    }

    fn node(&mut self) -> Result<Option<FbxNode>> {
        let end = self.offset()? as usize;
        let property_count = self.offset()?;
        let _property_bytes = self.offset()?;
        let name_length = self.u8()? as usize;
        if end == 0 {
            // Null record terminating a child list.
            return Ok(None);
        }
        let name = String::from_utf8_lossy(self.take(name_length)?).to_string();
        let mut node = FbxNode {
            name,
            properties: Vec::with_capacity(property_count as usize),
            children: Vec::new(),
        };
        for _ in 0..property_count {
            node.properties.push(self.property()?);
        }
        let sentinel = if self.wide { 25 } else { 13 };
        while self.position + sentinel <= end {
            match self.node()? {
                Some(child) => node.children.push(child),
                None => break,
            }
        }
        self.position = end.max(self.position);
        Ok(Some(node))
    }

    fn property(&mut self) -> Result<Property> {
        let kind = self.u8()?;
        Ok(match kind {
            b'Y' => Property::Int(i16::from_le_bytes(self.take(2)?.try_into().unwrap()) as i64),
            b'C' => Property::Bool(self.u8()? != 0),
            b'I' => Property::Int(i32::from_le_bytes(self.take(4)?.try_into().unwrap()) as i64),
            b'F' => Property::Float(f32::from_le_bytes(self.take(4)?.try_into().unwrap()) as f64),
            b'D' => Property::Float(f64::from_le_bytes(self.take(8)?.try_into().unwrap())),
            b'L' => Property::Int(i64::from_le_bytes(self.take(8)?.try_into().unwrap())),
            b'S' => {
                let length = self.u32()? as usize;
                Property::String(String::from_utf8_lossy(self.take(length)?).to_string())
            }
            b'R' => {
                let length = self.u32()? as usize;
                Property::Raw(self.take(length)?.to_vec())
            }
            b'f' | b'd' | b'l' | b'i' | b'b' | b'c' => {
                let count = self.u32()? as usize;
                let encoding = self.u32()?;
                let length = self.u32()? as usize;
                let raw = self.take(length)?;
                let bytes = if encoding == 1 {
                    miniz_oxide::inflate::decompress_to_vec_zlib(raw)
                        .map_err(|e| Error::Asset(format!("FBX array decompression failed: {e:?}")))?
                } else {
                    raw.to_vec()
                };
                let element = match kind {
                    b'f' | b'i' => 4,
                    b'd' | b'l' => 8,
                    _ => 1,
                };
                if bytes.len() < count * element {
                    return Err(Error::Asset("FBX array is shorter than declared".into()));
                }
                let chunks = bytes.chunks_exact(element).take(count);
                match kind {
                    b'f' => Property::FloatArray(chunks.map(|c| f32::from_le_bytes(c.try_into().unwrap()) as f64).collect()),
                    b'd' => Property::FloatArray(chunks.map(|c| f64::from_le_bytes(c.try_into().unwrap())).collect()),
                    b'l' => Property::IntArray(chunks.map(|c| i64::from_le_bytes(c.try_into().unwrap())).collect()),
                    b'i' => Property::IntArray(chunks.map(|c| i32::from_le_bytes(c.try_into().unwrap()) as i64).collect()),
                    _ => Property::IntArray(chunks.map(|c| c[0] as i64).collect()),
                }
            }
            other => return Err(Error::Asset(format!("unknown FBX property type '{}'", other as char))),
        })
    }
}

fn parse_binary(data: &[u8]) -> Result<FbxNode> {
    let version = u32::from_le_bytes(data[23..27].try_into().unwrap());
    let mut reader = BinaryReader {
        data,
        position: 27,
        wide: version >= 7500,
    };
    let mut root = FbxNode::default();
    while reader.position < data.len() {
        match reader.node() {
            Ok(Some(node)) => root.children.push(node),
            Ok(None) => break,
            Err(error) if !root.children.is_empty() => {
                // The footer after the top level records is not a node.
                log::debug!("FBX footer ignored: {error}");
                break;
            }
            Err(error) => return Err(error),
        }
    }
    Ok(root)
}

// ============================================================= ASCII parser

#[derive(Debug, Clone, PartialEq)]
enum Token {
    Key(String),
    Word(String),
    Str(String),
    Number(String),
    Comma,
    Open,
    Close,
    Star,
}

fn tokenize(text: &str) -> Vec<Token> {
    let mut tokens = Vec::new();
    let chars: Vec<char> = text.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        let c = chars[i];
        match c {
            ';' => {
                while i < chars.len() && chars[i] != '\n' {
                    i += 1;
                }
            }
            '"' => {
                let start = i + 1;
                i += 1;
                while i < chars.len() && chars[i] != '"' {
                    i += 1;
                }
                tokens.push(Token::Str(chars[start..i.min(chars.len())].iter().collect()));
                i += 1;
            }
            ',' => {
                tokens.push(Token::Comma);
                i += 1;
            }
            '{' => {
                tokens.push(Token::Open);
                i += 1;
            }
            '}' => {
                tokens.push(Token::Close);
                i += 1;
            }
            '*' => {
                tokens.push(Token::Star);
                i += 1;
            }
            c if c.is_whitespace() => i += 1,
            c if c.is_ascii_digit() || c == '-' || c == '+' || c == '.' => {
                let start = i;
                while i < chars.len() && (chars[i].is_ascii_alphanumeric() || matches!(chars[i], '-' | '+' | '.')) {
                    i += 1;
                }
                tokens.push(Token::Number(chars[start..i].iter().collect()));
            }
            _ => {
                let start = i;
                while i < chars.len() && (chars[i].is_alphanumeric() || matches!(chars[i], '_' | '|')) {
                    i += 1;
                }
                if i == start {
                    i += 1;
                    continue;
                }
                let word: String = chars[start..i].iter().collect();
                if i < chars.len() && chars[i] == ':' {
                    tokens.push(Token::Key(word));
                    i += 1;
                } else {
                    tokens.push(Token::Word(word));
                }
            }
        }
    }
    tokens
}

fn parse_ascii(text: &str) -> Result<FbxNode> {
    let tokens = tokenize(text);
    let mut position = 0;
    let mut root = FbxNode::default();
    while position < tokens.len() {
        if let Some(node) = parse_ascii_node(&tokens, &mut position)? {
            root.children.push(node);
        } else {
            position += 1;
        }
    }
    Ok(root)
}

fn parse_ascii_node(tokens: &[Token], position: &mut usize) -> Result<Option<FbxNode>> {
    let Some(Token::Key(name)) = tokens.get(*position) else {
        return Ok(None);
    };
    *position += 1;
    let mut node = FbxNode {
        name: name.clone(),
        ..Default::default()
    };

    // Values until a key, a block, or the closing brace of the parent.
    loop {
        match tokens.get(*position) {
            Some(Token::Number(text)) => {
                node.properties.push(if text.contains(['.', 'e', 'E']) {
                    Property::Float(text.parse().unwrap_or(0.0))
                } else {
                    text.parse::<i64>()
                        .map(Property::Int)
                        .unwrap_or_else(|_| Property::Float(text.parse().unwrap_or(0.0)))
                });
                *position += 1;
            }
            Some(Token::Str(text)) => {
                node.properties.push(Property::String(text.clone()));
                *position += 1;
            }
            Some(Token::Word(word)) => {
                node.properties.push(match word.as_str() {
                    "T" | "Y" => Property::Bool(true),
                    "F" | "N" => Property::Bool(false),
                    _ => Property::String(word.clone()),
                });
                *position += 1;
            }
            Some(Token::Comma) | Some(Token::Star) => *position += 1,
            Some(Token::Open) => {
                *position += 1;
                while let Some(token) = tokens.get(*position) {
                    if *token == Token::Close {
                        *position += 1;
                        break;
                    }
                    match parse_ascii_node(tokens, position)? {
                        Some(child) => node.children.push(child),
                        None => *position += 1,
                    }
                }
                break;
            }
            _ => break,
        }
    }

    // `Name: *count { a: ... }` arrays: the count is not a real property.
    if node.child("a").is_some() && node.properties.len() == 1 {
        node.properties.clear();
    }
    Ok(Some(node))
}

/// Parses binary or ASCII FBX into a node tree.
pub fn parse(data: &[u8]) -> Result<FbxNode> {
    if data.len() > 27 && data.starts_with(b"Kaydara FBX Binary") {
        parse_binary(data)
    } else {
        let text = String::from_utf8_lossy(data);
        if !text.contains("FBXHeaderExtension") && !text.contains("Objects") {
            return Err(Error::Asset("not an FBX file".into()));
        }
        parse_ascii(&text)
    }
}

// ================================================================= importer

struct Object<'a> {
    node: &'a FbxNode,
    name: String,
    class: String,
    subclass: String,
}

#[derive(Default)]
struct Graph {
    /// child id -> parents (with the property name for OP links)
    parents: HashMap<i64, Vec<(i64, Option<String>)>>,
    /// parent id -> children
    children: HashMap<i64, Vec<(i64, Option<String>)>>,
}

impl Graph {
    fn children_of(&self, id: i64) -> &[(i64, Option<String>)] {
        self.children.get(&id).map(Vec::as_slice).unwrap_or(&[])
    }

    fn parents_of(&self, id: i64) -> &[(i64, Option<String>)] {
        self.parents.get(&id).map(Vec::as_slice).unwrap_or(&[])
    }
}

fn object_name(raw: &str) -> String {
    // Binary: "Name\0\x01Class"; ASCII: "Class::Name".
    if let Some(index) = raw.find("\u{0}\u{1}") {
        raw[..index].to_string()
    } else if let Some(index) = raw.find("::") {
        raw[index + 2..].to_string()
    } else {
        raw.to_string()
    }
}

fn euler_to_quat(degrees: Vec3, order: i64) -> Quat {
    let r = degrees * (std::f32::consts::PI / 180.0);
    let axis = |index: usize| match index {
        0 => Quat::from_rotation_x(r.x),
        1 => Quat::from_rotation_y(r.y),
        _ => Quat::from_rotation_z(r.z),
    };
    // FBX orders name the rotation applied first: eEulerXYZ rotates about X,
    // then Y, then Z, so R = Rz * Ry * Rx.
    let sequence = match order {
        1 => [0, 2, 1],
        2 => [1, 2, 0],
        3 => [1, 0, 2],
        4 => [2, 0, 1],
        5 => [2, 1, 0],
        _ => [0, 1, 2],
    };
    axis(sequence[2]) * axis(sequence[1]) * axis(sequence[0])
}

/// Model local matrix following the FBX transform stack:
/// T * Roff * Rp * Rpre * R * Rpost^-1 * Rp^-1 * Soff * Sp * S * Sp^-1.
fn model_matrix(model: &FbxNode, translation: Vec3, rotation: Vec3, scale: Vec3) -> Mat4 {
    let order = model.f64_property("RotationOrder", 0.0) as i64;
    let rotation_offset = model.vec3_property("RotationOffset", Vec3::ZERO);
    let rotation_pivot = model.vec3_property("RotationPivot", Vec3::ZERO);
    let scaling_offset = model.vec3_property("ScalingOffset", Vec3::ZERO);
    let scaling_pivot = model.vec3_property("ScalingPivot", Vec3::ZERO);
    let pre = euler_to_quat(model.vec3_property("PreRotation", Vec3::ZERO), 0);
    let post = euler_to_quat(model.vec3_property("PostRotation", Vec3::ZERO), 0);
    Mat4::from_translation(translation)
        * Mat4::from_translation(rotation_offset)
        * Mat4::from_translation(rotation_pivot)
        * Mat4::from_quat(pre)
        * Mat4::from_quat(euler_to_quat(rotation, order))
        * Mat4::from_quat(post.inverse())
        * Mat4::from_translation(-rotation_pivot)
        * Mat4::from_translation(scaling_offset)
        * Mat4::from_translation(scaling_pivot)
        * Mat4::from_scale(scale)
        * Mat4::from_translation(-scaling_pivot)
}

fn decompose(matrix: Mat4) -> Transform {
    let (scale, rotation, translation) = matrix.to_scale_rotation_translation();
    Transform {
        translation,
        rotation: rotation.normalize(),
        scale,
    }
}

fn matrix_from(values: &[f64]) -> Option<Mat4> {
    (values.len() >= 16).then(|| {
        let mut array = [0.0f32; 16];
        for (target, source) in array.iter_mut().zip(values) {
            *target = *source as f32;
        }
        Mat4::from_cols_array(&array)
    })
}

/// Imports an FBX file into `scene` under `parent`.
pub fn load_fbx(scene: &mut Scene, path: &str, parent: Option<NodeId>) -> Result<ImportResult> {
    let data = std::fs::read(path)?;
    let base = Path::new(path).parent().map(Path::to_path_buf).unwrap_or_default();
    let name = Path::new(path)
        .file_stem()
        .map(|n| n.to_string_lossy().to_string())
        .unwrap_or_else(|| "fbx".into());
    load_fbx_from_slice(scene, &data, parent, &name, Some(&base))
}

pub fn load_fbx_from_slice(
    scene: &mut Scene,
    data: &[u8],
    parent: Option<NodeId>,
    name: &str,
    base_directory: Option<&Path>,
) -> Result<ImportResult> {
    let document = parse(data)?;
    let objects_node = document
        .child("Objects")
        .ok_or_else(|| Error::Asset("the FBX file has no Objects section".into()))?;

    // ------------------------------------------------------------- objects
    let mut objects: HashMap<i64, Object> = HashMap::new();
    for node in &objects_node.children {
        let Some(id) = node.properties.first().and_then(Property::as_i64) else {
            continue;
        };
        let raw_name = node.properties.get(1).and_then(Property::as_str).unwrap_or_default();
        let subclass = node.properties.get(2).and_then(Property::as_str).unwrap_or_default();
        objects.insert(
            id,
            Object {
                node,
                name: object_name(raw_name),
                class: node.name.clone(),
                subclass: subclass.to_string(),
            },
        );
    }

    let mut graph = Graph::default();
    if let Some(connections) = document.child("Connections") {
        for link in connections.children_named("C") {
            let kind = link.properties.first().and_then(Property::as_str).unwrap_or_default();
            let (Some(child), Some(parent_id)) = (
                link.properties.get(1).and_then(Property::as_i64),
                link.properties.get(2).and_then(Property::as_i64),
            ) else {
                continue;
            };
            let property = (kind == "OP")
                .then(|| link.properties.get(3).and_then(Property::as_str).map(str::to_string))
                .flatten();
            graph.parents.entry(child).or_default().push((parent_id, property.clone()));
            graph.children.entry(parent_id).or_default().push((child, property));
        }
    }

    // --------------------------------------------------------------- root
    let root = scene.create_node(parent)?;
    let mut result = ImportResult {
        root,
        ..Default::default()
    };
    let settings = document.child("GlobalSettings");
    let unit = settings.map_or(1.0, |s| s.f64_property("UnitScaleFactor", 1.0)) as f32;
    let up_axis = settings.map_or(1, |s| s.f64_property("UpAxis", 1.0) as i64);
    let up_sign = settings.map_or(1.0, |s| s.f64_property("UpAxisSign", 1.0)) as f32;
    if let Some(node) = scene.node_mut(root) {
        node.name = name.to_string();
        // FBX units are centimetres by default; the engine works in metres.
        node.transform.scale = Vec3::splat(unit * 0.01);
        if up_axis == 2 {
            node.transform.rotation = Quat::from_rotation_x(-std::f32::consts::FRAC_PI_2 * up_sign);
        }
    }

    // ---------------------------------------------------------- materials
    let mut texture_cache: HashMap<i64, u32> = HashMap::new();
    let mut materials: HashMap<i64, u32> = HashMap::new();
    for (id, object) in objects.iter().filter(|(_, o)| o.class == "Material") {
        let node = object.node;
        let diffuse = node.vec3_property("DiffuseColor", Vec3::splat(0.8)) * node.f64_property("DiffuseFactor", 1.0) as f32;
        let emissive = node.vec3_property("EmissiveColor", Vec3::ZERO) * node.f64_property("EmissiveFactor", 1.0) as f32;
        let opacity = node
            .property70("Opacity")
            .and_then(|v| v.first().and_then(Property::as_f64))
            .unwrap_or_else(|| 1.0 - node.f64_property("TransparencyFactor", 0.0)) as f32;
        let shading_name = node.child_string("ShadingModel").unwrap_or_default().to_lowercase();
        let mut material = Material {
            name: object.name.clone(),
            shading: if shading_name.contains("lambert") { ShadingModel::Lambert } else { ShadingModel::Phong },
            base_color: Vec4::new(diffuse.x, diffuse.y, diffuse.z, opacity.clamp(0.0, 1.0)),
            emissive,
            specular: node.vec3_property("SpecularColor", Vec3::splat(0.2)) * node.f64_property("SpecularFactor", 1.0) as f32,
            shininess: node.f64_property("Shininess", node.f64_property("ShininessExponent", 20.0)) as f32,
            alpha_mode: if opacity < 0.999 { AlphaMode::Blend } else { AlphaMode::Opaque },
            cull_mode: CullMode::Back,
            ..Default::default()
        };

        for (texture_id, property) in graph.children_of(*id) {
            let Some(texture_object) = objects.get(texture_id).filter(|o| o.class == "Texture") else {
                continue;
            };
            let slot = property.as_deref().unwrap_or("DiffuseColor");
            let srgb = !matches!(slot, "NormalMap" | "Bump");
            let texture = match texture_cache.get(texture_id) {
                Some(existing) => Some(*existing),
                None => load_texture(scene, &objects, &graph, *texture_id, texture_object, base_directory, srgb).inspect(|texture| {
                    texture_cache.insert(*texture_id, *texture);
                    result.textures.push(*texture);
                }),
            };
            match slot {
                "DiffuseColor" | "Maya|baseColor" => material.textures.base_color = texture,
                "NormalMap" | "Bump" => material.textures.normal = texture,
                "EmissiveColor" => material.textures.emissive = texture,
                _ => {}
            }
        }

        let material_id = scene.add_material(material);
        materials.insert(*id, material_id);
        result.materials.push(material_id);
    }
    let fallback_material = scene.add_material(Material {
        shading: ShadingModel::Lambert,
        base_color: Vec4::new(0.8, 0.8, 0.8, 1.0),
        ..Default::default()
    });
    result.materials.push(fallback_material);

    // ------------------------------------------------------------- models
    let model_ids: Vec<i64> = objects
        .iter()
        .filter(|(_, o)| o.class == "Model")
        .map(|(id, _)| *id)
        .collect();
    let mut model_nodes: HashMap<i64, NodeId> = HashMap::new();
    for id in &model_ids {
        let object = &objects[id];
        let node_id = scene.create_node(Some(root))?;
        let model = object.node;
        let local = model_matrix(
            model,
            model.vec3_property("Lcl Translation", Vec3::ZERO),
            model.vec3_property("Lcl Rotation", Vec3::ZERO),
            model.vec3_property("Lcl Scaling", Vec3::ONE),
        );
        if let Some(node) = scene.node_mut(node_id) {
            node.name = object.name.clone();
            node.transform = decompose(local);
            node.visible = model.f64_property("Visibility", 1.0) > 0.5;
        }
        model_nodes.insert(*id, node_id);
        result.nodes.push(node_id);
    }

    // Parent models to models (anything else stays under the import root).
    for id in &model_ids {
        let node_id = model_nodes[id];
        if let Some(parent_id) = graph
            .parents_of(*id)
            .iter()
            .find_map(|(parent, _)| model_nodes.get(parent).copied())
        {
            scene.set_parent(node_id, Some(parent_id))?;
        }
    }

    // ------------------------------------------------------------- meshes
    let mut skinned_geometries: Vec<(i64, NodeId, Vec<(NodeId, u32)>)> = Vec::new();
    for id in &model_ids {
        let model_node = model_nodes[id];
        let model = objects[id].node;
        let geometric = Mat4::from_scale_rotation_translation(
            model.vec3_property("GeometricScaling", Vec3::ONE),
            euler_to_quat(model.vec3_property("GeometricRotation", Vec3::ZERO), 0),
            model.vec3_property("GeometricTranslation", Vec3::ZERO),
        );

        // Materials attached to the model, in slot order.
        let slot_materials: Vec<u32> = graph
            .children_of(*id)
            .iter()
            .filter_map(|(child, _)| materials.get(child).copied())
            .collect();

        for (geometry_id, _) in graph.children_of(*id) {
            let Some(geometry_object) = objects.get(geometry_id).filter(|o| o.class == "Geometry" && o.subclass == "Mesh") else {
                continue;
            };
            let parts = build_mesh(geometry_object.node, &geometric)?;
            let mut created = Vec::new();
            for (slot, geometry) in parts {
                let material = slot_materials.get(slot).copied().unwrap_or(fallback_material);
                let geometry = scene.add_geometry(geometry);
                result.geometries.push(geometry);
                let target = if created.is_empty() && slot_materials.len() <= 1 {
                    model_node
                } else {
                    let child = scene.create_node(Some(model_node))?;
                    result.nodes.push(child);
                    child
                };
                if let Some(node) = scene.node_mut(target) {
                    node.mesh = Some(MeshBinding {
                        geometry,
                        material,
                        cast_shadow: true,
                        receive_shadow: true,
                        skin: None,
                    });
                }
                created.push((target, geometry));
            }
            skinned_geometries.push((*geometry_id, model_node, created));
        }
    }

    // -------------------------------------------------------------- skins
    for (geometry_id, _model_node, parts) in &skinned_geometries {
        let Some(skin_deformer) = graph
            .children_of(*geometry_id)
            .iter()
            .find(|(child, _)| objects.get(child).is_some_and(|o| o.class == "Deformer" && o.subclass == "Skin"))
            .map(|(child, _)| *child)
        else {
            continue;
        };

        let mut joints: Vec<NodeId> = Vec::new();
        let mut inverse_bind_matrices: Vec<Mat4> = Vec::new();
        // Control point -> (joint index, weight) influences.
        let mut influences: HashMap<usize, Vec<(u16, f32)>> = HashMap::new();
        for (cluster_id, _) in graph.children_of(skin_deformer) {
            let Some(cluster) = objects.get(cluster_id).filter(|o| o.class == "Deformer" && o.subclass == "Cluster") else {
                continue;
            };
            let Some(bone) = graph
                .children_of(*cluster_id)
                .iter()
                .find_map(|(child, _)| model_nodes.get(child).copied())
            else {
                continue;
            };
            let joint_index = joints.len() as u16;
            joints.push(bone);
            let transform = matrix_from(&cluster.node.child_floats("Transform")).unwrap_or(Mat4::IDENTITY);
            let transform_link = matrix_from(&cluster.node.child_floats("TransformLink")).unwrap_or(Mat4::IDENTITY);
            inverse_bind_matrices.push(transform_link.inverse() * transform);
            let indices = cluster.node.child_ints("Indexes");
            let weights = cluster.node.child_floats("Weights");
            for (index, weight) in indices.iter().zip(weights.iter()) {
                influences.entry(*index as usize).or_default().push((joint_index, *weight as f32));
            }
        }
        if joints.is_empty() {
            continue;
        }

        let skin_id = scene.add_skin(Skin {
            name: objects[&skin_deformer].name.clone(),
            joints,
            inverse_bind_matrices,
        });
        result.skins.push(skin_id);

        for (node, geometry) in parts {
            let Some(target) = scene.geometry_mut(*geometry) else {
                continue;
            };
            let Some(sources) = target.skin.take().map(|s| s.joints) else {
                continue;
            };
            // `build_mesh` stored each vertex's control point in joints[i][0..2].
            let mut joints = Vec::with_capacity(sources.len());
            let mut weights = Vec::with_capacity(sources.len());
            for source in &sources {
                let control_point = source[0] as usize | ((source[1] as usize) << 16);
                let mut list = influences.get(&control_point).cloned().unwrap_or_default();
                list.sort_by(|a, b| b.1.total_cmp(&a.1));
                list.truncate(4);
                let total: f32 = list.iter().map(|(_, w)| *w).sum();
                let mut joint = [0u16; 4];
                let mut weight = [0f32; 4];
                for (slot, (index, w)) in list.iter().enumerate() {
                    joint[slot] = *index;
                    weight[slot] = if total > 0.0 { *w / total } else { 0.0 };
                }
                joints.push(joint);
                weights.push(weight);
            }
            target.skin = Some(SkinWeights {
                joints,
                weights,
                bind_pose: target.vertices.clone(),
            });
            if let Some(binding) = scene.node_mut(*node).and_then(|n| n.mesh.as_mut()) {
                binding.skin = Some(skin_id);
            }
        }
    }

    // Geometries without a skin keep no control point bookkeeping.
    for geometry in &result.geometries {
        if let Some(target) = scene.geometry_mut(*geometry)
            && target.skin.as_ref().is_some_and(|s| s.bind_pose.is_empty())
        {
            target.skin = None;
        }
    }

    // ---------------------------------------------------------- animation
    for (stack_id, stack) in objects.iter().filter(|(_, o)| o.class == "AnimationStack") {
        let mut clip = AnimationClip {
            name: stack.name.clone(),
            channels: Vec::new(),
        };
        for (layer_id, _) in graph.children_of(*stack_id) {
            if !objects.get(layer_id).is_some_and(|o| o.class == "AnimationLayer") {
                continue;
            }
            // Group curve nodes by the model they drive.
            let mut per_model: HashMap<i64, HashMap<String, i64>> = HashMap::new();
            for (curve_node_id, _) in graph.children_of(*layer_id) {
                if !objects.get(curve_node_id).is_some_and(|o| o.class == "AnimationCurveNode") {
                    continue;
                }
                for (model_id, property) in graph.parents_of(*curve_node_id) {
                    if model_nodes.contains_key(model_id)
                        && let Some(property) = property
                    {
                        per_model.entry(*model_id).or_default().insert(property.clone(), *curve_node_id);
                    }
                }
            }
            for (model_id, properties) in per_model {
                let model = objects[&model_id].node;
                let target = model_nodes[&model_id];
                if let Some(channels) = animate_model(model, target, &properties, &objects, &graph) {
                    clip.channels.extend(channels);
                }
            }
        }
        if !clip.channels.is_empty() {
            result.animations.push(scene.add_animation(clip));
        }
    }

    Ok(result)
}

/// Resolves a texture: embedded `Video` content first, then the file name
/// relative to the FBX, then the absolute path stored in the file.
fn load_texture(
    scene: &mut Scene,
    objects: &HashMap<i64, Object>,
    graph: &Graph,
    texture_id: i64,
    texture: &Object,
    base_directory: Option<&Path>,
    srgb: bool,
) -> Option<u32> {
    for (video_id, _) in graph.children_of(texture_id) {
        if let Some(video) = objects.get(video_id).filter(|o| o.class == "Video")
            && let Some(Property::Raw(bytes)) = video.node.child("Content").and_then(|c| c.properties.first())
            && !bytes.is_empty()
            && let Ok(decoded) = Texture::from_encoded_bytes(bytes, srgb)
        {
            return Some(scene.add_texture(decoded));
        }
    }

    let mut candidates: Vec<PathBuf> = Vec::new();
    for key in ["RelativeFilename", "FileName"] {
        if let Some(file) = texture.node.child_string(key) {
            let normalized = file.replace('\\', "/");
            if let Some(base) = base_directory {
                candidates.push(base.join(&normalized));
                if let Some(name) = Path::new(&normalized).file_name() {
                    candidates.push(base.join(name));
                }
            }
            candidates.push(PathBuf::from(normalized));
        }
    }
    candidates
        .into_iter()
        .find(|path| path.is_file())
        .and_then(|path| Texture::from_file(&path.to_string_lossy(), srgb).ok())
        .map(|decoded| scene.add_texture(decoded))
}

/// Builds one geometry per material slot from an FBX mesh. Each vertex's
/// control point index is stashed in `skin.joints` so skin clusters can be
/// resolved afterwards.
fn build_mesh(mesh: &FbxNode, geometric: &Mat4) -> Result<Vec<(usize, Geometry)>> {
    let positions = mesh.child_floats("Vertices");
    let polygon_indices = mesh.child_ints("PolygonVertexIndex");
    if positions.len() < 9 || polygon_indices.len() < 3 {
        return Ok(Vec::new());
    }

    let normal_layer = mesh.child("LayerElementNormal");
    let uv_layer = mesh.child("LayerElementUV");
    let material_layer = mesh.child("LayerElementMaterial");

    let layer_value = |layer: Option<&FbxNode>, data_name: &str, index_name: &str, components: usize, control_point: usize, polygon_vertex: usize, polygon: usize| -> Option<Vec<f64>> {
        let layer = layer?;
        let data = layer.child_floats(data_name);
        let mapping = layer.child_string("MappingInformationType").unwrap_or_default();
        let reference = layer.child_string("ReferenceInformationType").unwrap_or_default();
        let mut index = match mapping.as_str() {
            "ByVertex" | "ByVertice" | "ByControlPoint" => control_point,
            "ByPolygon" => polygon,
            "AllSame" => 0,
            _ => polygon_vertex,
        };
        if reference == "IndexToDirect" || reference == "Index" {
            let indices = layer.child_ints(index_name);
            index = *indices.get(index)? as usize;
        }
        let start = index * components;
        (start + components <= data.len()).then(|| data[start..start + components].to_vec())
    };

    let normal_matrix = Mat4::from_mat3(crate::math::Mat3::from_mat4(*geometric).inverse().transpose());
    let materials = material_layer.map(|layer| layer.child_ints("Materials")).unwrap_or_default();
    let all_same = material_layer
        .and_then(|layer| layer.child_string("MappingInformationType"))
        .is_some_and(|m| m == "AllSame");

    let mut parts: HashMap<usize, (Vec<Vertex>, Vec<u32>, Vec<[u16; 4]>)> = HashMap::new();
    let mut polygon: Vec<(usize, usize)> = Vec::new(); // (control point, polygon vertex)
    let mut polygon_index = 0usize;

    for (polygon_vertex, raw) in polygon_indices.iter().enumerate() {
        let end = *raw < 0;
        let control_point = if end { (-raw - 1) as usize } else { *raw as usize };
        polygon.push((control_point, polygon_vertex));
        if !end {
            continue;
        }

        let slot = if all_same {
            materials.first().copied().unwrap_or(0) as usize
        } else {
            materials.get(polygon_index).copied().unwrap_or(0) as usize
        };
        let (vertices, indices, stash) = parts.entry(slot).or_default();
        let base = vertices.len() as u32;
        for (control_point, polygon_vertex) in &polygon {
            let p = control_point * 3;
            if p + 3 > positions.len() {
                return Err(Error::Asset("FBX polygon references a missing vertex".into()));
            }
            let position = geometric.transform_point3(Vec3::new(positions[p] as f32, positions[p + 1] as f32, positions[p + 2] as f32));
            let normal = layer_value(normal_layer, "Normals", "NormalsIndex", 3, *control_point, *polygon_vertex, polygon_index)
                .map(|n| normal_matrix.transform_vector3(Vec3::new(n[0] as f32, n[1] as f32, n[2] as f32)).normalize_or(Vec3::Y))
                .unwrap_or(Vec3::Y);
            let uv = layer_value(uv_layer, "UV", "UVIndex", 2, *control_point, *polygon_vertex, polygon_index)
                .map(|t| [t[0] as f32, 1.0 - t[1] as f32])
                .unwrap_or([0.0, 0.0]);
            vertices.push(Vertex {
                position: position.to_array(),
                normal: normal.to_array(),
                uv,
                tangent: [1.0, 0.0, 0.0, 1.0],
            });
            stash.push([(*control_point & 0xFFFF) as u16, (*control_point >> 16) as u16, 0, 0]);
        }
        // Fan triangulation of the (convex) polygon.
        for i in 1..polygon.len().saturating_sub(1) {
            indices.extend_from_slice(&[base, base + i as u32, base + i as u32 + 1]);
        }
        polygon.clear();
        polygon_index += 1;
    }

    let mut result: Vec<(usize, Geometry)> = parts
        .into_iter()
        .filter(|(_, (vertices, indices, _))| !vertices.is_empty() && !indices.is_empty())
        .map(|(slot, (vertices, indices, stash))| {
            let mut geometry = Geometry::new(vertices, indices);
            geometry.name = object_name(mesh.properties.get(1).and_then(Property::as_str).unwrap_or_default());
            if normal_layer.is_none() {
                geometry.compute_normals();
            }
            geometry.compute_tangents();
            // Temporary control point bookkeeping, consumed by the skin pass.
            geometry.skin = Some(SkinWeights {
                joints: stash,
                weights: Vec::new(),
                bind_pose: Vec::new(),
            });
            (slot, geometry)
        })
        .collect();
    result.sort_by_key(|(slot, _)| *slot);
    Ok(result)
}

/// Evaluates an FBX animation curve at `time` (linear between keys).
fn evaluate_curve(times: &[f64], values: &[f64], time: f64, default: f64) -> f64 {
    if times.is_empty() || values.is_empty() {
        return default;
    }
    if time <= times[0] {
        return values[0];
    }
    let last = times.len().min(values.len()) - 1;
    if time >= times[last] {
        return values[last];
    }
    let next = times.partition_point(|t| *t <= time).min(last);
    let key = next.saturating_sub(1);
    let span = (times[next] - times[key]).max(1e-9);
    let u = (time - times[key]) / span;
    values[key] + (values[next] - values[key]) * u
}

/// Converts the Lcl Translation / Rotation / Scaling curves of one model into
/// engine channels, re-evaluating the full FBX transform stack per key.
fn animate_model(
    model: &FbxNode,
    target: NodeId,
    properties: &HashMap<String, i64>,
    objects: &HashMap<i64, Object>,
    graph: &Graph,
) -> Option<Vec<Channel>> {
    // property -> [x, y, z] curves as (times seconds, values)
    let mut curves: HashMap<&str, [(Vec<f64>, Vec<f64>, f64); 3]> = HashMap::new();
    let mut all_times: Vec<f64> = Vec::new();

    for property in ["Lcl Translation", "Lcl Rotation", "Lcl Scaling"] {
        let static_default = match property {
            "Lcl Scaling" => model.vec3_property(property, Vec3::ONE),
            _ => model.vec3_property(property, Vec3::ZERO),
        };
        let mut axes = [
            (Vec::new(), Vec::new(), static_default.x as f64),
            (Vec::new(), Vec::new(), static_default.y as f64),
            (Vec::new(), Vec::new(), static_default.z as f64),
        ];
        if let Some(curve_node_id) = properties.get(property) {
            if let Some(curve_node) = objects.get(curve_node_id) {
                for (axis, name) in ["d|X", "d|Y", "d|Z"].iter().enumerate() {
                    axes[axis].2 = curve_node.node.f64_property(name, axes[axis].2);
                }
            }
            for (curve_id, channel) in graph.children_of(*curve_node_id) {
                let Some(curve) = objects.get(curve_id).filter(|o| o.class == "AnimationCurve") else {
                    continue;
                };
                let axis = match channel.as_deref() {
                    Some("d|X") => 0,
                    Some("d|Y") => 1,
                    Some("d|Z") => 2,
                    _ => continue,
                };
                let times: Vec<f64> = curve.node.child_ints("KeyTime").iter().map(|t| *t as f64 / KTIME_PER_SECOND).collect();
                let values = curve.node.child_floats("KeyValueFloat");
                all_times.extend_from_slice(&times);
                axes[axis].0 = times;
                axes[axis].1 = values;
            }
        }
        curves.insert(property, axes);
    }

    if all_times.is_empty() {
        return None;
    }
    all_times.sort_by(f64::total_cmp);
    all_times.dedup_by(|a, b| (*a - *b).abs() < 1e-6);
    let start = all_times[0];

    let sample = |property: &str, time: f64| -> Vec3 {
        let axes = &curves[property];
        Vec3::new(
            evaluate_curve(&axes[0].0, &axes[0].1, time, axes[0].2) as f32,
            evaluate_curve(&axes[1].0, &axes[1].1, time, axes[1].2) as f32,
            evaluate_curve(&axes[2].0, &axes[2].1, time, axes[2].2) as f32,
        )
    };

    let mut translations = Vec::with_capacity(all_times.len() * 3);
    let mut rotations = Vec::with_capacity(all_times.len() * 4);
    let mut scales = Vec::with_capacity(all_times.len() * 3);
    let mut previous_rotation = Quat::IDENTITY;
    for (index, time) in all_times.iter().enumerate() {
        let local = model_matrix(
            model,
            sample("Lcl Translation", *time),
            sample("Lcl Rotation", *time),
            sample("Lcl Scaling", *time),
        );
        let transform = decompose(local);
        translations.extend_from_slice(&transform.translation.to_array());
        // Keep neighbouring quaternions in the same hemisphere for slerp.
        let mut rotation = transform.rotation;
        if index > 0 && previous_rotation.dot(rotation) < 0.0 {
            rotation = -rotation;
        }
        previous_rotation = rotation;
        rotations.extend_from_slice(&rotation.to_array());
        scales.extend_from_slice(&transform.scale.to_array());
    }

    let times: Vec<f32> = all_times.iter().map(|t| (*t - start) as f32).collect();
    let channel = |path, values| Channel {
        target,
        path,
        interpolation: Interpolation::Linear,
        times: times.clone(),
        values,
    };
    Some(vec![
        channel(TargetPath::Translation, translations),
        channel(TargetPath::Rotation, rotations),
        channel(TargetPath::Scale, scales),
    ])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ascii_tokens_and_arrays() {
        let text = "; comment\nObjects:  {\n\tGeometry: 12, \"Geometry::Box\", \"Mesh\" {\n\t\tVertices: *6 {\n\t\t\ta: 0,1.5,-2,3e0,4,5\n\t\t}\n\t}\n}\n";
        let document = parse_ascii(text).unwrap();
        let geometry = &document.child("Objects").unwrap().children[0];
        assert_eq!(geometry.properties[0].as_i64(), Some(12));
        assert_eq!(object_name(geometry.properties[1].as_str().unwrap()), "Box");
        assert_eq!(geometry.child_floats("Vertices"), vec![0.0, 1.5, -2.0, 3.0, 4.0, 5.0]);
    }

    #[test]
    fn euler_order_xyz_matches_fbx() {
        // FBX XYZ: rotate about X first, then Y, then Z (R = Rz * Ry * Rx).
        let q = euler_to_quat(Vec3::new(90.0, 90.0, 0.0), 0);
        let expected = Quat::from_rotation_y(std::f32::consts::FRAC_PI_2) * Quat::from_rotation_x(std::f32::consts::FRAC_PI_2);
        assert!(q.dot(expected).abs() > 0.99999, "{q:?} vs {expected:?}");
    }
}
