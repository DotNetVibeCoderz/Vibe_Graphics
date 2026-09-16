# Progress / Catatan Progres

Development log for Three.Net. Newest first.
Catatan pengembangan Three.Net. Terbaru di atas.

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

- Only Windows (DX12) is verified on real hardware so far.
- Web and mobile heads compile, but the native core for WebAssembly and Android is not built yet (phase 5).
- Point lights do not cast shadows yet; skinning runs on the CPU (see PLAN.md).
