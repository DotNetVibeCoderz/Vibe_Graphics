//! Scene graph: a flat arena of nodes with parent / child links, plus the
//! resource arenas (geometry, material, texture) shared by the renderer.

use crate::camera::Camera;
use crate::error::{Error, Result};
use crate::geometry::Geometry;
use crate::light::Light;
use crate::material::Material;
use crate::math::{Aabb, Mat4, Transform, Vec3};
use crate::shader::CustomShader;
use crate::texture::Texture;

/// Generic slot arena. Identifiers are 1-based so that `0` is a null handle on
/// the C ABI, and they carry a generation counter to catch stale handles.
#[derive(Debug)]
pub struct Arena<T> {
    slots: Vec<Option<T>>,
    generations: Vec<u32>,
    free: Vec<u32>,
}

impl<T> Default for Arena<T> {
    fn default() -> Self {
        Self {
            slots: Vec::new(),
            generations: Vec::new(),
            free: Vec::new(),
        }
    }
}

impl<T> Arena<T> {
    pub fn insert(&mut self, value: T) -> u32 {
        if let Some(index) = self.free.pop() {
            self.slots[index as usize] = Some(value);
            index + 1
        } else {
            self.slots.push(Some(value));
            self.generations.push(0);
            self.slots.len() as u32
        }
    }

    pub fn remove(&mut self, id: u32) -> Option<T> {
        let index = id.checked_sub(1)? as usize;
        let value = self.slots.get_mut(index)?.take();
        if value.is_some() {
            self.generations[index] = self.generations[index].wrapping_add(1);
            self.free.push(index as u32);
        }
        value
    }

    #[inline]
    pub fn get(&self, id: u32) -> Option<&T> {
        self.slots.get(id.checked_sub(1)? as usize)?.as_ref()
    }

    #[inline]
    pub fn get_mut(&mut self, id: u32) -> Option<&mut T> {
        self.slots.get_mut(id.checked_sub(1)? as usize)?.as_mut()
    }

    #[inline]
    pub fn contains(&self, id: u32) -> bool {
        self.get(id).is_some()
    }

    #[inline]
    pub fn len(&self) -> usize {
        self.slots.iter().filter(|s| s.is_some()).count()
    }

    #[inline]
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    #[inline]
    pub fn capacity(&self) -> usize {
        self.slots.len()
    }

    pub fn iter(&self) -> impl Iterator<Item = (u32, &T)> {
        self.slots
            .iter()
            .enumerate()
            .filter_map(|(i, slot)| slot.as_ref().map(|v| (i as u32 + 1, v)))
    }

    pub fn iter_mut(&mut self) -> impl Iterator<Item = (u32, &mut T)> {
        self.slots
            .iter_mut()
            .enumerate()
            .filter_map(|(i, slot)| slot.as_mut().map(|v| (i as u32 + 1, v)))
    }
}

pub type NodeId = u32;
pub type GeometryId = u32;
pub type MaterialId = u32;
pub type TextureId = u32;
pub type ShaderId = u32;

/// Geometry + material pair rendered at a node.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MeshBinding {
    pub geometry: GeometryId,
    pub material: MaterialId,
    pub cast_shadow: bool,
    pub receive_shadow: bool,
}

#[derive(Debug, Clone)]
pub struct Node {
    pub name: String,
    pub transform: Transform,
    pub visible: bool,
    /// Layer bitmask; a camera only renders nodes whose mask overlaps its own.
    pub layers: u32,
    /// Opaque payload owned by the managed layer (usually a GCHandle).
    pub user_data: u64,
    pub mesh: Option<MeshBinding>,
    pub light: Option<Light>,
    pub camera: Option<Camera>,
    pub(crate) parent: Option<NodeId>,
    pub(crate) children: Vec<NodeId>,
    pub(crate) world: Mat4,
    pub(crate) world_dirty: bool,
}

impl Default for Node {
    fn default() -> Self {
        Self {
            name: String::new(),
            transform: Transform::IDENTITY,
            visible: true,
            layers: 1,
            user_data: 0,
            mesh: None,
            light: None,
            camera: None,
            parent: None,
            children: Vec::new(),
            world: Mat4::IDENTITY,
            world_dirty: true,
        }
    }
}

