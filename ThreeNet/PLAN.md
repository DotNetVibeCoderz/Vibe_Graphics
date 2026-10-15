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
| Modular shaders / custom shader injection (WGSL, GLSL/HLSL via naga) | ✅ | `user_vertex` / `user_surface` hooks in WGSL or GLSL (naga), 16 free parameters, a free texture slot, `sky_color` for reflections, CPU validation, hot reload. HLSL input is not offered by naga |
| Sky | ✅ | `SkyMode.Procedural` / `Texture`: one triangle at the far plane, so it is never fogged and never pollutes the depth prepass. Gradient, haze, sun disc, cloud sheet, moon and stars; callable from material shaders as `sky_color` |
| Additive blending | ✅ | `AlphaMode.Additive` for fire, sparks and glows |

## Phase 3 - Asset pipeline

| Item | Status | Notes |
|---|---|---|
| glTF / GLB loader (meshes, PBR materials, textures, cameras, punctual lights) | ✅ | |
| OBJ loader | ✅ | |
| PNG / JPEG / BMP / TGA / HDR textures | ✅ | |
| FBX loader | ✅ | Binary + ASCII, materials, textures, transform stack, skins, animation stacks |
| Keyframe / skeletal animation playback | ✅ | Step / linear / cubic spline clips, players with speed/weight/loop, CPU skinning; glTF + FBX + code |
| Morph targets / blend shapes | ✅ | glTF targets with their names and starting weights, `MorphTargetWeights` channels, targets built in code; deformed before skinning so the two stack, and skipped when the pose has not changed |
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
| Android / iOS native core | 🟡 | Android **verified on a real device** (Xiaomi M2012K11AG, Adreno 650, Vulkan): offscreen render + physics + HUD, and live rendering into a `SurfaceView` through `Renderer.CreateForAndroid` at ~48 fps with MSAA, shadows and bloom; also built for x64 and checked on the CI emulator. iOS static libraries built in CI and linked through buildTransitive targets, not device tested |
| WebAssembly build of the core (WebGPU) | 🟡 | `wasm32-unknown-emscripten` static library (WebGL2 via the GLES backend) built and packaged for `browser-wasm`; not browser tested yet |
| Consistent API across platforms | ✅ | Same C ABI everywhere |

## Phase 6 - Extensions

| Item | Status | Notes |
|---|---|---|
| Physics (Bullet / PhysX / Rapier) | ✅ | Rapier 0.35: bodies, colliders (incl. trimesh / convex), forces, raycasts, contact events, joints, character controller |
| Spatial audio | ✅ | cpal output + software mixer: panning, attenuation, Doppler, air absorption; WAV/OGG/MP3/FLAC via symphonia; offline rendering |
| VR/AR via OpenXR | 🟡 | Runtime / headset probe, off-axis cameras, `StereoRig`; headset sessions and swapchain submission still open |
| Networking / multiplayer sync | ✅ | `NetworkSession` (UDP): host relay, reliable ordered + unreliable messages, interpolated node replication, timeouts |
| Water, fire and VFX (`ThreeNet.Effects`) | ✅ | `WaterSimulation` / `WaterSurface` (wave equation height field, caustics by differential area, sky reflection, depth absorption), `FireEffect` (ray marched volume with simplex turbulence), `ParticleEffect` (emitter shapes, curl noise, attractors, collision, colour over life) |

## Phase 7 - Ecosystem & tooling

| Item | Status | Notes |
|---|---|---|
| ThreeGallery (Avalonia) | ✅ | 20 samples, live source, stats, screenshots |
| DemoGraphics (Avalonia) | ✅ | `samples/DemoGraphics`: 15 scenes across gallery / laboratory / benchmark / inspect / sandbox, shared weather, A/B compare, debug views, JSON+CSV benchmark export, reproducible captures, `--check` smoke test |
| ThreeAppGen - AI code editor (Jack - The Code Bender) | ✅ | Semantic Kernel, OpenAI/Azure, Claude, Gemini, Ollama, tools |
| Three.js → Three.Net converter (desktop / web / mobile) | ✅ | Static inventory + LLM + build validation + auto-fix |
| Scene editor / inspector | ✅ | `apps/ThreeEditor`: scene tree, viewport picking / dragging, live inspector (transform, geometry, material, light, camera, physics), undo/redo, play mode, JSON scenes (`SceneDocument`) |
| Plugin system | ✅ | `IThreeNetPlugin` + `PluginManager`: collectible load contexts, commands and importers, editor menu integration, example plugin (`samples/ThreePlugin.Sample`) |
| Documentation | ✅ | `docs/` |
| NuGet release (`ThreeNet`, `ThreeNet.Native`, `ThreeNet.Avalonia`) | ✅ | Published by CI when `VersionPrefix` changes |

