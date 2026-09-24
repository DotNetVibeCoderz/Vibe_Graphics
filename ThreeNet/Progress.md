# Progress / Catatan Progres

Development log for Three.Net. Newest first.
Catatan pengembangan Three.Net. Terbaru di atas.

## 2026-09-24 - Point light shadows and an editor redesign (0.6.0)

- **Point lights cast shadows**: six cube faces per light, selected per pixel from the major axis of the
  light to fragment vector. Shadow layers are now allocated on demand (8 by default, up to 16) so a cube
  map costs memory only in scenes that use one. GPU tests in Rust and .NET check that a cube shadows a
  floor and that the shadow disappears when `CastShadow` is off; the ThreeGallery shadows sample gained a
  circling lantern.
- **ThreeEditor redesign** (`frontend-design`): warm graphite chrome that does not tint the viewport,
  axis colour (X red, Y green, Z blue) wherever an axis is actually meant - transform fields, the origin
  marker in the scene and the viewport readout - and one amber for "selected" or "running". Numbers are
  monospaced. New in the UI: hierarchy filter, per row visibility toggles, right click menu, collapsible
  inspector sections, geometry fields named for the shape, snapping to 0.25 m while dragging, render
  toggles on the command bar, and a viewport readout drawn with the engine's own HUD overlay.

## 2026-09-21 - Phase 7: scene editor and plugins / Fase 7: editor scene dan plugin (0.5.0)

- **Scene documents** (`ThreeNet.Scenes.SceneDocument`): a serialisable description of a scene - geometries
  (primitives or model files), textures, materials, a node tree with lights, cameras and physics, and
  environment settings. `Build(scene)` creates everything and reports missing assets instead of throwing;
  JSON save / load keeps asset paths relative to the file.
- **ThreeEditor** (`apps/ThreeEditor`): scene tree, viewport (picking and ground-plane dragging through
  `InteractionManager`, orbit camera, editor grid), live inspector for transform, geometry, material,
  light, camera and physics, undo / redo (100 steps), duplicate, reparent, model import, screenshots,
  and a play mode that steps physics and restores the scene on stop.
- **Plugin system** (`ThreeNet.Plugins`): `IThreeNetPlugin` implementations are discovered in a folder and
  loaded into collectible `AssemblyLoadContext`s; they register commands (shown in the editor's Plugins
  menu) and importers for their own file formats. Example: `samples/ThreePlugin.Sample` (scatter boxes,
  ring of lights, `.points` importer), built into the editor's `plugins` folder.
- New doc: `docs/editor.md`. Phase 7 is complete; every roadmap phase now has working code.

## 2026-09-17 - 0.4.1 / 0.4.2: Android verified on an emulator

- The 0.4.0 Android binaries needed `libc++_shared.so`, so the CI emulator run failed with
  `DllNotFoundException`. `rust/.cargo/config.toml` now links libc++ and libc++abi statically for Android
  targets; the core only depends on system libraries (libc, libm, libdl, libandroid, libaaudio).
- The CI emulator then ran the sample end to end: `THREENET_OK adapter: SwiftShader Device (Subzero)
  (Cpu, Vulkan) ABI 7 ... ball rests at y=0.60, draws 3, triangles 5292` - rendering, physics and the HUD
  all work on Android.
- 0.4.1 published `ThreeNet`, `ThreeNet.Native` (win-x64, linux-x64, osx-arm64, osx-x64, android-arm64,
  android-x64, ios-arm64, iossimulator-arm64, browser-wasm) and `ThreeNet.Avalonia`.
- 0.4.2: the Rust crate version follows the packages, so `ThreeNetRuntime.NativeVersion` no longer reports 0.1.0.

## 2026-09-17 - Phase 5: multiplatform runtime / Fase 5: runtime multiplatform (0.4.0)

- Platform services became Cargo features (`basis`, `audio-output`, `gamepad`, `xr`) with stubs, so the C ABI
  is identical on every target; winit is left out on Android and Emscripten (hosts own the surface).
- **Android**: arm64 and x64 cores built with the NDK (API 26, AAudio) and packaged as
  `runtimes/android-*/native`; `samples/ThreeNet.Samples.Android` renders PBR + physics + HUD offscreen.
  Local emulator runs were blocked (the machine has no WHPX), so CI runs the sample on an emulator.
