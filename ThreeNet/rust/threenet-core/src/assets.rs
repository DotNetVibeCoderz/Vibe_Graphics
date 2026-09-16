//! Texture streaming and the asset cache.
//!
//! Streaming: [`Scene::load_texture_async`] returns a texture id right away. The
//! texture shows a neutral placeholder while a small worker pool reads and
//! decodes the file; finished images are swapped in by
//! [`Scene::poll_streaming`] (the renderer calls it at the start of every
//! frame) with a per-frame budget so a burst of loads does not stall a frame.
//!
//! Cache: [`Scene::load_texture_cached`] and [`Scene::load_model_cached`] return
//! the already loaded resource for a path. Cached models are imported once
//! under a hidden prototype node and every request gets a clone that shares
//! geometry, materials and textures.

use std::collections::{HashMap, VecDeque};
use std::path::{Path, PathBuf};
use std::sync::mpsc::{Receiver, Sender, channel};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use crate::error::{Error, Result};
use crate::loaders::ImportResult;
use crate::scene::{NodeId, Scene, TextureId};
use crate::texture::Texture;

/// Loading state of a texture, as reported by [`Scene::texture_state`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u32)]
pub enum StreamState {
    /// No texture with that id.
    Missing = 0,
    /// The placeholder is shown while the image loads.
    Loading = 1,
    Ready = 2,
    /// Loading failed; the placeholder stays and [`Scene::texture_error`] says why.
    Failed = 3,
}

/// Counters describing streaming and the cache.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct AssetStats {
    pub pending_textures: u32,
    pub cached_textures: u32,
    pub cached_models: u32,
    pub cache_hits: u64,
    pub cache_misses: u64,
    pub streamed_textures: u64,
}

struct Job {
    ticket: u64,
    path: PathBuf,
    srgb: bool,
}

#[derive(Debug)]
struct Finished {
    ticket: u64,
    result: Result<Texture>,
}

struct Workers {
    jobs: Sender<Job>,
    results: Mutex<Receiver<Finished>>,
}

impl Workers {
    fn spawn() -> Self {
        let (jobs, job_receiver) = channel::<Job>();
        let (result_sender, results) = channel::<Finished>();
        let job_receiver = Arc::new(Mutex::new(job_receiver));
        let count = std::thread::available_parallelism().map_or(2, |n| n.get()).clamp(1, 4);
        for index in 0..count {
            let job_receiver = Arc::clone(&job_receiver);
            let result_sender = result_sender.clone();
            let _ = std::thread::Builder::new()
                .name(format!("threenet-assets-{index}"))
                .spawn(move || {
                    loop {
                        // The lock is held only while waiting for the next job.
                        let job = match job_receiver.lock() {
                            Ok(receiver) => receiver.recv(),
                            Err(_) => return,
                        };
                        let Ok(job) = job else {
                            return; // the scene was dropped
                        };
                        let result = Texture::from_file(&job.path.to_string_lossy(), job.srgb);
                        if result_sender.send(Finished { ticket: job.ticket, result }).is_err() {
                            return;
                        }
                    }
                });
        }
        Self {
            jobs,
            results: Mutex::new(results),
        }
    }
}

impl std::fmt::Debug for Workers {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("Workers")
    }
}

#[derive(Debug)]
struct Pending {
    texture: TextureId,
}

/// Streaming and cache bookkeeping owned by a [`Scene`].
#[derive(Debug)]
pub(crate) struct AssetManager {
    workers: Option<Workers>,
    next_ticket: u64,
    pending: HashMap<u64, Pending>,
    /// Finished loads waiting for an upload slot.
    ready: VecDeque<Finished>,
    failed: HashMap<TextureId, String>,
    textures: HashMap<(PathBuf, bool), TextureId>,
    models: HashMap<PathBuf, NodeId>,
    prototypes: Option<NodeId>,
    stats: AssetStats,
    /// Finished textures applied per [`Scene::poll_streaming`] call.
    pub(crate) uploads_per_poll: usize,
}

impl Default for AssetManager {
    fn default() -> Self {
        Self {
            workers: None,
            next_ticket: 1,
            pending: HashMap::new(),
            ready: VecDeque::new(),
            failed: HashMap::new(),
            textures: HashMap::new(),
            models: HashMap::new(),
            prototypes: None,
            stats: AssetStats::default(),
            uploads_per_poll: 4,
        }
    }
}

impl AssetManager {
    /// Called when a texture is removed so a recycled id is not mistaken for it.
    pub(crate) fn forget_texture(&mut self, id: TextureId) {
        self.pending.retain(|_, pending| pending.texture != id);
        self.failed.remove(&id);
        self.textures.retain(|_, cached| *cached != id);
    }