## Phase 8 - Three.js parity / Kesetaraan fitur Three.js

Checked against the Three.js feature list in `threejs_fitur_lengkap.md` on 2026-10-14. Everything not listed
here is already covered by phases 1-7. The gaps are grouped by what they cost to close, not by how the list
orders them, because that is the useful question.

Diperiksa terhadap daftar fitur Three.js di `threejs_fitur_lengkap.md`. Yang tidak tercantum di sini sudah
tersedia di fase 1-7.

### A. Small additions - existing code, more of it

Done in 0.10.0.

| Item | Status | Notes |
|---|---|---|
| Geometry: circle, capsule, torus knot, tube, lathe, extrude, polyhedra | ✅ | `primitives` in `geometry.rs`, plus `Scene.Create*Geometry`. The tube is framed by parallel transport, not Frenet, so a straight run does not twist it; extrude triangulates by ear clipping, so concave outlines work; the polyhedron generator subdivides onto a sphere, so a detail 2 icosahedron is a geodesic sphere |
| Hemisphere light | ✅ | `LightType.Hemisphere`. The axis is the node local `+Y` and the ground colour rides in the cone and size slots a hemisphere light never reads |
| Toon, depth and normal materials | ✅ | `ShadingModel.Toon` / `.Depth` / `.Normal`, with `MaterialOptions.Toon`, `.DepthPreview`, `.NormalPreview`. The toon band count rides in shininess |
| Point and line materials | ✅ | `Effects.PointsAndLines`. WebGPU has no `gl_PointSize` and no line width, so both expand to quads - in the vertex shader, so a turning camera costs nothing. Lines dash by distance travelled |
| Texture formats: WebP, EXR, DDS, TIFF | ✅ | EXR takes the float path HDR already uses |
| Animation crossfading and additive blending | ✅ | `AnimationMixer` eases weights on a clock for cross fades; `AnimationPlayer.Additive` adds a clip measured against its own `ReferenceTime`, so a lean rides on a walk. Rotation composes by quaternion, scale by ratio |
| Audio analysis (FFT / spectrum / waveform) | ✅ | `AudioEngine.EnableAnalysis`, `Level`, `GetWaveform`, `GetSpectrum`. A Hann window and a radix-2 transform over a ring buffer, tapped after the master gain so it reports what a listener hears |
| Helper objects: axes, box, arrow, camera frustum, skeleton, grid | ✅ | `Helpers`, as line meshes through the normal pipeline |
| Camera controls: fly, first person, trackball | ✅ | `FlyController`, `FirstPersonController`, `TrackballController` beside `OrbitController`. The trackball keeps a full quaternion, so it rolls past the poles where a yaw and pitch pair gimbals |
| Pointer lock | ✅ | `AppWindow.PointerLocked` on the native window: winit takes the grab and hides the cursor, and raw movement arrives as `EventKind.RawMouseMove` with a delta and no position - which keeps coming after the cursor reaches the edge of the screen, the whole reason to lock. macOS and Wayland pin the cursor, Windows and X11 confine it; both deliver raw motion, so the code asks for the pin and falls back. A grab that fails leaves the flag off rather than lying, so the host keeps getting ordinary cursor events instead of waiting for raw ones. Still nothing for `ThreeNetView`: Avalonia exposes no lock, so the controllers there keep `LookWithoutDragging` |
| Drag controls (moving objects with the pointer) | 🟡 | ThreeEditor drags nodes in its viewport, but the gesture is not packaged as a controller anything else can use |

### B. Core renderer work


