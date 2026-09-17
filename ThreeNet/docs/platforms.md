# Platforms / Platform

Three.Net ships one C ABI on every platform; `ThreeNet.Native` carries a binary per runtime identifier.
Features that need platform services compile to stubs where they are unavailable, so the same managed
code runs everywhere and reports "not included in this build" instead of failing to load.

| Runtime | Binary | Graphics | Build | Status |
|---|---|---|---|---|
| `win-x64` | `threenet_core.dll` | DX12 (Vulkan via `WGPU_BACKEND`) | CI + local | tested (GPU tests on real hardware; CI uses WARP) |
| `linux-x64` | `libthreenet_core.so` | Vulkan / GLES | CI | built and tested in CI (GPU tests skip without an adapter) |
| `osx-arm64` | `libthreenet_core.dylib` | Metal | CI | built and tested in CI |
| `osx-x64` | `libthreenet_core.dylib` | Metal | CI (cross) | built |
| `android-arm64`, `android-x64` | `libthreenet_core.so` (API 26+) | Vulkan / GLES | CI + local (NDK) | built; emulator smoke test in CI |
| `ios-arm64`, `iossimulator-arm64` | `libthreenet_core.a` (static) | Metal | CI | built, not device tested |
| `browser-wasm` | `libthreenet_core.a` (Emscripten, static) | WebGL2 (GLES backend) | CI + local | built, not browser tested |

## Build features

| Cargo feature | Default | What it adds | Left out on |
|---|---|---|---|
| `basis` | yes | Basis Universal transcoding (C++) | browser |
| `audio-output` | yes | cpal audio device (the mixer and decoders are always built) | browser |
| `gamepad` | yes | physical gamepads via gilrs (virtual pads always work) | browser |
| `xr` | yes | OpenXR runtime discovery | browser |
| `gltf-lights` | yes | KHR_lights_punctual import | - |

Native windows (`AppWindow`, winit) are not built for Android and the browser: those hosts own the surface,
so render with `Renderer.CreateOffscreen` (Avalonia's `ThreeNetView` already does).

## Android

```bash
cargo install cargo-ndk
export ANDROID_NDK_HOME=<ndk>                     # NDK r26+; API 26 is required for AAudio
cargo ndk -t arm64-v8a -t x86_64 --platform 26 build --release --manifest-path rust/threenet-core/Cargo.toml
```

With the NuGet package, `runtimes/android-*/native/libthreenet_core.so` is picked up automatically. From source,
add the libraries as `AndroidNativeLibrary` items like `samples/ThreeNet.Samples.Android`, which renders a scene
offscreen (PBR, physics, HUD) and shows it in an `ImageView`; it logs `THREENET_OK` with the adapter name.

## iOS

The package's `buildTransitive` targets add `libthreenet_core.a` as a `NativeReference` (force loaded, with
`-lc++`), and the managed resolver binds P/Invokes to the main program. The binaries are built in CI on macOS
(`cargo rustc --crate-type staticlib --target aarch64-apple-ios`).

## Browser (WebAssembly)

```bash
cargo rustc --lib --release --manifest-path rust/threenet-core/Cargo.toml --target wasm32-unknown-emscripten \
  --no-default-features --features gltf-lights --crate-type staticlib
```

`buildTransitive` adds the static library as a `NativeFileReference` for `browser-wasm` apps (requires the
`wasm-tools` workload). wgpu uses its GLES backend on Emscripten (WebGL2).

---

Three.Net - Gravicode Studios, led by Kang Fadhil.