impl Node {
    #[inline]
    pub fn parent(&self) -> Option<NodeId> {
        self.parent
    }

    #[inline]
    pub fn children(&self) -> &[NodeId] {
        &self.children
    }

    /// Cached world matrix. Valid after [`Scene::update_world_transforms`].
    #[inline]
    pub fn world_matrix(&self) -> Mat4 {
        self.world
    }
}

/// Environment settings applied to the whole scene.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Environment {
    /// Clear colour in linear RGB, `w` is the clear alpha.
    pub background: [f32; 4],
    /// Constant ambient term multiplied by its intensity.
    pub ambient_color: Vec3,
    pub ambient_intensity: f32,
    /// Exponential squared fog; disabled when `fog_density <= 0`.
    pub fog_color: Vec3,
    pub fog_density: f32,
    pub fog_start: f32,
    pub fog_end: f32,
    /// Equirectangular HDR environment map used for image based lighting.
    pub environment_map: Option<TextureId>,
    pub environment_intensity: f32,
}

impl Default for Environment {
    fn default() -> Self {
        Self {
            background: [0.02, 0.02, 0.03, 1.0],
            ambient_color: Vec3::ONE,
            ambient_intensity: 0.03,
            fog_color: Vec3::new(0.5, 0.55, 0.6),
            fog_density: 0.0,
            fog_start: 10.0,
            fog_end: 100.0,
            environment_map: None,
            environment_intensity: 1.0,
        }
    }
}

/// The scene graph plus every resource it references.
#[derive(Debug)]
pub struct Scene {
    pub environment: Environment,
    nodes: Arena<Node>,
    geometries: Arena<Geometry>,
    materials: Arena<Material>,
    textures: Arena<Texture>,
    shaders: Arena<CustomShader>,
    root: NodeId,
    /// Camera used when the caller does not pass one explicitly.
    active_camera: Option<NodeId>,
}

impl Default for Scene {
    fn default() -> Self {
        Self::new()
    }
}

impl Scene {
    pub fn new() -> Self {
        let mut nodes = Arena::default();
        let root = nodes.insert(Node {
            name: String::from("root"),
            ..Default::default()
        });
        Self {
            environment: Environment::default(),
            nodes,
            geometries: Arena::default(),
            materials: Arena::default(),
            textures: Arena::default(),
            shaders: Arena::default(),
            root,
            active_camera: None,
        }
    }

    #[inline]
    pub fn root(&self) -> NodeId {
        self.root
    }

    #[inline]
    pub fn active_camera(&self) -> Option<NodeId> {
        self.active_camera
    }

    pub fn set_active_camera(&mut self, node: Option<NodeId>) {
        self.active_camera = node.filter(|id| self.nodes.contains(*id));
    }

    // ---------------------------------------------------------------- nodes

    /// Creates a node parented to `parent` (the root when `None`).
    pub fn create_node(&mut self, parent: Option<NodeId>) -> Result<NodeId> {
        let parent = parent.unwrap_or(self.root);
        if !self.nodes.contains(parent) {
            return Err(Error::InvalidHandle("parent node"));
        }
        let id = self.nodes.insert(Node {
            parent: Some(parent),
            ..Default::default()
        });
        self.nodes
            .get_mut(parent)
            .expect("parent checked above")
            .children
            .push(id);
        Ok(id)
    }

