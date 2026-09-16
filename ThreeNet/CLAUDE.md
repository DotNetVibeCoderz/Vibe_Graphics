# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Three.Net: a native multiplatform 3D library for .NET 10 with a Rust (wgpu 30) rendering core behind a C ABI,
plus two Avalonia 12 apps. `requirements.md` (Indonesian) is the original spec; `PLAN.md` is the roadmap with
status and `Progress.md` the dated development log — update both when you finish meaningful work.

## Commands

```bash
dotnet build ThreeNet.slnx                                   # builds everything; ThreeNet.Native runs cargo build --release
dotnet build <proj> -p:SkipRustBuild=true                    # skip cargo when the native lib is already built
cargo build --release --manifest-path rust/threenet-core/Cargo.toml
cargo test  --manifest-path rust/threenet-core/Cargo.toml    # includes offscreen GPU render tests
cargo run   --manifest-path rust/threenet-core/Cargo.toml --example offscreen   # writes offscreen.png
cargo run --release --manifest-path rust/threenet-core/Cargo.toml --example shadows -- docs/images/gallery-shadows.png

dotnet test tests/ThreeNet.Tests                             # binding + renderer tests (GPU tests self-skip)
dotnet test tests/ThreeNet.Tests --filter "FullyQualifiedName~RendersALitSphereOffscreen"   # single test
dotnet test tests/ThreeAppGen.Tests --filter "Category!=LLM&Category!=Slow"   # fast converter tests
dotnet test tests/ThreeAppGen.Tests --filter "Category=Slow"  # real dotnet builds of converted desktop/web/android projects (minutes)
THREENET_LLM_KEYFILE=<key file> dotnet test tests/ThreeAppGen.Tests --filter "Category=LLM"   # real LLM conversion

dotnet run --project samples/ThreeNet.Samples.HelloCube
dotnet run --project apps/ThreeGallery
dotnet run --project apps/ThreeAppGen            # add `-- --convert <folder>` to open the Three.js converter page
dotnet run --project samples/ThreeNet.Samples.MotoCross
dotnet run --project apps/HomeComplexCad         # add `-- --location "Ruang Tamu" --group Dahlia --hour 20 --mode fp`
```

## Architecture (big picture)

- `rust/threenet-core`: scene graph as generational arenas (`scene.rs`), forward renderer with HDR target,
  shadow maps (`renderer/shadows.rs`, cascades + spot), SSAO (`renderer/ssao.rs`), bloom + tone mapping
  (`renderer/`), uber WGSL shader (`shaders/`), glTF/OBJ loaders, raycast, winit host
  (`window.rs`), and `ffi.rs` — the only public surface. Ids are 1-based `u32` (0 = null); failures return a
  negative status and set a thread-local message.
- `src/ThreeNet`: `Interop/NativeMethods.cs` mirrors `ffi.rs` 1:1 with `LibraryImport`; `Interop/NativeTypes.cs`
  mirrors every `#[repr(C)]` struct (field order must match exactly). The assembly uses
  `DisableRuntimeMarshalling`, so only blittable types may cross. Public API wraps handles
  (`Node`, `Geometry`, `Material`, `Texture` are classes holding `(Scene, Id)`).
- **Changing the ABI**: edit `ffi.rs` + `NativeTypes.cs`/`NativeMethods.cs` together, bump `ABI_VERSION` (lib.rs)
  and `ThreeNetRuntime.ExpectedAbiVersion`, rebuild the Rust core (a stale DLL causes silent struct mismatches).
- `src/ThreeNet.Native`: MSBuild packaging; copies `rust/target/release/threenet_core.*` to
  `runtimes/<rid>/native`. `NativeLibraryResolver` also probes the repo's `rust/target`.
- `src/ThreeNet.Avalonia`: `ThreeNetView` renders offscreen (BGRA) and blits to a `WriteableBitmap`;
  `OrbitController`.
- `samples/ThreeNet.Samples.MotoCross` and `apps/HomeComplexCad`: larger showcase apps; Rodin generated GLBs live
  in their `Assets/` folders and are instanced with `Node.Clone`. The renderer keeps at most 64 enabled lights.
- `apps/ThreeGallery`: each `Samples/*.cs` is embedded and shown as live source; register new samples in `SampleCatalog`.
- `apps/ThreeAppGen`: Semantic Kernel (`Services/KernelFactory.cs` picks OpenAI / Azure OpenAI / Claude via
  Anthropic.SDK / Gemini / Ollama and builds provider-specific execution settings), `ChatService` (Jack, tools in
  `Plugins/`), `ProjectService` (templates, dotnet CLI), settings in `app.config` via `AppSettings`.
- `apps/ThreeAppGen/Services/Conversion`: Three.js converter. Pipeline in `ThreeJsConverter`: analyzer →
  `SceneInventoryExtractor` (regex-based static IR) → `ConvertedProjectScaffolder` (Core + host projects) →
  `BaselineCodeGenerator` (always compiles) → `LlmCodeConverter` (strict contract prompt, `<file>` blocks) →
  `BuildValidator` + fix loop → fallback to baseline → `ConversionReport`. Hosts are fixed templates; only the
  `*.Core` project is LLM-editable; `Templates/*.template` files are embedded (`WithCulture=false`).

## Gotchas

- Windows defaults to the DX12 backend: enumerating Vulkan crashes on some machines. `WGPU_BACKEND` overrides.
- `AppWindow.Run` hops to an STA thread and winit runs with `any_thread`; keep that when touching `window.rs`.
- The renderer passed to host callbacks must stay boxed (stable address) — .NET keeps the pointer.
- Struct defaults: never write `Default => new()` together with a parameterless ctor that reads `Default`
  (infinite recursion); put values in the ctor.
- Child `dotnet build` processes launched with redirected output must disable build servers / node reuse
  or they never report completion.
- Avalonia 12: no `GetVisualRoot()` (use `TopLevel.GetTopLevel`), `PlaceholderText` not `Watermark`, don't define
  your own `InitializeComponent`, Android uses `AvaloniaAndroidApplication<TApp>` + non-generic `AvaloniaMainActivity`.
- PowerShell `Set-Content -Encoding utf8` writes a BOM, which breaks WGSL; write files with the editor tools.

## Conventions

- .NET 10, standard C# naming, file-scoped namespaces, nullable enabled; Rust edition 2024.
- README is bilingual (Indonesian + English); full docs live in `docs/`.
- Credit "Gravicode Studios, led by Kang Fadhil" in apps and docs.

## Credentials (external files, never copy them into the repo or commit them)

- NuGet publish API key: `C:\Users\mifma\Documents\CodeSandbox\PackageCredentials.txt`
- Real LLM test keys (Azure OpenAI, DeepSeek, Tavily): `C:\Users\mifma\Documents\CodeSandbox\testkey.txt`
  (read at runtime by the opt-in LLM test via `THREENET_LLM_KEYFILE`)
