//! Compute shaders: work that is not drawing.
//!
//! A kernel is a WGSL module with a `@compute` entry point, and the things it
//! reads and writes are storage buffers the scene owns. Dispatching one binds
//! the buffers in the order they were given, at `@group(0) @binding(0)` upwards,
//! and an optional block of parameters after them.
//!
//! This is the foundation two other things were waiting on - particles that are
//! stepped on the GPU, and skinning that does not round trip through the CPU -
//! but it is useful on its own for anything that wants to chew through a buffer
//! without the result ever reaching system memory.
//!
//! Compute is not universal: WebGL2 has none, and an adapter can report that it
//! cannot. Dispatching on such a device reports rather than failing silently,
//! and [`Renderer::supports_compute`] answers the question beforehand.

/// A storage buffer the scene owns, which a kernel can read and write.
#[derive(Debug, Clone)]
pub struct StorageBuffer {
    /// Size in bytes. Fixed once created.
    pub size: u64,
    /// Pending CPU side contents, uploaded before the next dispatch that uses
    /// it. `None` once the GPU copy is authoritative.
    pub(crate) pending: Option<Vec<u8>>,
    /// Bumped on every write, so the cache knows to upload again.
    pub(crate) version: u32,
}

impl StorageBuffer {
    pub fn new(size: u64) -> Self {
        Self {
            size,
            pending: None,
            version: 0,
        }
    }

    /// Queues `data` to be uploaded at `offset`.
    pub fn write(&mut self, offset: u64, data: &[u8]) -> bool {
        if offset + data.len() as u64 > self.size {
            return false;
        }
        let buffer = self.pending.get_or_insert_with(|| vec![0u8; self.size as usize]);
        let start = offset as usize;
        buffer[start..start + data.len()].copy_from_slice(data);
        self.version = self.version.wrapping_add(1);
        true
    }
}

/// A compiled compute kernel.
#[derive(Debug, Clone)]
pub struct ComputeKernel {
    pub name: String,
    pub source: String,
    /// The `@compute` function to run.
    pub entry_point: String,
    pub(crate) version: u32,
}

impl ComputeKernel {
    pub fn new(name: impl Into<String>, source: impl Into<String>, entry_point: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            source: source.into(),
            entry_point: entry_point.into(),
            version: 0,
        }
    }
}