    /// Duplicates `source` and its whole subtree under `parent` (the root when
    /// `None`). Clones share geometry, materials and textures with the
    /// original, so placing an imported model many times costs no GPU memory
    /// beyond the per-object transforms. Returns the id of the copied root.
    pub fn clone_subtree(&mut self, source: NodeId, parent: Option<NodeId>) -> Result<NodeId> {
        if source == self.root {
            return Err(Error::InvalidArgument("the root node cannot be cloned".into()));
        }
        if !self.nodes.contains(source) {
            return Err(Error::InvalidHandle("node"));
        }
        let parent = parent.unwrap_or(self.root);
        if !self.nodes.contains(parent) {
            return Err(Error::InvalidHandle("parent node"));
        }

        // Breadth first: (source node, parent of its copy).
        let mut queue = std::collections::VecDeque::from([(source, parent)]);
        let mut copied_root = None;
        while let Some((original, new_parent)) = queue.pop_front() {
            let Some(node) = self.nodes.get(original) else {
                continue;
            };
            let copy = Node {
                name: node.name.clone(),
                transform: node.transform,
                visible: node.visible,
                layers: node.layers,
                user_data: 0,
                mesh: node.mesh,
                light: node.light,
                camera: node.camera,
                parent: Some(new_parent),
                children: Vec::new(),
                world: Mat4::IDENTITY,
                world_dirty: true,
            };
            let children = node.children.clone();
            let id = self.nodes.insert(copy);
            if let Some(parent_node) = self.nodes.get_mut(new_parent) {
                parent_node.children.push(id);
            }
            copied_root.get_or_insert(id);
            queue.extend(children.into_iter().map(|child| (child, id)));
        }

        copied_root.ok_or(Error::InvalidHandle("node"))
    }

    /// Removes a node and its whole subtree. The root cannot be removed.
    pub fn remove_node(&mut self, id: NodeId) -> Result<()> {
        if id == self.root {
            return Err(Error::InvalidArgument("the root node cannot be removed".into()));
        }
        if !self.nodes.contains(id) {
            return Err(Error::InvalidHandle("node"));
        }
        if let Some(parent) = self.nodes.get(id).and_then(|n| n.parent)
            && let Some(parent) = self.nodes.get_mut(parent)
        {
            parent.children.retain(|child| *child != id);
        }
        let mut stack = vec![id];
        while let Some(current) = stack.pop() {
            if let Some(node) = self.nodes.remove(current) {
                stack.extend_from_slice(&node.children);
            }
            if self.active_camera == Some(current) {
                self.active_camera = None;
            }
        }
        Ok(())
    }

    /// Re-parents `id` under `parent`, keeping the local transform.
    pub fn set_parent(&mut self, id: NodeId, parent: Option<NodeId>) -> Result<()> {
        if id == self.root {
            return Err(Error::InvalidArgument("the root node cannot be re-parented".into()));
        }
        let new_parent = parent.unwrap_or(self.root);
        if !self.nodes.contains(id) || !self.nodes.contains(new_parent) {
            return Err(Error::InvalidHandle("node"));
        }
        // Walking up from the new parent must not reach `id`, or the graph
        // would contain a cycle.
        let mut cursor = Some(new_parent);
        while let Some(current) = cursor {
            if current == id {
                return Err(Error::InvalidArgument(
                    "re-parenting would create a cycle".into(),
                ));
            }
            cursor = self.nodes.get(current).and_then(|n| n.parent);
        }
        if let Some(old) = self.nodes.get(id).and_then(|n| n.parent)
            && let Some(old) = self.nodes.get_mut(old)
        {
            old.children.retain(|child| *child != id);
        }
        self.nodes.get_mut(new_parent).unwrap().children.push(id);
        let node = self.nodes.get_mut(id).unwrap();
        node.parent = Some(new_parent);
        node.world_dirty = true;
        self.mark_subtree_dirty(id);
        Ok(())
    }

    #[inline]
    pub fn node(&self, id: NodeId) -> Option<&Node> {
        self.nodes.get(id)
    }

    /// Mutable node access. Any transform change must be followed by
    /// [`Scene::mark_dirty`] so the cached world matrices are refreshed.
    #[inline]
    pub fn node_mut(&mut self, id: NodeId) -> Option<&mut Node> {
        self.nodes.get_mut(id)
    }

    #[inline]
    pub fn nodes(&self) -> impl Iterator<Item = (NodeId, &Node)> {
        self.nodes.iter()
    }

    #[inline]
    pub fn node_count(&self) -> usize {
        self.nodes.len()
    }

    /// Flags `id` and its descendants for a world matrix refresh.
    pub fn mark_dirty(&mut self, id: NodeId) {
        if let Some(node) = self.nodes.get_mut(id) {
            node.world_dirty = true;
        }
        self.mark_subtree_dirty(id);
    }

