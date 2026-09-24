# Three.Net Roadmap / Rencana Pengembangan

Status legend: ✅ done · 🟡 partial · ⬜ planned

Made by Gravicode Studios, led by Kang Fadhil.

## Phase 1 - Core foundation / Fondasi inti

| Item | Status | Notes |
|---|---|---|
| Rust core on `wgpu` (DX12 / Metal / Vulkan / GL) | ✅ | `rust/threenet-core`, wgpu 30. DX12 by default on Windows. |
| Basic rendering: mesh, camera, light | ✅ | Forward renderer, MSAA, frustum culling, per object dynamic uniforms |
| Minimal scene graph | ✅ | Arena with generational ids, parenting, cached world matrices |
| .NET binding (interop layer) | ✅ | C ABI + `LibraryImport`, blittable structs, `DisableRuntimeMarshalling` |

## Phase 2 - Rendering & materials / Rendering & material

| Item | Status | Notes |
|---|---|---|
| PBR metallic-roughness + Basic / Lambert / Phong | ✅ | One uber shader |
| Textures (base colour, normal, metallic-roughness, emissive, occlusion) | ✅ | GPU mipmap generation, samplers |
| HDR pipeline + tone mapping (ACES, Reinhard, Filmic) | ✅ | Rgba16Float target |
| Bloom | ✅ | Soft knee bright pass + separable blur |
| Fog, image based lighting (equirect) | ✅ | Exponential squared fog |
| SSAO | ✅ | Normal/depth prepass, half res hemisphere kernel, bilateral blur |
| Depth of field, motion blur | ✅ | Golden angle bokeh DoF, depth reprojection camera motion blur (HDR, both paths) |
| Deferred renderer | ✅ | `RenderPath.Deferred`: 5 target G-buffer + fullscreen lighting, up to 128 lights; transparents forward |
| Shadow maps | ✅ | Directional cascades (up to 4), spot, and point lights with cube maps (six faces); PCF, up to 16 layers allocated on demand, per node cast/receive |
| Modular shaders / custom shader injection (WGSL, GLSL/HLSL via naga) | ✅ | `user_vertex` / `user_surface` hooks in WGSL or GLSL (naga), CPU validation, hot reload. HLSL input is not offered by naga |

## Phase 3 - Asset pipeline

| Item | Status | Notes |
|---|---|---|
| glTF / GLB loader (meshes, PBR materials, textures, cameras, punctual lights) | ✅ | |
| OBJ loader | ✅ | |
| PNG / JPEG / BMP / TGA / HDR textures | ✅ | |
| FBX loader | ✅ | Binary + ASCII, materials, textures, transform stack, skins, animation stacks |
| Keyframe / skeletal animation playback | ✅ | Step / linear / cubic spline clips, players with speed/weight/loop, CPU skinning; glTF + FBX + code |
| Texture streaming, compression (KTX2/Basis), asset cache manager | ✅ | KTX2 (raw, BC, ETC2, ASTC, zstd/zlib) + Basis ETC1S/UASTC transcoded per GPU; async loads with placeholders and a per-frame budget; texture + model cache |

## Phase 4 - Interactivity

| Item | Status | Notes |
|---|---|---|
| Keyboard, mouse, touch input (native window) | ✅ | `AppWindow.Input` |
| Raycasting / picking | ✅ | BVH-less: AABB broad phase + Möller-Trumbore |
| Orbit camera controls | ✅ | `OrbitController` (Avalonia), `OrbitRig` (converted apps) |
| Gamepad | ✅ | `Gamepads` (gilrs: XInput/WGI, evdev, IOKit), sticks with radial dead zone, triggers, rumble, virtual pads |
| Event system (onClick / onHover / drag on nodes) | ✅ | `InteractionManager`: enter/leave/down/up/move/click/double click/drag with bubbling; wired into `ThreeNetView` |
| UI overlay | ✅ | Native HUD (`Scene.Overlay`): rounded panels, images, text (Inter, custom fonts), anchors, parents, hit testing, buttons |

## Phase 5 - Multiplatform runtime

| Item | Status | Notes |
|---|---|---|
| Windows | ✅ | Tested (DX12) |
| Linux, macOS | ✅ | Built and tested in CI (linux-x64, osx-arm64) plus osx-x64 cross build |
| Android / iOS native core | 🟡 | Android arm64/x64 built with the NDK (API 26), packaged and **verified on the CI emulator** (Vulkan/SwiftShader: render + physics + HUD); iOS static libraries built in CI and linked through buildTransitive targets, not device tested |
| WebAssembly build of the core (WebGPU) | 🟡 | `wasm32-unknown-emscripten` static library (WebGL2 via the GLES backend) built and packaged for `browser-wasm`; not browser tested yet |
| Consistent API across platforms | ✅ | Same C ABI everywhere |

## Phase 6 - Extensions

| Item | Status | Notes |
|---|---|---|
| Physics (Bullet / PhysX / Rapier) | ✅ | Rapier 0.35: bodies, colliders (incl. trimesh / convex), forces, raycasts, contact events, joints, character controller |
| Spatial audio | ✅ | cpal output + software mixer: panning, attenuation, Doppler, air absorption; WAV/OGG/MP3/FLAC via symphonia; offline rendering |
| VR/AR via OpenXR | 🟡 | Runtime / headset probe, off-axis cameras, `StereoRig`; headset sessions and swapchain submission still open |
| Networking / multiplayer sync | ✅ | `NetworkSession` (UDP): host relay, reliable ordered + unreliable messages, interpolated node replication, timeouts |

## Phase 7 - Ecosystem & tooling

| Item | Status | Notes |
|---|---|---|
| ThreeGallery (Avalonia) | ✅ | 20 samples, live source, stats, screenshots |
| ThreeAppGen - AI code editor (Jack - The Code Bender) | ✅ | Semantic Kernel, OpenAI/Azure, Claude, Gemini, Ollama, tools |
| Three.js → Three.Net converter (desktop / web / mobile) | ✅ | Static inventory + LLM + build validation + auto-fix |
| Scene editor / inspector | ✅ | `apps/ThreeEditor`: scene tree, viewport picking / dragging, live inspector (transform, geometry, material, light, camera, physics), undo/redo, play mode, JSON scenes (`SceneDocument`) |
| Plugin system | ✅ | `IThreeNetPlugin` + `PluginManager`: collectible load contexts, commands and importers, editor menu integration, example plugin (`samples/ThreePlugin.Sample`) |
| Documentation | ✅ | `docs/` |
| NuGet release (`ThreeNet`, `ThreeNet.Native`, `ThreeNet.Avalonia`) | ✅ | Published by CI when `VersionPrefix` changes |

## Next up / Berikutnya

Open work, with why it is still open:

1. **OpenXR sessions and swapchain submission.** Needs Vulkan / D3D12 interop out of wgpu and a headset to
   verify against; the runtime probe and off-axis stereo cameras are in.
2. **Browser (WebGL2) and iOS on real targets.** Both are built and packaged by CI, but nothing here can run
   an iOS device or a browser build end to end, so neither is claimed as tested.
3. **GPU skinning and instanced rendering.** CPU skinning is correct and keeps bounds and picking accurate;
   a GPU path would be a second code path (bounds and raycasts would still need the CPU pose), so it waits
   for a scene that actually needs the throughput.
4. **Contact hardening shadows and light probes.**
