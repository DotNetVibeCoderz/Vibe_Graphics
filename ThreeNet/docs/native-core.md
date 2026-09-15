# Native core (Rust) / Inti native

`rust/threenet-core` is a `cdylib` + `rlib` crate (edition 2024).

## Build and test

```bash
cargo build --release --manifest-path rust/threenet-core/Cargo.toml
cargo test --manifest-path rust/threenet-core/Cargo.toml
cargo run  --manifest-path rust/threenet-core/Cargo.toml --example offscreen   # writes offscreen.png
cargo run  --manifest-path rust/threenet-core/Cargo.toml --example window      # native window
```

Outputs: `threenet_core.dll` (Windows), `libthreenet_core.so` (Linux), `libthreenet_core.dylib` (macOS) in
`rust/target/<profile>/`. The release profile uses fat LTO, one codegen unit and `panic = "abort"`.

## Modules

| Module | Responsibility |
|---|---|
| `math.rs` | `Transform`, `Aabb`, `Frustum` (on glam 0.33) |
| `geometry.rs` | 48 byte interleaved `Vertex`, normals/tangents, primitives |
| `material.rs`, `light.rs`, `camera.rs`, `texture.rs` | Component data |
| `scene.rs` | Generational `Arena<T>`, node graph, world matrices, resources |
| `renderer/mod.rs` | Instance/adapter/device, targets, draw collection, render, readback |
| `renderer/pipeline.rs` | Bind group layouts, pipeline cache |
| `renderer/resources.rs` | GPU caches, default textures, mipmap generator |
| `renderer/post.rs` | Bloom and tone mapping |
| `renderer/uniforms.rs` | `#[repr(C)]` uniform blocks matching the WGSL |
| `shaders/scene.wgsl`, `shaders/post.wgsl` | Uber shader and post passes |
| `loaders.rs` | glTF / GLB (with KHR_lights_punctual), OBJ |
| `raycast.rs` | Ray / AABB / triangle intersection |
| `window.rs` | winit 0.30 application host, input translation |
| `ffi.rs` | The C ABI |

## Adding an FFI function

1. Implement the behaviour in the relevant module with safe Rust.
2. Add an `extern "C"` function in `ffi.rs`: validate pointers with the `scene_ref!` / `renderer_ref!` macros,
   return a status (`i32`) or a handle (`0` on failure) and call `set_last_error` on failure.
3. Use `#[repr(C)]` structs for anything larger than a few scalars.
4. Mirror it in `src/ThreeNet/Interop/NativeMethods.cs` (and `NativeTypes.cs` for structs).
5. If an existing signature or struct layout changes, bump `ABI_VERSION` in `lib.rs` and
   `ThreeNetRuntime.ExpectedAbiVersion`.
6. Cover it with a Rust test and a .NET test.

## Notes on wgpu 30

- `InstanceDescriptor::new_without_display_handle()`, `request_adapter` returns `Result`.
- `DeviceDescriptor` has `experimental_features` and `trace`; `SurfaceConfiguration` has `color_space`.
- Pipeline layouts take `&[Option<&BindGroupLayout>]` and `immediate_size`; vertex buffers are `Option`s.
- `Surface::get_current_texture()` returns `CurrentSurfaceTexture`; present with `queue.present(frame)`.
- WGSL: `operator` is a reserved word.