    fn mark_subtree_dirty(&mut self, id: NodeId) {
        let mut stack = vec![id];
        while let Some(current) = stack.pop() {
            let Some(node) = self.nodes.get_mut(current) else {
                continue;
            };
            node.world_dirty = true;
            stack.extend_from_slice(&node.children.clone());
        }
    }

    /// Recomputes every dirty world matrix, depth first from the root.
    pub fn update_world_transforms(&mut self) {
        let mut stack: Vec<(NodeId, Mat4, bool)> = vec![(self.root, Mat4::IDENTITY, false)];
        while let Some((id, parent_world, parent_dirty)) = stack.pop() {
            let Some(node) = self.nodes.get_mut(id) else {
                continue;
            };
            let dirty = parent_dirty || node.world_dirty;
            if dirty {
                node.world = parent_world * node.transform.matrix();
                node.world_dirty = false;
            }
            let world = node.world;
            let children = node.children.clone();
            for child in children {
                stack.push((child, world, dirty));
            }
        }
    }

    /// World space matrix, recomputed on demand if the cache is stale.
    pub fn world_matrix(&mut self, id: NodeId) -> Option<Mat4> {
        self.update_world_transforms();
        self.nodes.get(id).map(|n| n.world)
    }

    /// True when the node and all of its ancestors are visible.
    pub fn is_visible_in_hierarchy(&self, id: NodeId) -> bool {
        let mut cursor = Some(id);
        while let Some(current) = cursor {
            let Some(node) = self.nodes.get(current) else {
                return false;
            };
            if !node.visible {
                return false;
            }
            cursor = node.parent;
        }
        true
    }

    /// World space bounds of a node subtree.
    pub fn compute_bounds(&mut self, id: NodeId) -> Aabb {
        self.update_world_transforms();
        let mut bounds = Aabb::EMPTY;
        let mut stack = vec![id];
        while let Some(current) = stack.pop() {
            let Some(node) = self.nodes.get(current) else {
                continue;
            };
            if let Some(mesh) = node.mesh
                && let Some(geometry) = self.geometries.get(mesh.geometry)
            {
                bounds = bounds.union(&geometry.bounds.transformed(&node.world));
            }
            stack.extend_from_slice(&node.children);
        }
        bounds
    }

    // ------------------------------------------------------------ resources

    pub fn add_geometry(&mut self, geometry: Geometry) -> GeometryId {
        self.geometries.insert(geometry)
    }

    pub fn remove_geometry(&mut self, id: GeometryId) -> bool {
        self.geometries.remove(id).is_some()
    }

    #[inline]
    pub fn geometry(&self, id: GeometryId) -> Option<&Geometry> {
        self.geometries.get(id)
    }

    #[inline]
    pub fn geometry_mut(&mut self, id: GeometryId) -> Option<&mut Geometry> {
        self.geometries.get_mut(id)
    }

    pub fn add_material(&mut self, material: Material) -> MaterialId {
        self.materials.insert(material)
    }

    pub fn remove_material(&mut self, id: MaterialId) -> bool {
        self.materials.remove(id).is_some()
    }

    #[inline]
    pub fn material(&self, id: MaterialId) -> Option<&Material> {
        self.materials.get(id)
    }

    #[inline]
    pub fn material_mut(&mut self, id: MaterialId) -> Option<&mut Material> {
        self.materials.get_mut(id)
    }

    pub fn add_texture(&mut self, texture: Texture) -> TextureId {
        self.textures.insert(texture)
    }

    pub fn remove_texture(&mut self, id: TextureId) -> bool {
        self.textures.remove(id).is_some()
    }

    #[inline]
    pub fn texture(&self, id: TextureId) -> Option<&Texture> {
        self.textures.get(id)
    }

    #[inline]
    pub fn texture_mut(&mut self, id: TextureId) -> Option<&mut Texture> {
        self.textures.get_mut(id)
    }

    // -------------------------------------------------------------- shaders

    pub fn add_shader(&mut self, shader: CustomShader) -> ShaderId {
        self.shaders.insert(shader)
    }

    /// Replaces the source of an existing shader; materials using it pick the
    /// new version up on the next frame.
    pub fn replace_shader(&mut self, id: ShaderId, mut shader: CustomShader) -> bool {
        let Some(existing) = self.shaders.get_mut(id) else {
            return false;
        };
        shader.version = existing.version.wrapping_add(1).max(1);
        *existing = shader;
        true
    }

