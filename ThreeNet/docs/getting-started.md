# Getting started / Memulai

## Prerequisites / Kebutuhan

- .NET SDK 10 (`dotnet --version` ≥ 10.0)
- Rust toolchain 1.88+ (`rustup`, `cargo`)
- GPU drivers: DirectX 12 on Windows, Metal on macOS, Vulkan on Linux

## Build / Build

```bash
git clone https://github.com/DotNetVibeCoderz/Vibe_Graphics
cd Vibe_Graphics/ThreeNet
dotnet build ThreeNet.slnx
```

`src/ThreeNet.Native` runs `cargo build --release` for `rust/threenet-core` before compiling and copies the
native library to `runtimes/<rid>/native/`. Pass `-p:SkipRustBuild=true` to reuse an existing build and
`-p:RustProfile=debug` when working on the Rust code itself.

## Run / Menjalankan

```bash
dotnet run --project samples/ThreeNet.Samples.HelloCube   # native window, orbiting camera, click to pick
dotnet run --project apps/ThreeGallery                    # sample gallery
dotnet run --project apps/ThreeAppGen                     # AI app generator and Three.js converter
```

## Test / Pengujian

```bash
cargo test --manifest-path rust/threenet-core/Cargo.toml         # Rust unit + offscreen GPU tests
dotnet test tests/ThreeNet.Tests                                 # .NET binding and renderer tests
dotnet test tests/ThreeAppGen.Tests --filter "Category!=LLM"     # converter tests (includes real builds)
```

GPU tests skip themselves when no adapter is available.

![Hello Cube sample](images/hello-cube.png)

## Your first scene / Scene pertama

```csharp
using System.Numerics;
using ThreeNet;

ThreeNetRuntime.Initialize();                       // optional: verifies the native ABI, sets logging

using Scene scene = new()
{
    Environment = SceneEnvironment.Default with { Background = MathHelpers.FromHex(0x0B0D12), AmbientIntensity = 0.1f },
};

Material orange = scene.CreateMaterial(MaterialOptions.Pbr(Colors.Orange, metallic: 0.1f, roughness: 0.35f));
Node cube = scene.AddMesh(scene.CreateBoxGeometry(1.2f, 1.2f, 1.2f), orange, name: "cube");

Node sun = scene.AddLight(Light.Directional(Vector3.One, 3f));
sun.Position = new Vector3(4, 6, 4);
sun.LookAt(Vector3.Zero);

Node camera = scene.AddCamera(Camera.Perspective(55f.ToRadians()), new Vector3(0, 2, 6));
camera.LookAt(Vector3.Zero);

AppWindow window = new(WindowOptions.Default with
{
    Title = "First scene",
    Renderer = RendererOptions.Default with { MsaaSamples = 4, Bloom = true },
});

float time = 0;
window.Render += (renderer, delta) =>
{
    time += delta;
    cube.EulerAngles = new Vector3(time * 0.5f, time, 0);
    renderer.Render(scene, camera);
};
window.Input += (renderer, input) =>
{
    if (input.Kind == InputEventKind.KeyDown && input.Key == Key.Escape) Environment.Exit(0);
};
window.Run();
```

## Embedding in Avalonia / Di dalam Avalonia

```xml
<Window xmlns:tn="clr-namespace:ThreeNet.Avalonia;assembly=ThreeNet.Avalonia">
  <tn:ThreeNetView x:Name="Viewport" />
</Window>
```

```csharp
Viewport.Scene = scene;
Viewport.Camera = camera;
Viewport.Frame += (_, e) => cube.EulerAngles = new Vector3(0, (float)e.TotalSeconds, 0);
var orbit = new OrbitController(Viewport, camera) { AutoRotate = true, AutoRotateSpeed = 0.3f };
orbit.FrameBounds(scene.GetBounds());
```

## Troubleshooting

| Symptom | Fix |
|---|---|
| `DllNotFoundException: threenet_core` | Build `src/ThreeNet.Native` (or run `cargo build --release`), or set `THREENET_NATIVE_PATH`. |
| Crash while enumerating adapters on Windows | Vulkan loader issue; the default is DX12. Do not force `WGPU_BACKEND=vulkan`. |
| `ABI mismatch` from `ThreeNetRuntime.Initialize` | The native binary is older than the bindings: rebuild the Rust core. |
| Black window on a VM | No hardware adapter; use a machine with a GPU (WARP/software adapters are not requested). |