| Item | Status | Notes |
|---|---|---|
| Instanced rendering (`InstancedMesh`) | ✅ | `Node.SetInstances`. One instance buffer per node, grown but never shrunk, and a `vs_instanced` entry point beside `vs_main` in the same module - an entry point nothing calls places no demand on the pipeline layout. The transforms apply inside the node transform, so moving the node moves the set. The cost: instances share one material, one set of morph weights and one entry in the frustum test, against `Clone` which costs a draw each but culls individually |
| LOD | ✅ | `Node.SetLevelsOfDetail`. The level is picked before the bounds are taken, so culling and the shadow caster agree with what is drawn; a null level lets a node disappear entirely in the distance |
| Clipping planes | ✅ | `RendererOptions.ClipPlanes`, up to four world space planes. Clipped in the forward pass, the deferred G-buffer and the SSAO prepass - the prepass matters, or ambient occlusion and depth of field keep occluding against surfaces nothing draws |
| Render targets as a public API | ✅ | `Scene.CreateRenderTarget` + `Renderer.RenderToTexture`, with no CPU round trip - the result is a GPU texture the material bind groups reference directly. Mirrors, portals, monitors and minimaps work today. The target must match the renderer size: every pass before the post chain owns buffers sized to the renderer, and an arbitrary size would rebuild all of them twice a frame. Relaxing that wants a second target set per size |
| Cube map textures | ✅ | `Scene.CreateCubeTexture` from six faces. One environment slot takes either shape and the renderer binds it to the flat or the cube binding, with `misc.y` saying which - the unused one gets a 1x1 default, because a layout entry with nothing in it is a validation error rather than an empty slot |
| `CubeCamera` (dynamic six face capture) | ✅ | `Scene.CreateCubeRenderTarget` + `Renderer.RenderToCube(scene, position, target)`: six renders at 90 degrees into per face layer views, through a probe camera created and removed by the capture, so nothing in the scene is disturbed. A bake, not a per frame cost: the renderer has to be square and face sized, like every other render target |
| PBR extensions: clearcoat, sheen, anisotropy | ✅ | `MaterialOptions.Clearcoat`, `ClearcoatRoughness`, `Sheen`, `SheenRoughness`. The coat attenuates the layer underneath by its own Fresnel, since the energy it reflects has to come from somewhere; sheen uses the Charlie distribution, which peaks at grazing angles and is why velvet lights up around its edge. Anisotropy stretches the highlight along the grain, widening one axis by exactly as much as it narrows the other, so the GGX normalisation holds and a brushed surface does not read as merely rougher. Forward path only - the G-buffer has five targets and no room for a sixth, and there is a test pinning that |
| PBR extensions: transmission | ✅ | `MaterialOptions.Transmission`, `Ior`, `Thickness`. The forward pass splits in two with the colour copied aside between, so glass samples what the opaque half left; the split only happens when something actually refracts, and a test asserts an opaque scene is bit identical. Transmission replaces the diffuse rather than adding to it, since light that passed through did not scatter back. Forward path only, and glass does not refract other glass - Three.js has the same limitation |
| PBR extensions: iridescence | ✅ | `MaterialOptions.Iridescence`, `IridescenceIor`, `IridescenceThickness` (nanometres). The Belcour-Barla model KHR_materials_iridescence specifies, evaluated per fragment: the optical path difference through the film becomes a colour through a Gaussian fit of the CIE matching curves, and that colours the Fresnel term for both direct light and the environment. Forward path only, like the other extended lobes |
| Post: film grain, vignette, colour grading | ✅ | `RendererOptions.Vignette`, `VignetteSoftness`, `Grain`, `Saturation`, `Contrast`, `Brightness`, folded into the composite after tone mapping - they are what a lens and a film stock do, not what the scene does. A debug view switches them off, since it is meant to be read |
| Post: FXAA | ✅ | `RendererOptions.Fxaa`. The composite lands in an intermediate and FXAA copies it out, so nothing pays for a pass it is not using. This is the only antialiasing the deferred path can have: MSAA needs a multisampled target and the G-buffer is resolved before lighting |
| Post: SSR | ✅ | `RendererOptions.Ssr`, `SsrIntensity`, `SsrMaxDistance`, `SsrThickness`, `SsrSteps`. A fullscreen pass between the scene and the camera effects, marching the normal and linear depth prepass SSAO already writes - so turning it on turns that prepass on, in either path. Coarse march then four binary refinement steps. Weighted by Fresnel and the intensity only: the prepass carries geometry and not material parameters, so roughness does not blur the reflection. Fades at the frame edges, with distance and towards the camera, because a screen space ray has nothing to return once it leaves the frame |
| Post: LUT grading | ✅ | `RendererOptions.ColorLut` + `ColorLutStrength`, and `Scene.CreateNeutralColorLut(size)` for a table to start from. An ordinary texture laid out as a strip of `size` slices each `size` by `size`, so the shader reads the size off the texture height and there is nothing to keep in step by hand. Applied last, after tone mapping and the saturation and contrast, which is what a table exported from a grading tool was authored against |
| Post: SMAA | ✅ | `RendererOptions.Smaa`. Three passes: edges by luma with local contrast adaptation, blend weights from walking each edge run to its ends, then the blend. The coverage area is computed in the shader rather than read from the reference's AreaTex, which is a tabulation of that same arithmetic - inline removes its quantisation and leaves nothing that could be subtly wrong without a test noticing. SearchTex is likewise replaced by a plain walk. Diagonal patterns and corner rounding are not included; both are optional in the reference and both want tables of their own. A test measures the staircase rather than eyeballing it: the second difference of the edge crossing per column, which has to fall by a quarter, with a companion check that the image brightness does not move - smoothing that shifts the edge is a blur |
| Light probes | ✅ | `Renderer.BakeLightProbe(scene, position)` and `Scene.LightProbe`. Six renders projected onto three bands of spherical harmonics, nine coefficients a channel - enough to reconstruct the irradiance of any environment to about a percent, because a matte surface gathers over a cosine lobe too broad to hold anything sharper. It needed no compute: the projection is a CPU pass over six readbacks. The readback is eight bit, so anything brighter than white clips |
| Compute pipelines | ✅ | `Scene.CreateComputeKernel`, `Scene.CreateStorageBuffer`, `Renderer.Dispatch` and `Renderer.ReadStorageBuffer`, with `Renderer.SupportsCompute` for adapters that have none. Buffers bind in the order given at `@group(0) @binding(0)` upwards. Pipelines are cached by kernel, edit and binding count, because compiling WGSL per dispatch would cost more than most kernels do |
| GPU particles | ✅ | `Effects.GpuParticleEffect`. State in a storage buffer, stepped by a kernel that also writes the billboard quads straight into the geometry vertex buffer, so nothing crosses the bus. `Renderer.DispatchToGeometry` is the general form. What it gives up is visibility: the CPU does not know where any particle is, so there is no picking, no collision and the bounds are whatever `BoundsRadius` says |
| GPU skinning | ✅ | `Node.GpuSkinning`, per mesh. The bind pose and weights upload once and only the joint matrices travel each frame, against a whole vertex buffer per mesh on the CPU path. `Scene::joint_matrices` is shared by both paths, so the pose cannot drift between them. The cost is that the CPU no longer knows the pose: a raycast hits the bind pose and so do the bounds, which is why it is opt in and CPU skinning stays the default |
| Instance aware culling and picking | ✅ | Culling unions the bounds of every instance, so a forest does not vanish when the node origin leaves the frustum; raycasting tests each instance transform and reports `RayHit.InstanceIndex`, so picking a crowd says which copy was hit |
| Screen space refraction | ✅ | Shipped with transmission: the forward pass splits, the opaque colour is copied aside, and a refracting material samples it along the bent view ray. The offset comes from `Thickness` rather than from scene depth, because the post chain still does not hand a material the depth buffer |