    fn is_pending(&self, id: TextureId) -> bool {
        self.pending.values().any(|pending| pending.texture == id)
    }
}

fn cache_key(path: &str) -> PathBuf {
    match std::fs::canonicalize(path) {
        // Windows returns verbatim `\\?\` paths, which do not accept the `/`
        // separators of relative texture URIs inside model files.
        Ok(canonical) => match canonical.to_str().and_then(|s| s.strip_prefix(r"\\?\")) {
            Some(stripped) if !stripped.starts_with("UNC") => PathBuf::from(stripped),
            _ => canonical,
        },
        Err(_) => PathBuf::from(path),
    }
}

fn placeholder(path: &Path, srgb: bool) -> Texture {
    let mut texture = Texture::solid([128, 128, 128, 255], srgb);
    texture.name = path.to_string_lossy().into_owned();
    texture.sampler = Default::default();
    texture
}

impl Scene {
    /// Starts loading an image file (PNG, JPEG, HDR, KTX2, Basis, ...) in the
    /// background and returns its texture id immediately. A grey placeholder
    /// is used until the image is ready. Loads are cached: asking for the same
    /// path and colour space again returns the same texture.
    pub fn load_texture_async(&mut self, path: &str, srgb: bool) -> Result<TextureId> {
        let key = cache_key(path);
        if let Some(&id) = self.assets.textures.get(&(key.clone(), srgb))
            && self.texture(id).is_some()
        {
            self.assets.stats.cache_hits += 1;
            return Ok(id);
        }
        if !key.is_file() {
            return Err(Error::Asset(format!("texture file not found: {path}")));
        }
        self.assets.stats.cache_misses += 1;

        let id = self.add_texture(placeholder(&key, srgb));
        let ticket = self.assets.next_ticket;
        self.assets.next_ticket += 1;
        let workers = self.assets.workers.get_or_insert_with(Workers::spawn);
        workers
            .jobs
            .send(Job {
                ticket,
                path: key.clone(),
                srgb,
            })
            .map_err(|_| Error::Asset("asset workers are not running".into()))?;
        self.assets.pending.insert(ticket, Pending { texture: id });
        self.assets.textures.insert((key, srgb), id);
        Ok(id)
    }

    /// Loads an image synchronously, or returns the texture already loaded
    /// (or loading) for that path and colour space.
    pub fn load_texture_cached(&mut self, path: &str, srgb: bool) -> Result<TextureId> {
        let key = cache_key(path);
        if let Some(&id) = self.assets.textures.get(&(key.clone(), srgb))
            && self.texture(id).is_some()
        {
            self.assets.stats.cache_hits += 1;
            return Ok(id);
        }
        self.assets.stats.cache_misses += 1;
        let texture = Texture::from_file(&key.to_string_lossy(), srgb)?;
        let id = self.add_texture(texture);
        self.assets.textures.insert((key, srgb), id);
        Ok(id)
    }

    /// Places a model, importing the file only the first time. Later calls
    /// clone the cached prototype, sharing its geometry, materials and
    /// textures. Models with animations or skins are imported every time,
    /// because clips target the nodes of one particular import.
    pub fn load_model_cached(&mut self, path: &str, parent: Option<NodeId>) -> Result<ImportResult> {
        let key = cache_key(path);
        if let Some(&prototype) = self.assets.models.get(&key)
            && self.node(prototype).is_some()
        {
            self.assets.stats.cache_hits += 1;
            return self.instantiate_prototype(prototype, parent);
        }
        self.assets.stats.cache_misses += 1;

        let key_string = key.to_string_lossy().into_owned();
        let extension = key
            .extension()
            .map(|e| e.to_string_lossy().to_ascii_lowercase())
            .unwrap_or_default();
        let import = |scene: &mut Scene, parent: Option<NodeId>| match extension.as_str() {
            "gltf" | "glb" => crate::loaders::load_gltf(scene, &key_string, parent),
            "fbx" => crate::fbx::load_fbx(scene, &key_string, parent),
            "obj" => crate::loaders::load_obj(scene, &key_string, parent),
            other => Err(Error::InvalidArgument(format!("no importer for '.{other}' files"))),
        };

        let holder = match self.assets.prototypes.filter(|id| self.node(*id).is_some()) {
            Some(holder) => holder,
            None => {
                let holder = self.create_node(None)?;
                let node = self.node_mut(holder).expect("just created");
                node.name = String::from("asset-cache");
                node.visible = false;
                self.assets.prototypes = Some(holder);
                holder
            }
        };

        let prototype = import(self, Some(holder))?;
        if !prototype.animations.is_empty() || !prototype.skins.is_empty() {
            // Not cacheable: move the import to where the caller wants it.
            self.set_parent(prototype.root, parent)?;
            return Ok(prototype);
        }
        self.assets.models.insert(key, prototype.root);
        let mut result = self.instantiate_prototype(prototype.root, parent)?;
        result.geometries = prototype.geometries;
        result.materials = prototype.materials;
        result.textures = prototype.textures;
        Ok(result)
    }

    fn instantiate_prototype(&mut self, prototype: NodeId, parent: Option<NodeId>) -> Result<ImportResult> {
        let root = self.clone_subtree(prototype, parent)?;
        let mut nodes = Vec::new();
        let mut stack = vec![root];
        while let Some(id) = stack.pop() {
            nodes.push(id);
            if let Some(node) = self.node(id) {
                stack.extend(node.children.iter().rev());
            }
        }
        Ok(ImportResult {
            root,
            nodes,
            geometries: Vec::new(),
            materials: Vec::new(),
            textures: Vec::new(),
            animations: Vec::new(),
            skins: Vec::new(),
        })
    }

    /// Applies background loads that have finished, at most
    /// `streaming_uploads_per_frame` per call. Returns how many were applied.
    pub fn poll_streaming(&mut self) -> usize {
        if let Some(workers) = &self.assets.workers
            && let Ok(results) = workers.results.lock()
        {
            while let Ok(finished) = results.try_recv() {
                self.assets.ready.push_back(finished);
            }
        }

        let mut applied = 0;
        while applied < self.assets.uploads_per_poll.max(1) {
            let Some(finished) = self.assets.ready.pop_front() else {
                break;
            };
            let Some(pending) = self.assets.pending.remove(&finished.ticket) else {
                continue; // the texture was removed meanwhile
            };
            let id = pending.texture;
            match finished.result {
                Ok(mut loaded) => {
                    let Some(texture) = self.texture_mut(id) else {
                        continue;
                    };
                    loaded.name = std::mem::take(&mut texture.name);
                    loaded.sampler = texture.sampler;
                    loaded.version = texture.version();
                    *texture = loaded;
                    texture.touch();
                    self.assets.stats.streamed_textures += 1;
                }
                Err(error) => {
                    log::warn!("streamed texture {id} failed: {error}");
                    self.assets.failed.insert(id, error.to_string());
                }
            }
            applied += 1;
        }
        applied
    }

    /// Blocks until every streamed texture is applied or `timeout` elapses.
    /// Returns true when nothing is pending any more.
    pub fn finish_streaming(&mut self, timeout: Duration) -> bool {
        let deadline = Instant::now() + timeout;
        loop {
            while self.poll_streaming() > 0 {}
            if self.assets.pending.is_empty() {
                return true;
            }
            if Instant::now() >= deadline {
                return false;
            }
            std::thread::sleep(Duration::from_millis(2));
        }
    }

    pub fn texture_state(&self, id: TextureId) -> StreamState {
        if self.texture(id).is_none() {
            StreamState::Missing
        } else if self.assets.is_pending(id) {
            StreamState::Loading
        } else if self.assets.failed.contains_key(&id) {
            StreamState::Failed
        } else {
            StreamState::Ready
        }
    }

    pub fn texture_error(&self, id: TextureId) -> Option<&str> {
        self.assets.failed.get(&id).map(String::as_str)
    }

    /// How many finished textures [`Scene::poll_streaming`] applies per call.
    pub fn set_streaming_uploads_per_frame(&mut self, count: usize) {
        self.assets.uploads_per_poll = count.max(1);
    }

    pub fn asset_stats(&self) -> AssetStats {
        AssetStats {
            pending_textures: self.assets.pending.len() as u32,
            cached_textures: self
                .assets
                .textures
                .values()
                .filter(|id| self.texture(**id).is_some())
                .count() as u32,
            cached_models: self
                .assets
                .models
                .values()
                .filter(|id| self.node(**id).is_some())
                .count() as u32,
            ..self.assets.stats
        }
    }

    /// Forgets cached paths and removes the hidden model prototypes. Textures
    /// and placed model instances stay in the scene.
    pub fn clear_asset_cache(&mut self) {
        self.assets.textures.clear();
        self.assets.models.clear();
        if let Some(holder) = self.assets.prototypes.take()
            && self.node(holder).is_some()
        {
            let _ = self.remove_node(holder);
        }
    }
}
