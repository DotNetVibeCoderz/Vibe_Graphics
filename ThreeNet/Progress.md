# Progress / Catatan Progres

Development log for Three.Net. Newest first.
Catatan pengembangan Three.Net. Terbaru di atas.

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
- No shadows, SSAO, skeletal animation or custom shaders yet (see PLAN.md).