### C. Asset formats

| Item | Status | Notes |
|---|---|---|
| STL, PLY | ✅ | `Scene.LoadStl` and `Scene.LoadPly`, both also reachable through `LoadModel`. STL reads binary and ASCII, told apart by the file length rather than by the leading word, because exporters write `solid` into the binary header too; a facet with a zero normal takes it from the winding. PLY reads ASCII and binary in either endianness, driven by the header, so properties come in whatever order and spelling the exporter chose and unknown elements are stepped over rather than ending the parse. A PLY with no faces loads as a point cloud |
| 3DS | ✅ | `Scene.Load3ds`. A chunk tree like VOX, so no parser dependency: step into the containers you know and over the ones you do not, which is what lets it survive files full of material and keyframe chunks. Z up becomes Y up, normals are computed because the format has none, and faces pointing past the vertex list are dropped rather than trusted |
| Collada (DAE) | ✅ | `Scene.LoadCollada`. The indirection is the job: a source of numbers, an accessor giving the stride, `<vertices>` aliasing the position source, and one index per input per corner. Corners agreeing on every index are welded, so a cube is eight vertices and not thirty six. Materials and the node hierarchy are not read |
| KMZ | ✅ | `Scene.LoadKmz`. A zip holding a Collada document, so unwrapping it is the whole difference; the model inside goes through the Collada loader |
| 3MF | ✅ | `Scene.Load3mf`. A zip holding XML that lists vertices and triangles directly, with none of Collada's indirection. Normals computed, since a printed solid carries none |
| VRML | ✅ | `Scene.LoadVrml`. Not XML, whatever its age suggests - its own curly brace syntax, so a tokeniser rather than a parser dependency. `IndexedFaceSet` only; the rest of the language is sensors and scripts, which want a browser |
| VOX | ✅ | `Scene.LoadVox`. Only the visible shell is built - a face with a voxel against it can never be seen, so a solid 4 cube is 96 faces rather than 384 - and the palette becomes a 256 by 1 texture that every voxel points at through its UV, so a model of any number of colours is one mesh with one material. Voxel space is Z up and the mesh is built Y up |
| USDZ / USDA | ✅ | `Scene.LoadUsd`. The `def Mesh` prims out of a text layer, which is what AR exporters write once a scene is flattened. Binary `.usdc` is **not** read - it is USD's own crystal format and guessing at it would give wrong geometry rather than an error, so a USDZ holding only `.usdc` says so and points at `usdcat` |
| IFC | ✅ | `Scene.LoadIfc`, the tessellated half: `IfcTriangulatedFaceSet` as IFC4 exporters write it, and the older `IfcPolyLoop` faceted form. The parametric half - swept solids, extrusions, booleans, the placement tree - is **not** read, because evaluating it is a geometry kernel. A file written entirely that way reports that and says to re-export tessellated, rather than importing empty and looking broken |
| SVG | ✅ | `Scene.LoadSvg(path, parent, depth)`, standing on the ear clipping extrude from group A. Paths, polygons and rects; curves flattened to segments because the triangulator works on polygons. Arcs are skipped rather than closed with a straight line through where the curve belongs, and a file of nothing but arcs says so instead of importing empty. SVG's y runs down the page, so outlines are flipped |
| Meshopt | ✅ | `Geometry.Optimize()` reorders for the post-transform cache and then compacts the vertex buffer to match, same triangles and same picture. `Geometry.Simplify(ratio, errorLimit)` collapses edges while holding the silhouette and returns a new mesh, which is how the levels for `Node.SetLevelsOfDetail` get made without modelling them. The ratio is a target, not a promise - a mesh with no redundancy comes back larger than asked for rather than destroyed, and the returned counts say what happened. Not covered: decoding `EXT_meshopt_compression` inside glTF, which is a separate job in the glTF reader rather than in this module |
| Draco | ✅ | `Scene.CreateGeometryFromDraco(bytes)`, on `draco-core` - which turned out to be a **pure Rust** decoder, so the C++ toolchain this row was parked behind was never needed. Decode only; nothing here writes Draco. The encoder is a dev dependency so the test can make a real buffer to decode, because tests that only reject bad input say nothing about whether decoding works |