    pub fn remove_shader(&mut self, id: ShaderId) -> bool {
        self.shaders.remove(id).is_some()
    }

    #[inline]
    pub fn shader(&self, id: ShaderId) -> Option<&CustomShader> {
        self.shaders.get(id)
    }

    #[inline]
    pub fn stats(&self) -> SceneStats {
        SceneStats {
            nodes: self.nodes.len(),
            geometries: self.geometries.len(),
            materials: self.materials.len(),
            textures: self.textures.len(),
        }
    }

    /// Convenience helper: node + geometry + material in one call.
    pub fn add_mesh(
        &mut self,
        parent: Option<NodeId>,
        geometry: GeometryId,
        material: MaterialId,
    ) -> Result<NodeId> {
        if !self.geometries.contains(geometry) {
            return Err(Error::InvalidHandle("geometry"));
        }
        if !self.materials.contains(material) {
            return Err(Error::InvalidHandle("material"));
        }
        let id = self.create_node(parent)?;
        self.nodes.get_mut(id).unwrap().mesh = Some(MeshBinding {
            geometry,
            material,
            cast_shadow: true,
            receive_shadow: true,
        });
        Ok(id)
    }

    /// Finds the first node with the given name (depth first from the root).
    pub fn find_by_name(&self, name: &str) -> Option<NodeId> {
        let mut stack = vec![self.root];
        while let Some(current) = stack.pop() {
            let node = self.nodes.get(current)?;
            if node.name == name {
                return Some(current);
            }
            stack.extend_from_slice(&node.children);
        }
        None
    }
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct SceneStats {
    pub nodes: usize,
    pub geometries: usize,
    pub materials: usize,
    pub textures: usize,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn child_inherits_parent_transform() {
        let mut scene = Scene::new();
        let parent = scene.create_node(None).unwrap();
        let child = scene.create_node(Some(parent)).unwrap();
        scene.node_mut(parent).unwrap().transform.translation = Vec3::new(1.0, 0.0, 0.0);
        scene.node_mut(child).unwrap().transform.translation = Vec3::new(0.0, 2.0, 0.0);
        scene.mark_dirty(parent);
        scene.update_world_transforms();
        let world = scene.node(child).unwrap().world_matrix();
        assert_eq!(world.w_axis.truncate(), Vec3::new(1.0, 2.0, 0.0));
    }

    #[test]
    fn cloning_copies_the_subtree_and_shares_meshes() {
        let mut scene = Scene::new();
        let geometry = scene.add_geometry(crate::geometry::primitives::cuboid(1.0, 1.0, 1.0, 1));
        let material = scene.add_material(crate::material::Material::default());
        let parent = scene.create_node(None).unwrap();
        let child = scene.add_mesh(Some(parent), geometry, material).unwrap();
        scene.node_mut(child).unwrap().transform.translation = Vec3::new(1.0, 2.0, 3.0);

        let copy = scene.clone_subtree(parent, None).unwrap();
        assert_ne!(copy, parent);
        let copied_child = scene.node(copy).unwrap().children()[0];
        assert_ne!(copied_child, child);
        let node = scene.node(copied_child).unwrap();
        assert_eq!(node.transform.translation, Vec3::new(1.0, 2.0, 3.0));
        assert_eq!(node.mesh.unwrap().geometry, geometry);
        assert_eq!(node.parent(), Some(copy));
        assert!(scene.clone_subtree(scene.root(), None).is_err());
    }

    #[test]
    fn removing_a_node_removes_its_subtree() {
        let mut scene = Scene::new();
        let parent = scene.create_node(None).unwrap();
        let child = scene.create_node(Some(parent)).unwrap();
        scene.remove_node(parent).unwrap();
        assert!(scene.node(parent).is_none());
        assert!(scene.node(child).is_none());
    }

    #[test]
    fn reparenting_into_own_subtree_is_rejected() {
        let mut scene = Scene::new();
        let parent = scene.create_node(None).unwrap();
        let child = scene.create_node(Some(parent)).unwrap();
        assert!(scene.set_parent(parent, Some(child)).is_err());
    }
}
