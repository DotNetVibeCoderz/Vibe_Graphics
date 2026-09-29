//! GPU frame timing with timestamp queries.
//!
//! Two timestamps bracket the frame's command buffer. The resolved values are
//! copied into a mappable buffer and read on a later frame, so a measurement
//! never stalls the CPU: [`GpuTimer::poll`] picks up whatever has finished and
//! the reported time simply lags a frame or two behind.
//!
//! Adapters without `TIMESTAMP_QUERY` (and `TIMESTAMP_QUERY_INSIDE_ENCODERS`,
//! which arbitrary encoder timestamps need) get [`GpuTimer::disabled`], which
//! reports `0.0` and costs nothing.

use std::sync::Arc;
use std::sync::atomic::{AtomicU8, Ordering};

/// Mapping states shared with the `map_async` callback.
const WAITING: u8 = 0;
const MAPPED: u8 = 1;
const FAILED: u8 = 2;

/// Features a device needs before frame timing works.
pub fn required_features() -> wgpu::Features {
    wgpu::Features::TIMESTAMP_QUERY | wgpu::Features::TIMESTAMP_QUERY_INSIDE_ENCODERS
}

/// Bytes two resolved timestamps take up.
const RESOLVED_BYTES: u64 = 16;

pub struct GpuTimer {
    inner: Option<Inner>,
    last_ms: f32,
}

struct Inner {
    query_set: wgpu::QuerySet,
    resolve: wgpu::Buffer,
    readback: wgpu::Buffer,
    /// Nanoseconds per timestamp tick.
    period_ns: f32,
    /// A mapping is in flight for a frame already submitted.
    pending: bool,
    /// Written by the map callback: `WAITING`, `MAPPED` or `FAILED`.
    state: Arc<AtomicU8>,
}

impl std::fmt::Debug for GpuTimer {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("GpuTimer")
            .field("supported", &self.inner.is_some())
            .field("last_ms", &self.last_ms)
            .finish()
    }
}

impl GpuTimer {
    /// A timer that measures nothing, for adapters without timestamp queries.
    pub fn disabled() -> Self {
        Self { inner: None, last_ms: 0.0 }
    }

    pub fn new(device: &wgpu::Device, queue: &wgpu::Queue) -> Self {
        if !device.features().contains(required_features()) {
            return Self::disabled();
        }

        let query_set = device.create_query_set(&wgpu::QuerySetDescriptor {
            label: Some("threenet.timestamps"),
            ty: wgpu::QueryType::Timestamp,
            count: 2,
        });
        let resolve = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("threenet.timestamps.resolve"),
            size: RESOLVED_BYTES,
            usage: wgpu::BufferUsages::QUERY_RESOLVE | wgpu::BufferUsages::COPY_SRC,
            mapped_at_creation: false,
        });
        let readback = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("threenet.timestamps.readback"),
            size: RESOLVED_BYTES,
            usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
            mapped_at_creation: false,
        });

        Self {
            inner: Some(Inner {
                query_set,
                resolve,
                readback,
                period_ns: queue.get_timestamp_period(),
                pending: false,
                state: Arc::new(AtomicU8::new(WAITING)),
            }),
            last_ms: 0.0,
        }
    }

    /// True when this adapter can time frames at all.
    #[inline]
    pub fn is_supported(&self) -> bool {
        self.inner.is_some()
    }

    /// GPU time of the most recently measured frame, in milliseconds
    /// (`0.0` when unsupported or when nothing has been measured yet).
    #[inline]
    pub fn last_ms(&self) -> f32 {
        self.last_ms
    }

    /// Collects a finished measurement without blocking.
    pub fn poll(&mut self, device: &wgpu::Device) {
        let Some(inner) = &mut self.inner else { return };
        if !inner.pending {
            return;
        }

        // Nudge the queue so map callbacks for submitted work can run.
        let _ = device.poll(wgpu::PollType::Poll);
        match inner.state.swap(WAITING, Ordering::AcqRel) {
            MAPPED => {}
            FAILED => {
                // Nothing to unmap; just make the next frame measurable again.
                inner.pending = false;
                return;
            }
            _ => return,
        }

        if let Ok(view) = inner.readback.slice(..).get_mapped_range() {
            let start = u64::from_ne_bytes(view[0..8].try_into().unwrap_or_default());
            let end = u64::from_ne_bytes(view[8..16].try_into().unwrap_or_default());
            drop(view);
            let ms = end.saturating_sub(start) as f64 * inner.period_ns as f64 / 1.0e6;
            // A wrapped or reset counter shows up as an absurd number; ignore it.
            if ms.is_finite() && ms < 10_000.0 {
                self.last_ms = ms as f32;
            }
        }

        inner.readback.unmap();
        inner.pending = false;
    }

    /// Writes the opening timestamp. Returns false when this frame is not timed.
    pub fn begin(&mut self, encoder: &mut wgpu::CommandEncoder) -> bool {
        let Some(inner) = &mut self.inner else {
            return false;
        };
        if inner.pending {
            // The previous measurement is still on its way back.
            return false;
        }
        encoder.write_timestamp(&inner.query_set, 0);
        true
    }

    /// Writes the closing timestamp and queues the readback copy. Call with the
    /// same encoder `begin` returned true for, before it is finished.
    pub fn end(&mut self, encoder: &mut wgpu::CommandEncoder, timed: bool) {
        if !timed {
            return;
        }
        let Some(inner) = &mut self.inner else { return };
        encoder.write_timestamp(&inner.query_set, 1);
        encoder.resolve_query_set(&inner.query_set, 0..2, &inner.resolve, 0);
        encoder.copy_buffer_to_buffer(&inner.resolve, 0, &inner.readback, 0, RESOLVED_BYTES);
    }

    /// Starts the readback for the frame just submitted.
    pub fn after_submit(&mut self, timed: bool) {
        if !timed {
            return;
        }
        let Some(inner) = &mut self.inner else { return };
        let state = Arc::clone(&inner.state);
        inner.readback.slice(..).map_async(wgpu::MapMode::Read, move |result| {
            state.store(if result.is_ok() { MAPPED } else { FAILED }, Ordering::Release);
        });
        inner.pending = true;
    }
}