### What is deliberately not planned

`WebGLRenderer`, `OffscreenCanvas`, node materials and TSL are Three.js's own web plumbing: the equivalents
here are wgpu, offscreen rendering and the WGSL / GLSL hooks. `ArrayCamera` is a WebXR multiview detail that
`StereoRig` already covers for the stereo case.

## Next up / Berikutnya

Open work, with why it is still open. Phase 8 above is the wider gap list against Three.js; these are the
items that were already in flight before it was written.

1. **OpenXR sessions and swapchain submission.** Needs Vulkan / D3D12 interop out of wgpu and a headset to
   verify against; the runtime probe and off-axis stereo cameras are in.
2. **Browser (WebGL2) and iOS on real targets.** Both are built and packaged by CI, but nothing here can run
   an iOS device or a browser build end to end, so neither is claimed as tested. (Android is now covered: it
   renders live on a real phone.)
3. **Bounds that follow a GPU skinned pose.** `Node.GpuSkinning` exists now, but the CPU keeps the bind
   pose, so bounds and raycasts are the bind pose too. Expanding the bind pose box by how far the joints
   travelled would fix culling without giving the pose back to the CPU.
4. **Contact hardening shadows.** Light probes are done; this is the half of that line that is left, and it wants a wider shadow filter driven by the distance between blocker and receiver.
5. **GPU particles.** `ParticleEffect` rewrites geometry on the CPU each frame, which is honest but caps out
   in the low thousands per system. The compute pipelines it was waiting on exist now, so this is a matter of
   writing the step kernel and feeding the sprite buffer from it.
6. **Screen space refraction for water.** Transmission gave the water surface everything it needs - the
   opaque colour is bound and a refracting material bends it - so this is now a matter of turning it on in
   `WaterSurface` and tuning it, rather than of missing machinery.
