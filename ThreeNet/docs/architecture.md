# Architecture / Arsitektur

```
┌────────────────────────────── .NET 10 ──────────────────────────────┐
│ ThreeGallery · ThreeAppGen (Semantic Kernel, converter) · your app  │
│ ThreeNet.Avalonia   ThreeNetView (offscreen + BGRA blit), orbit     │
│ ThreeNet            Scene · Node · Geometry · Material · Texture    │
│                     Light · Camera · Renderer · AppWindow · Raycast │
│ ThreeNet.Interop    LibraryImport, blittable structs, resolver      │
├──────────────────────────── C ABI (tn_*) ───────────────────────────┤
│ threenet-core (Rust)                                                │
│   ffi.rs      handles, status codes, thread local last error        │
│   scene.rs    generational arenas: nodes, geometry, material, tex   │
│   renderer/   device, pipeline cache, GPU resource cache, post      │
│   loaders.rs  glTF/GLB, OBJ        raycast.rs   picking             │
│   window.rs   winit host with callbacks into .NET                   │
│ wgpu 30  →  DX12 · Metal · Vulkan · GL                              │
└─────────────────────────────────────────────────────────────────────┘
```

## Ownership and handles

- `Scene` and `Renderer` are heap objects owned by the managed wrappers (`tn_scene_create` /
  `tn_scene_destroy`). Wrappers are `IDisposable` with a finaliser as a safety net.
- Everything inside a scene (nodes, geometries, materials, textures) is addressed by a 1-based `u32` id into a
  generational arena. `0` is the null handle. Managed `Node`, `Geometry`, `Material` and `Texture` are thin
  classes holding `(Scene, Id)`; equality is by id, so handles are free to create.
- The native side validates every pointer and id: invalid input returns a negative status and sets a thread
  local message, which `NativeError.Check` turns into `ThreeNetException`.
- `tn_abi_version` guards against a managed build running with an older native binary.

## Interop rules

- All structs crossing the boundary are `#[repr(C)]` / `StructLayout.Sequential` and blittable. The assembly
  disables runtime marshalling, so calls are a direct transition with no marshalling stubs.
- Bulk data (vertices, indices, pixels) is passed as pointer + length and copied once on the native side.
- Callbacks from the window loop use `[UnmanagedCallersOnly]` function pointers and a `GCHandle` as user data.
  Exceptions are caught in the callback; they never unwind into Rust.
- The renderer handed to `on_init` lives in a `Box`, so its address is stable for the lifetime of the loop.

## Rendering pipeline

1. `update_world_transforms` refreshes dirty world matrices (iterative DFS, dirty flags propagate).
2. Lights are gathered (up to 64) into a uniform array; draw items are gathered, frustum culled against
   transformed AABBs, and sorted: opaque front to back, transparent back to front, `RenderOrder` first.
3. GPU resources are ensured lazily by version: geometry buffers are rewritten in place when the size is
   unchanged, textures get GPU generated mip chains, material bind groups are rebuilt only when a material or
   one of its textures changes.
4. Per object matrices go into one dynamic-offset uniform buffer (grown geometrically).
5. Forward pass into an `Rgba16Float` HDR target (with MSAA resolve), one uber shader for Basic / Lambert /
   Phong / PBR (GGX, Smith, Schlick), fog and equirectangular IBL.
6. Post: soft knee bright pass → two separable blur iterations at half resolution → composite with exposure and
   tone mapping (ACES, Reinhard, Filmic) into the swap chain (sRGB) or an offscreen texture.
7. Offscreen renderers can read pixels back as RGBA or BGRA.

Pipelines are cached by (topology, cull mode, blend, depth write/test, wireframe, samples, format).

## Threads and windows

- The native window loop (`AppWindow.Run`) must own its thread. On Windows it needs a single threaded apartment
  (winit's OLE drag and drop), so the managed side runs it on a dedicated STA thread and winit is allowed to
  run off the main thread.
- `Scene` and `Renderer` are not thread safe; use them from the thread that renders.
- Avalonia hosting renders offscreen on the UI thread and copies into a `WriteableBitmap`. This costs one
  readback per frame but composes with the rest of the visual tree on every platform.

## Backends

`wgpu::Backends` default to DX12 on Windows and to the primary backends elsewhere. `WGPU_BACKEND`
(`dx12`, `vulkan`, `metal`, `gl`) overrides the choice.

## Applications

- **ThreeGallery** loads each sample's own source as an embedded resource, so the code panel always shows the
  code that is running.
- **ThreeAppGen** is Avalonia + AvaloniaEdit + Semantic Kernel. Services: `AppSettings` (app.config),
  `KernelFactory` (providers, execution settings), `ChatService` (streaming, tools), `ProjectService`
  (templates, dotnet CLI), `Conversion/*` (Three.js converter). See [threeappgen.md](threeappgen.md).
