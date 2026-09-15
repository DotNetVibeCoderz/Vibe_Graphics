# Three.js → Three.Net conversion

ThreeAppGen converts a Three.js web project (scripts plus assets) into a .NET 10 solution that renders with
Three.Net on the desktop, in the browser or on mobile.

*Bahasa Indonesia: ringkasan ada di bagian bawah halaman ini.*

![Converter page after a real LLM conversion](images/appgen-converter.png)

*A real run on `samples/threejs/crystal-garden` with gpt-5-mini: the first LLM output had 4 compiler errors,
the auto-fix loop repaired them in one round.*

![The converted project running as a desktop app](images/converted-crystal-garden.png)

*The converted Crystal Garden running natively: torus knot, glass pedestal, octahedron crystals created in a loop,
orbiting moons, tiled texture and fog, all translated from the JavaScript.*

## Using it

1. Open **Tools > Convert Three.js project...** or press **Convert Three.js** on the toolbar.
2. **Three.js project folder** - pick the folder with `index.html`, the scripts and the assets. The page shows
   what was found: scripts, entry script, assets, Three.js version and detected features.
3. **Target**
   - **Desktop**: native window app for Windows, Linux and macOS. Runs immediately.
   - **Web**: Avalonia UI with a browser (WebAssembly) head and a desktop preview head.
   - **Mobile**: Avalonia UI with an Android head and a desktop preview head.
4. **Project name and destination** - defaults to `Documents/ThreeNet/<ProjectName>`.
5. **Conversion** - use the LLM (recommended) and choose how many auto-fix rounds it gets.
6. Press **Convert**. The progress bar and the log show every stage. **Stop** cancels.
7. When it finishes: **Open in ThreeAppGen** (opens the project and `ConvertedScene.cs`), **Open folder**
   or **View report**.

The LLM provider is the one selected in **Settings** (OpenAI / Azure OpenAI, Claude, Gemini, Ollama). Without
a configured provider the static conversion still produces a compiling project.

## Pipeline

```
analyse -> extract inventory -> scaffold solution -> copy assets -> baseline C# -> build
        -> LLM translation -> build -> [fix with compiler errors -> build] x N -> fallback -> report
```

| Stage | What happens | Why |
|---|---|---|
| Analyse | Walks the folder, skips `node_modules`, `dist`, vendored `three.module.js`, `OrbitControls.js`, bundles over 400 KB. Finds the entry script from `index.html` (`<script src>` or an inline module), the Three.js version (package.json or the CDN import map) and the features used. | Only application code goes to the model, never vendored libraries. |
| Extract | A static pass reads the idioms nearly every scene uses: constructors, `position/rotation/scale.set`, `scene.add`/`group.add`, material literals, texture and model loaders, background, fog, tone mapping, bloom, `controls.target`, and per frame increments inside the animation loop. The result is a JSON **scene inventory**. | Deterministic facts (exact numbers, colours, hierarchy) ground the model and feed the baseline. |
| Scaffold | Writes a solution: `Name.Core` (converted scene + assets) plus fixed host projects. | Hosts never change, so the model only ever edits Core. |
| Assets | Copies textures, models, audio and data with their relative paths into `Core/Assets`, copied next to every host at build time. | Source paths stay valid. |
| Baseline | Generates `ConvertedScene.cs` from the inventory and compiles it. | A known good starting point, and the fallback if the LLM output cannot be fixed. |
| LLM translation | One request with a strict system prompt: output contract, Three.js → Three.Net mapping rules, the real Three.Net API reference, the inventory, the baseline and the sources. Large projects are first summarised module by module. | The model edits a compiling program instead of inventing one, and cannot call APIs that do not exist. |
| Validate + fix | Builds the startup project. Errors located in Core are sent back with the current files; the reply is written and rebuilt, up to N rounds. | Real compiler feedback instead of guessing. |
| Fallback | If it still fails, the model output is moved to `llm-attempt/` and the baseline is restored. | The delivered project always compiles. |
| Report | `CONVERSION_REPORT.md` and `README.md` (English + Indonesian). | Approximations, unsupported features and next steps are explicit. |

## Generated solution

```
Name/
  Name.slnx
  Directory.Build.props
  CONVERSION_REPORT.md
  README.md
  src/
    Name.Core/              converted scene (ConvertedScene.cs), ThreeJsCompat.cs, OrbitRig.cs, Assets/
    Name.Desktop/           desktop target: native window host
    Name.App/               web/mobile: shared Avalonia UI (ThreeNetView + OrbitController)
    Name.Preview/           web/mobile: desktop preview head (runs today)
    Name.Web/               web: Avalonia.Browser head (net10.0-browser)
    Name.Android/           mobile: Avalonia.Android head (net10.0-android)
```

`ConvertedScene` is the contract every host uses:

```csharp
public sealed partial class ConvertedScene : IDisposable
{
    public ConvertedScene(string assetsRoot);
    public Scene Scene { get; }
    public Node Camera { get; }
    public RendererOptions RendererOptions { get; }
    public bool UsesOrbitControls { get; }
    public Vector3 OrbitTarget { get; }
    public void Update(float deltaSeconds, double totalSeconds);  // animation loop body
    public void OnInput(InputEvent input);                        // event handlers
    public void Dispose();
}
```

Run the result with `dotnet run --project src/Name.Desktop` (desktop) or `dotnet run --project src/Name.Preview`
(web and mobile).

> The web and mobile heads compile when the matching workload is installed (`wasm-tools`, `android`).
> Running them on the device needs the Three.Net native core for WebAssembly / Android, which is part of
> roadmap phase 5. The preview head runs the same UI on the desktop today.

## Mapping reference

| Three.js | Three.Net |
|---|---|
| `new THREE.Scene()` | `new Scene()` |
| `Group`, `Object3D` | `Scene.CreateNode(parent, name)` |
| `Mesh(geometry, material)` | `Scene.AddMesh(geometry, material, parent, name)` |
| `BoxGeometry`, `SphereGeometry`, `PlaneGeometry`, `CylinderGeometry`, `ConeGeometry`, `TorusGeometry` | same primitives, same argument order |
| `TorusKnot`, `Icosahedron`, `Octahedron`, `Capsule`, `Circle`, `Ring` | approximated (noted in the report) |
| `MeshStandardMaterial`, `MeshPhysicalMaterial` | `MaterialOptions.Pbr` |
| `MeshPhongMaterial` / `MeshLambertMaterial` / `MeshBasicMaterial` | `Phong` / `Lambert` / `Basic` |
| hex colours (sRGB) | `ThreeJsCompat.Color` / `ColorRgb` (linearised) |
| `DirectionalLight`, `SpotLight` | light node + `LookAt(target)` |
| `PointLight`, `AmbientLight`, `HemisphereLight` | `Light.Point`, `Light.Ambient` |
| `PerspectiveCamera(fov°, aspect, near, far)` | `Camera.Perspective(DegToRad(fov), near, far)` |
| `rotation` (Euler XYZ) | `ThreeJsCompat.EulerXyz` |
| `requestAnimationFrame` / `setAnimationLoop` | `Update(deltaSeconds, totalSeconds)`; frame increments × 60 |
| `TextureLoader`, `GLTFLoader`, `OBJLoader` | `ThreeJsCompat.TryLoadTexture` / `TryLoadModel` |
| `OrbitControls` | host orbit controls (`OrbitRig`, `OrbitController`) |
| `UnrealBloomPass`, `toneMapping`, `toneMappingExposure` | `RendererOptions.Bloom`, `ToneMapping`, `Exposure` |
| `scene.fog`, `scene.background` | `SceneEnvironment` fog and background |
| `Raycaster` | `Scene.CreateCameraRay` + `Scene.Raycast` |

Not available yet (reported with advice): `ShaderMaterial`, shadows, skeletal animation (`AnimationMixer`),
points/lines/sprites helpers, CSS renderers, audio, WebXR, physics engines, GUI panels.

## Testing the converter

```bash
# fast unit tests (analyser, extractor, parsers)
dotnet test tests/ThreeAppGen.Tests --filter "Category!=Slow&Category!=LLM"

# end-to-end baseline conversions (desktop, web, mobile) with real builds
dotnet test tests/ThreeAppGen.Tests --filter "Category=Slow"

# real LLM conversion: point THREENET_LLM_KEYFILE at a local key file (never commit it)
THREENET_LLM_KEYFILE=/path/to/keys.txt dotnet test tests/ThreeAppGen.Tests --filter "Category=LLM"
```

The sample input is `samples/threejs/crystal-garden`.

---

## Ringkasan (Bahasa Indonesia)

- Buka **Tools > Convert Three.js project...**, pilih folder project Three.js, pilih target
  (Desktop / Web / Mobile), nama project dan folder tujuan (default `Documents/ThreeNet/<NamaProject>`),
  lalu klik **Convert**.
- Progres dan log tampil selama proses. Setelah selesai tersedia **Open in ThreeAppGen**, **Open folder**
  dan **View report**.
- Tahapan: analisis folder → inventori scene statis → scaffold solusi → salin aset → kode baseline yang pasti
  compile → terjemahan oleh LLM (dengan kontrak API ketat dan referensi API asli) → compile → perbaikan
  otomatis berdasarkan error compiler sebanyak N putaran → jika tetap gagal kembali ke baseline → laporan.
- Hasil selalu dapat di-compile. Detail pendekatan dan fitur yang belum didukung ada di
  `CONVERSION_REPORT.md`.