- **iOS**: `aarch64-apple-ios` / `-ios-sim` static libraries built on macOS CI, linked through
  `buildTransitive/ThreeNet.Native.targets` (`NativeReference`), P/Invokes bound to the main program.
- **Browser**: `wasm32-unknown-emscripten` static library (WebGL2 through wgpu's GLES backend), linked with
  `NativeFileReference` for `browser-wasm`.
- **macOS x64** cross build. `NativeRuntimeInfo` reports every RID. New doc: `docs/platforms.md`.
- Tests: Rust 33 unit + 29 integration, .NET 47, converter 11 - all passing locally.

## 2026-09-17 - Phase 6: extensions / Fase 6: ekstensi

- **Physics** (`Scene.Physics`, Rapier 0.35): dynamic / fixed / kinematic bodies on nodes, box / sphere / capsule /
  cylinder / triangle mesh / convex hull colliders with friction, restitution, density, sensors and collision
  groups; impulses, forces, velocities, teleport; raycasts; contact events; fixed / ball / hinge / slider
  joints; kinematic character controller (slide, slopes, auto-step, ground snap); fixed timestep sync.
- **Spatial audio** (`AudioEngine`): cpal output, software mixer with equal power panning, inverse distance
  attenuation, Doppler, air absorption and smoothed gains; WAV / OGG / MP3 / FLAC decoding (symphonia);
  node attached sounds and camera listener; offline rendering.
- **Networking** (`ThreeNet.Networking.NetworkSession`): UDP host / clients with relay, reliable ordered and
  unreliable messages, heartbeats and timeouts, node transform replication with snapshot interpolation.
- **XR**: OpenXR runtime / headset probe (dynamic loader), off-axis camera projection, `StereoRig` with
  side-by-side rendering. Headset sessions are not implemented yet.
- ThreeGallery: "Physics & spatial audio" sample. New doc: `docs/extensions.md`. **ABI 7.**

## 2026-09-17 - Phase 4: interactivity / Fase 4: interaktivitas

- **Node events** (`InteractionManager`): pointer enter/leave/down/up/move, click, double click and drag
  (camera plane, ground plane, events only) with bubbling to ancestors and `Handled`; built on raycasting,
  wired into `ThreeNetView` (drags suppress `OrbitController`) and usable with `AppWindow.Input`.
- **Native HUD** (`Scene.Overlay`): rounded panels with borders, images, text with the embedded Inter font
  (SIL OFL) or loaded TTF/OTF, anchors, parents, layers, scale; hit testing and buttons that block picking.
  Rendered by the Rust core after tone mapping (glyph atlas, one vertex buffer per frame).
- **Gamepads** (`Gamepads`): gilrs backend, stable slots, pressed/released edges, radial dead zone,
  triggers, rumble, connect/disconnect events, virtual pads.
- Motocross: gamepad controls; Rodin generated pine and oak trees, boulders and tyre stacks
  (`Node.SetShadowsRecursive` keeps distant trees out of the shadow maps).
- ThreeGallery: "Interaction & HUD" sample. New doc: `docs/interactivity.md`. **ABI 6.**

## 2026-09-17 - Phase 2 & 3 complete / Fase 2 & 3 selesai (0.3.0)

- **Custom shaders**: the uber shader is now modular (`common`, `material`, `forward`, `deferred_gbuffer`);
  `Scene.CreateShader` takes `user_vertex` / `user_surface` hooks in WGSL or GLSL (translated with naga),
  validates them on the CPU and supports hot reload (`Shader.Update`). Materials gained `Shader`, `Custom0`,
  `Custom1`.
- **Deferred renderer** (`RenderPath.Deferred`): G-buffer + fullscreen lighting pass, transparents forward,
  128 lights in both paths.
- **Depth of field** (bokeh) and **camera motion blur** on the HDR image, forward and deferred.
- **Animation**: clips with step / linear / cubic spline channels, players (speed, weight, loop), CPU
  skinning; imported from glTF and FBX or built in code (`AnimationClip`, `Scene.UpdateAnimations`).
- **FBX importer**: binary and ASCII, materials, textures, transform stack, skins, animation stacks;
  `Scene.LoadModel` picks the loader by extension.
- **KTX2 / Basis Universal**: raw, BC1-7, ETC2, ASTC and zstd/zlib supercompression; ETC1S and UASTC
  transcoded to BC7 / ETC2 / ASTC depending on the GPU, CPU decode fallback.
- **Texture streaming** (`LoadTextureAsync`, `Texture.State`, `SetStreamingBudget`, `FinishStreaming`) and
  **asset cache** (`LoadTextureCached`, `LoadModelCached`, `AssetStats`, `ClearAssetCache`).
- ThreeGallery: 5 new samples (custom shaders, deferred 100 lights, depth of field & motion blur, keyframe &
  skeletal animation, texture streaming & KTX2) - 18 in total. New doc: `docs/advanced-rendering.md`.
- **ABI 5.** The native build now compiles the Basis Universal C++ transcoder (needs a C++ toolchain).
- CI: 0.2.0 never reached NuGet because the Windows runner's WARP driver crashes in the shadow/SSAO GPU
  tests (they pass on real GPUs and on local WARP). Those tests now run as a non-blocking step on Windows;
  `THREENET_FALLBACK_ADAPTER=1` forces the software adapter to reproduce such issues.
- Tests: Rust 22 unit + 23 integration (deferred, effects, animation, FBX, KTX2/Basis incl. GPU render,
  streaming/cache), .NET 30.

## 2026-09-16 - Sample apps / Aplikasi contoh

- **Motocross** (`samples/ThreeNet.Samples.MotoCross`): procedural circuit with jumps, whoops and berms, analytic
  terrain, arcade suspension physics, dust particles, synthesised engine audio (winmm), lap timing, mini map,
  day/night presets with floodlights and headlight. Rider and bike are a Rodin (Hyper3D) GLB.
- **Home Complex CAD Viewer** (`apps/HomeComplexCad`): "Griya Nusantara Residence" with 12 houses (3 furnished show
  units with kitchen, bathrooms, bedrooms, playroom, family room, private pool), shops, clubhouse pool, park,
  sports court, street lamps and text signs; first person / third person / fly cameras with floor and wall
  collision; location menu; continuous time of day; land & building information for the building in view.
  Furniture, cars, trees and lamps generated with Rodin and instanced with `Node.Clone`.
- Library: `Node.Clone` (`tn_node_clone`), `Node.Light` getter (`tn_node_get_light`); **ABI 3**.
- Rodin MCP server registered for the project (`rodin.mcp.json` stays local and is git ignored).

## 2026-09-16 - Phase 2: shadows & SSAO / Bayangan & SSAO

- **Shadow maps**: cascaded shadow maps for directional lights (1-4 cascades, sphere fit with texel snapping
  so shadows do not shimmer), one perspective map per spot light, 8 layers per frame in a `Depth32Float`
  texture array. PCF filtering with a comparison sampler (`ShadowSoftness` 0-3), depth + normal offset bias
  per light (`ShadowBias`, `ShadowNormalBias`, `ShadowStrength`), off screen casters are collected before
  frustum culling, and every mesh node has `CastShadow` / `ReceiveShadow`.
- **SSAO**: view normal + linear depth prepass, half resolution hemisphere kernel (up to 32 samples, tiled
  4x4 noise), depth aware separable blur. Applied to ambient and IBL, and to direct light scaled by
  `SsaoDirectStrength`.
- `RendererOptions`: `Shadows`, `ShadowMapSize`, `ShadowDistance`, `ShadowCascades`, `ShadowSoftness`,
  `Ssao`, `SsaoRadius`, `SsaoIntensity`, `SsaoBias`, `SsaoSamples`, `SsaoDirectStrength`;
  `FrameStats` gained `ShadowLayers` and `ShadowDrawCalls`. **ABI bumped to 2.**
- ThreeGallery: new "Shadows & SSAO" sample (sweeping sun, spot light, pillars).
- ThreeAppGen converter: `renderer.shadowMap.enabled`, `light.castShadow`, `mesh.castShadow` /
  `receiveShadow` now convert instead of being reported as unsupported.
- Tests: 14 Rust (4 new GPU tests for cascade fitting, directional/spot shadows and SSAO), 19 .NET
  binding, 11 fast converter tests.

## 2026-09-16 - First release / Rilis pertama (0.1.0)

- Published to NuGet: `ThreeNet`, `ThreeNet.Native` (win-x64, linux-x64, osx-arm64) and `ThreeNet.Avalonia` 0.1.0,
  with symbol packages. Source moved into `DotNetVibeCoderz/Vibe_Graphics/ThreeNet`.
- GitHub Actions (`.github/workflows/threenet.yml`): native core built and tested on Windows, Linux and macOS,
  .NET tests, multi-RID pack, publish with the `NUGET_API_KEY` secret (`--skip-duplicate`; bump `VersionPrefix`
  to release again).
- Screenshots added to the README and docs.
- Fixed: ThreeAppGen crashed when sending a chat message (transcript is now an `ObservableCollection`);
  the last project reopens with its main file in the editor; dark syntax highlighting palette in both apps.

## 2026-09-15 - Three.js converter / Konverter Three.js

- ThreeAppGen: **Tools > Convert Three.js project...** page (source folder, target Desktop/Web/Mobile,
  destination defaulting to `Documents/ThreeNet/<Name>`, progress bar, live log, open in ThreeAppGen /
  open folder / view report).
- Conversion pipeline: analyser → static scene inventory → solution scaffold (Core + hosts) → asset copy →
  deterministic baseline → LLM translation → `dotnet build` → auto-fix rounds with compiler diagnostics →
  fallback to baseline → `CONVERSION_REPORT.md`.
- `ThreeJsCompat` helpers generated into converted projects: Euler XYZ, sRGB colours, safe asset loading and
  real Three.js geometries (TorusKnot, Tetra/Octa/Icosahedron, Circle, Ring, Capsule, Lathe).
- Azure OpenAI support and reasoning model settings (no temperature, `max_completion_tokens`) in the kernel factory.
- Sample input `samples/threejs/crystal-garden` and `tests/ThreeAppGen.Tests` (analyser, extractor, parsers,
  end-to-end baseline builds for all targets, opt-in real LLM conversion).
- Verified: real Azure `gpt-5-mini` conversion of the sample compiled on the first try.

## 2026-09-13 - Applications / Aplikasi

- **ThreeAppGen**: VSCode-like layout (explorer, AvaloniaEdit editor with highlighting and toggleable line
  numbers, resizable/hideable chat panel with model picker, image attachments, Ctrl+Enter, clear thread,
  logs panel, status bar), menu and toolbar (New Blank/Template, Open project/file, Close, Go to line,
  Roslyn Format, Build, Run, Deploy, Exit), Settings dialog persisted in `app.config`.
- Jack - The Code Bender on Semantic Kernel 1.80 with OpenAI, Claude (Anthropic SDK), Gemini and Ollama, plus
  kernel functions: project files, build, NuGet, Tavily search, web scraping, math, date/time, system info,
  Three.Net API reference and templates.
- 8 project templates: blank, spinning cube, solar system, procedural terrain, runner game, particle lab,
  bouncing ball simulator, Avalonia product viewer.
- **ThreeGallery**: 12 samples with embedded live source, stats HUD, orbit controls, picking, screenshots.
- `ThreeNet.Avalonia`: `ThreeNetView` (offscreen GPU + BGRA readback) and `OrbitController`.

## 2026-09-13 - Library / Library

- Rust core: wgpu 30 forward renderer (PBR/Phong/Lambert/Basic uber shader, up to 64 lights, MSAA, frustum
  culling), HDR + bloom + ACES/Reinhard/Filmic, textures with GPU mipmaps, scene graph arena, primitives,
  glTF/OBJ loaders, raycasting, winit window host, C ABI (`ffi.rs`).
- .NET binding: `Scene`, `Node`, `Geometry`, `Material`, `Texture`, `Light`, `Camera`, `Renderer`,
  `AppWindow`, input, raycasting; native library resolver; ABI version check.
- Fixed: Vulkan loader crash on Windows (DX12 default, `WGPU_BACKEND` override), WGSL reserved word,
  dangling renderer pointer handed to .NET, STA/any-thread event loop for .NET hosts, recursive struct defaults.
- Tests: 9 Rust (including offscreen GPU render), 17 .NET.

## Known limitations / Keterbatasan

- GPU rendering is verified on Windows hardware; Linux and macOS run the test suites in CI.
- Android, iOS and browser cores are built and packaged but not yet run on real devices / browsers
  (the Android emulator run happens in CI).
- OpenXR headset sessions are not implemented (runtime probe and stereo cameras are).
- Point lights do not cast shadows yet; skinning runs on the CPU (see PLAN.md).
