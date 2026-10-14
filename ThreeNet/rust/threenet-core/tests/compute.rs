//! Compute pipelines: a kernel that runs, reads and writes.

use threenet_core::compute::ComputeKernel;
use threenet_core::renderer::{Renderer, RendererConfig};
use threenet_core::scene::Scene;

fn renderer() -> Option<Renderer> {
    Renderer::new_offscreen(RendererConfig {
        width: 16,
        height: 16,
        msaa_samples: 1,
        ..Default::default()
    })
    .ok()
}

const DOUBLE: &str = r#"
@group(0) @binding(0) var<storage, read_write> values: array<u32>;

@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x >= arrayLength(&values)) {
        return;
    }
    values[id.x] = values[id.x] * 2u;
}
"#;

/// Reads a buffer back as `u32`, which is what these kernels work in.
fn as_u32(bytes: &[u8]) -> Vec<u32> {
    bytes
        .chunks_exact(4)
        .map(|chunk| u32::from_le_bytes([chunk[0], chunk[1], chunk[2], chunk[3]]))
        .collect()
}

#[test]
fn a_kernel_reads_and_writes_a_storage_buffer() {
    let Some(mut renderer) = renderer() else {
        return;
    };
    if !renderer.supports_compute() {
        return;
    }

    let mut scene = Scene::new();
    let count = 256usize;
    let buffer = scene.add_storage_buffer((count * 4) as u64);

    let input: Vec<u8> = (0..count as u32)
        .flat_map(|value| value.to_le_bytes())
        .collect();
    scene
        .storage_buffer_mut(buffer)
        .expect("just added")
        .write(0, &input);

    let kernel = scene.add_compute_kernel(ComputeKernel::new("double", DOUBLE, "main"));
    renderer
        .dispatch_compute(&mut scene, kernel, &[buffer], [(count as u32).div_ceil(64), 1, 1])
        .expect("dispatch");

    let output = as_u32(&renderer.read_storage_buffer(&scene, buffer).expect("readback"));
    assert_eq!(output.len(), count);
    for (index, value) in output.iter().enumerate() {
        assert_eq!(*value, index as u32 * 2, "element {index} was not doubled");
    }
}

#[test]
fn dispatches_accumulate_on_the_same_buffer() {
    let Some(mut renderer) = renderer() else {
        return;
    };
    if !renderer.supports_compute() {
        return;
    }

    let mut scene = Scene::new();
    let buffer = scene.add_storage_buffer(4 * 64);
    let input: Vec<u8> = (0..64u32).flat_map(|_| 1u32.to_le_bytes()).collect();
    scene
        .storage_buffer_mut(buffer)
        .expect("just added")
        .write(0, &input);

    let kernel = scene.add_compute_kernel(ComputeKernel::new("double", DOUBLE, "main"));
    // Three dispatches in a row: what the first wrote has to be what the second
    // reads, or the queue is not ordering them.
    for _ in 0..3 {
        renderer
            .dispatch_compute(&mut scene, kernel, &[buffer], [1, 1, 1])
            .expect("dispatch");
    }

    let output = as_u32(&renderer.read_storage_buffer(&scene, buffer).expect("readback"));
    assert!(output.iter().all(|value| *value == 8), "expected 1 doubled three times, got {:?}", &output[..4]);
}

#[test]
fn two_buffers_bind_in_the_order_they_are_given() {
    let Some(mut renderer) = renderer() else {
        return;
    };
    if !renderer.supports_compute() {
        return;
    }

    const COPY: &str = r#"
@group(0) @binding(0) var<storage, read_write> source: array<u32>;
@group(0) @binding(1) var<storage, read_write> destination: array<u32>;

@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x >= arrayLength(&source)) {
        return;
    }
    destination[id.x] = source[id.x] + 100u;
}
"#;

    let mut scene = Scene::new();
    let source = scene.add_storage_buffer(4 * 64);
    let destination = scene.add_storage_buffer(4 * 64);
    let input: Vec<u8> = (0..64u32).flat_map(|v| v.to_le_bytes()).collect();
    scene.storage_buffer_mut(source).unwrap().write(0, &input);

    let kernel = scene.add_compute_kernel(ComputeKernel::new("copy", COPY, "main"));
    renderer
        .dispatch_compute(&mut scene, kernel, &[source, destination], [1, 1, 1])
        .expect("dispatch");

    let out = as_u32(&renderer.read_storage_buffer(&scene, destination).unwrap());
    assert_eq!(out[0], 100);
    assert_eq!(out[63], 163);
    // The source must be untouched, which is what says the binding order was
    // not silently reversed.
    let unchanged = as_u32(&renderer.read_storage_buffer(&scene, source).unwrap());
    assert_eq!(unchanged[63], 63);
}

#[test]
fn a_dispatch_of_nothing_is_refused() {
    let Some(mut renderer) = renderer() else {
        return;
    };
    if !renderer.supports_compute() {
        return;
    }

    let mut scene = Scene::new();
    let buffer = scene.add_storage_buffer(16);
    let kernel = scene.add_compute_kernel(ComputeKernel::new("double", DOUBLE, "main"));

    // Zero workgroups does no work, which is almost always a bug in the caller
    // rather than an intention.
    assert!(renderer.dispatch_compute(&mut scene, kernel, &[buffer], [0, 1, 1]).is_err());
    // And a kernel needs something to work on.
    assert!(renderer.dispatch_compute(&mut scene, kernel, &[], [1, 1, 1]).is_err());
}

#[test]
fn a_buffer_read_before_any_dispatch_gives_back_what_was_staged() {
    let Some(mut renderer) = renderer() else {
        return;
    };

    let mut scene = Scene::new();
    let buffer = scene.add_storage_buffer(16);
    scene
        .storage_buffer_mut(buffer)
        .unwrap()
        .write(0, &7u32.to_le_bytes());

    let out = as_u32(&renderer.read_storage_buffer(&scene, buffer).unwrap());
    assert_eq!(out[0], 7);
}

#[test]
fn writing_past_the_end_is_refused() {
    let mut scene = Scene::new();
    let buffer = scene.add_storage_buffer(8);
    let handle = scene.storage_buffer_mut(buffer).unwrap();
    assert!(handle.write(0, &[0u8; 8]));
    assert!(!handle.write(4, &[0u8; 8]));
    assert!(!handle.write(9, &[0u8; 1]));
}
