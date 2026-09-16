//! Three.Net native core engine.
//!
//! A cross platform 3D engine (wgpu based) exposing a stable C ABI so that the
//! managed `ThreeNet` .NET library can drive rendering, scene graph and asset
//! loading with zero copy interop wherever possible.
//!
//! Made by Gravicode Studios - led by Kang Fadhil.

pub mod animation;
pub mod assets;
pub mod camera;
pub mod compressed;
pub mod error;
pub mod fbx;
pub mod ffi;
pub mod geometry;
pub mod light;
pub mod loaders;
pub mod material;
pub mod math;
pub mod raycast;
pub mod renderer;
pub mod scene;
pub mod shader;
pub mod texture;
pub mod window;

pub use camera::{Camera, Projection};
pub use error::{Error, Result};
pub use geometry::{Geometry, Vertex, primitives};
pub use light::{Light, LightKind};
pub use material::{AlphaMode, Material, ShadingModel};
pub use math::Transform;
pub use renderer::{RenderPath, Renderer, RendererConfig, ToneMapping};
pub use scene::{NodeId, Scene};

/// Semantic version of the native core, exposed to the managed layer so it can
/// verify that the shipped native binary matches the binding surface.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

/// ABI revision of the exported C surface. Bumped whenever an exported symbol
/// changes shape; the managed side refuses to load a mismatching binary.
pub const ABI_VERSION: u32 = 5;
