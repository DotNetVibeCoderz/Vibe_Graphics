# API reference

Namespace `ThreeNet` unless noted. Angles are radians, colours are linear RGBA `Vector4` (use
`MathHelpers.FromHex(0xRRGGBB)` for sRGB hex values), the world is right handed with +Y up and cameras and
lights face -Z.

## ThreeNetRuntime

| Member | Description |
|---|---|
| `Initialize(LogLevel = Warning)` | Loads the native core, checks the ABI, configures logging |
| `NativeVersion`, `NativeAbiVersion`, `ExpectedAbiVersion` | Version information |

## Scene : IDisposable

| Member | Description |
|---|---|
| `Root`, `NodeCount`, `ActiveCamera`, `Environment` | Graph root, statistics, default camera, background/ambient/fog/IBL |
| `CreateNode(parent?, name?)`, `Remove(node)`, `FindByName(name)`, `GetBounds(node?)` | Graph operations |
| `AddMesh(geometry, material, parent?, name?)` | Node with a mesh |
| `AddLight(light, parent?, name?)` | Node with a light |
| `AddCamera(camera, position, parent?, makeActive = true)` | Node with a camera |
| `CreateGeometry(vertices, indices, topology)` | Custom geometry (`ThreeNet.Interop.Vertex`) |
| `CreatePlaneGeometry`, `CreateBoxGeometry`, `CreateSphereGeometry`, `CreateCylinderGeometry`, `CreateConeGeometry`, `CreateTorusGeometry`, `CreateGridGeometry` | Primitives |
| `CreateMaterial(in MaterialOptions)`, `CreateMaterial(color, metallic, roughness)` | Materials |
| `LoadTexture(path, srgb)`, `LoadTexture(bytes, srgb)`, `CreateTexture(width, height, pixels, format)` | Textures |
| `LoadTextureAsync(path, srgb)`, `LoadTextureCached(path, srgb)`, `PollStreaming()`, `FinishStreaming(timeout)`, `SetStreamingBudget(n)`, `AssetStats`, `ClearAssetCache()` | Streaming and cache |
| `LoadGltf(path or bytes, parent?)`, `LoadFbx(path or bytes, parent?)`, `LoadObj(path, parent?)`, `LoadStl(path, parent?)`, `LoadPly(path, parent?)`, `LoadModel(path, parent?)`, `LoadModelCached(path, parent?)` | Asset import, returns `ImportResult` (`AnimationCount`, `SkinCount` included). `LoadModel` picks the loader from the extension: .gltf, .glb, .fbx, .obj, .stl, .ply |
| `CreateShader(source, language, name?)` | Custom shader hooks (WGSL / GLSL) |
| `Animations`, `CreateAnimation(name)`, `UpdateAnimations(delta)` | Animation clips |
| `Raycast(ray, options?, maxHits)`, `CreateCameraRay(camera, ndcX, ndcY, aspect)` | Picking |

## Node

`Name`, `Position`, `Rotation` (Quaternion), `EulerAngles` (YXZ), `Scale`, `Visible` (set), `Layers` (set),
`Tag`, `Light` (set), `Camera`, `Parent`, `Children`, `WorldMatrix`, `WorldPosition`,
`CreateChild`, `AttachMesh`, `DetachMesh`, `SetTransform`, `LookAt(target, up?)`, `Translate`, `Rotate`, `Remove`,
`Geometry`, `Material`, `MorphWeightCount`, `SetMorphWeight(i, w)`, `SetMorphWeights(weights)`, `GetMorphWeights()`.

## Instancing and lights

- `Node.Clone(parent)` copies a node and its subtree; the copy shares geometry, materials and textures, so
  import a glTF once and clone it for every placement.
- `Node.SetInstances(transforms)` / `SetInstances(positions, scale)`, `ClearInstances()`, `InstanceCount` draw
  one mesh many times from a single call; the transforms apply inside the node transform.
- `Node.SetLevelsOfDetail((geometry, distance), ...)`, `ClearLevelsOfDetail()`, `LevelOfDetailCount` swap the
  mesh by camera distance; a null geometry draws nothing, so a node can disappear in the distance.
- `Node.Light` has a getter: `node.Light = node.Light is { } l ? l with { Enabled = false } : null;`

## Resources

- `Geometry`: `Counts`, `Update(vertices, indices)`, `ComputeNormals()`, `ComputeTangents()`,
  `MorphTargetCount`, `MorphTargetNames`, `GetMorphTargetName(i)`, `AddMorphTarget(name, positionDeltas, normalDeltas?)`, `Destroy()`.
- `Material`: `Options` (get/set), `Update(Func<MaterialOptions, MaterialOptions>)`, `BaseColor`, `Destroy()`.
- `Scene.CreateRenderTarget(width, height)` makes a texture the renderer draws into.
- `Scene.CreateCubeTexture(size, +X, -X, +Y, -Y, +Z, -Z)` makes a cube map. Set as
  `SceneEnvironment.EnvironmentMap` it is sampled by direction, which is even everywhere; an
  equirectangular image crowds its samples at the poles.
- `Scene.CreateCubeRenderTarget(size)` makes an empty one for `Renderer.RenderToCube` to fill.
- `Scene.CreateNeutralColorLut(size)` makes a colour lookup table that changes nothing, as the starting
  point for a look. The layout is a strip: `size` slices of `size` by `size`, red across a slice, green
  down it, blue choosing the slice. Set it as `RendererOptions.ColorLut`.
- `Texture`: `SetSampler(wrapU, wrapV, linearFilter, mipmaps, anisotropy)`, `Update(pixels)` (bytes or floats,
  written into the existing GPU texture), `State` (`TextureState`), `LoadError`, `Destroy()`.
- `Shader`: `Name`, `CompiledWgsl`, `Update(source, language)`, `Destroy()`.
- `AnimationClip`: `Name`, `Duration`, `AddTranslation`, `AddRotation`, `AddScale`, `AddChannel(node, path, interpolation, times, values)`, `Play(loop)`, `Destroy()`.
- `AnimationPlayer`: `Time`, `Speed`, `Weight`, `Loop`, `IsPlaying`, `Additive`, `ReferenceTime`, `Stop()`.
- `AnimationMixer`: `FadeTo`, `FadeIn`, `FadeOut`, `CrossFade`, `Update(dt)`, `Active`, `Clear` - eases blend
  weights on a clock, which is all a cross fade is.
- `Helpers`: `AddAxes`, `AddBox`, `AddArrow`, `AddCameraFrustum`, `AddSkeleton`, `AddGrid` - line meshes that
  show what a scene is doing rather than what it looks like.
- `MaterialOptions` (struct): `Shading`, `AlphaMode`, `CullMode`, `BaseColor`, `Emissive`, `EmissiveIntensity`,
  `Metallic`, `Roughness`, `Specular`, `Shininess`, `Reflectance`, `Clearcoat`, `ClearcoatRoughness`,
  `Sheen`, `SheenRoughness`, `Anisotropy`, `AnisotropyRotation`, `Transmission`, `Ior`, `Thickness`,
  `Iridescence`, `IridescenceIor`, `IridescenceThickness`
  (those twelve are forward path only),
  `NormalScale`, `OcclusionStrength`,
  `AlphaCutoff`, `UvScale`, `UvOffset`, `DepthWrite`, `DepthTest`, `Wireframe`, `RenderOrder`, `BaseColorMap`,
  `NormalMap`, `MetallicRoughnessMap`, `EmissiveMap`, `OcclusionMap`, `CustomMap`, `Shader`,
  `Custom0`..`Custom3`; factories `Pbr`, `Basic`, `Phong`, `Lambert`. `AlphaMode.Additive` adds the fragment
  to the frame instead of blending over it.

## Lighting and cameras

- `LightProbe`: `Coefficients` (nine `Vector3`), `Irradiance(normal)`, `Uniform(radiance)`, `Scaled(factor)`,
  `IsDark`. Bake one with `Renderer.BakeLightProbe` and set it as `Scene.LightProbe`, with
  `Scene.LightProbeIntensity` to scale it. It adds to the constant ambient rather than replacing it, so a
  surface facing the window is lit and the side away from it is not.

- `Light` (struct): `Type`, `Color`, `Intensity`, `Range`, `InnerConeAngle`, `OuterConeAngle`, `Size`,
  `CastShadow`, `Enabled`, `ShadowBias`, `ShadowNormalBias`, `ShadowStrength`;
  factories `Directional`, `Point`, `Spot`, `Ambient`.
- `Camera` (struct): `Perspective(fov, near, far)`, `Orthographic(height, near, far)`, `AspectRatio` (0 = auto).
- `SceneEnvironment` (struct): `Background`, `AmbientColor`, `AmbientIntensity`, `FogColor`, `FogDensity`,
  `FogStart`, `FogEnd`, `EnvironmentMap`, `EnvironmentIntensity`, and the sky: `Sky` (`SkyMode.Color`,
  `Texture`, `Procedural`), `SunDirection`, `SkyIntensity`, `SkyHaze`, `SkyClouds`, `SkyRotation`.

## Renderer : IDisposable

| Member | Description |
|---|---|
| `CreateOffscreen(options)`, `CreateForWin32(hwnd, options)`, `CreateForX11(window, display, screen, options)`, `CreateForAppKit(nsView, options)`, `CreateForAndroid(nativeWindow, options)` | Factories |
| `Render(scene, camera?)` | Draws a frame |
| `RenderToTexture(scene, camera?, target)` | Draws into a texture a material can sample; the target must match the renderer size |
| `RenderToCube(scene, position, target)` | Captures the surroundings of a point into a cube render target: six frames at 90 degrees, through a camera the capture creates and removes itself |
| `BakeLightProbe(scene, position)` | The same six frames projected onto spherical harmonics, giving a `LightProbe` of directional ambient light. Offscreen and square renderers only |
| `Resize(width, height)`, `Options`, `Width`, `Height`, `AspectRatio` | Target |
| `ReadPixels()`, `ReadPixels(Span<byte>)`, `PixelBufferSize` | Offscreen readback |
| `Stats` (`FrameStats`), `AdapterName`, `IsOffscreen` | Diagnostics |

`RendererOptions`: `Width`, `Height`, `VSync`, `MsaaSamples`, `Exposure`, `ToneMapping`, `Bloom`,
`BloomIntensity`, `BloomThreshold`, `ColorLut`, `ColorLutStrength` (a lookup table applied last, after
tone mapping and the other grading), `Ssr`, `SsrIntensity`, `SsrMaxDistance`, `SsrThickness`, `SsrSteps`
(screen space reflections; they share the depth prepass with `Ssao`, reflect only what is on screen, and
are weighted by Fresnel rather than by roughness), `FrustumCulling`, `PowerPreference`, `BgraOutput`,
`Shadows`, `ShadowMapSize`, `ShadowDistance`, `ShadowCascades`, `ShadowSoftness`,
`Ssao`, `SsaoRadius`, `SsaoIntensity`, `SsaoBias`, `SsaoSamples`, `SsaoDirectStrength`,
`RenderPath` (`Forward` / `Deferred`), `DepthOfField`, `DofFocusDistance`, `DofFocusRange`, `DofMaxBlur`,
`MotionBlur`, `MotionBlurStrength`, `MotionBlurSamples`, `DebugView`, `Wireframe`,
`Fxaa`, `Vignette`, `VignetteSoftness`, `Grain`, `Saturation`, `Contrast`, `Brightness`,
`ClipPlanes` (up to four world space planes; see [advanced-rendering.md](advanced-rendering.md#clipping-planes)).

Custom shaders, deferred rendering, DoF / motion blur, animation, FBX, KTX2 / Basis and streaming are
explained with examples in [advanced-rendering.md](advanced-rendering.md).

`FrameStats`: `DrawCalls`, `Triangles`, `VisibleNodes`, `CulledNodes`, `Lights`, `CpuTimeMs`,
`ShadowLayers`, `ShadowDrawCalls`.

## Shadows and ambient occlusion

Shadows need two switches: `RendererOptions.Shadows` and `Light.CastShadow`.

```csharp
renderer.Options = renderer.Options with
{
    Shadows = true,
    ShadowMapSize = 2048,      // per layer
    ShadowCascades = 3,        // per directional light
    ShadowDistance = 60f,      // how far directional shadows reach
    ShadowSoftness = 1,        // PCF radius in texels (0 = hard)
    Ssao = true,
    SsaoRadius = 0.6f,
    SsaoIntensity = 1.5f,
    SsaoDirectStrength = 0.25f,
};

Node sun = scene.AddLight(Light.Directional(Vector3.One, 3f) with { CastShadow = true });
sun.LookAt(Vector3.Zero);
floor.CastShadow = false;      // receives only
```

- Directional lights use cascaded shadow maps fitted to the camera frustum, spot lights one perspective
  map; point lights do not cast shadows yet. Eight layers are available per frame (a 3-cascade sun plus
  five spot lights, for example), and lights beyond that keep lighting the scene without shadows.
- Acne and peter-panning are tuned per light with `ShadowBias` (normalised depth) and `ShadowNormalBias`
  (in shadow map texels); `ShadowStrength` fades a shadow out.
- Every mesh node exposes `CastShadow` and `ReceiveShadow` (both default to `true`).
- SSAO runs a normal/depth prepass plus a half resolution occlusion pass and a bilateral blur. It darkens
  ambient and image based lighting fully, and direct lighting by `SsaoDirectStrength`.

## AppWindow

`new AppWindow(WindowOptions)`, events `Load(Renderer)`, `Render(Renderer, float delta)`,
`Input(Renderer, InputEvent)`, `Closing(CloseRequest)`, method `Run()`.
`InputEvent`: `Kind`, `Key`, `Button`, `Modifiers`, `Position`, `Delta`, `IsRepeat`, `Size`, `IsFocused`.

## Raycasting

`Ray(origin, direction)`, `RayHit(Node, Distance, Point, Normal, TriangleIndex, Barycentric)`,
`RaycastOptions`: `MaxDistance`, `Layers`, `VisibleOnly`, `IncludeBackFaces`. `BoundingBox`: `Min`, `Max`,
`Center`, `Size`, `Radius`, `IsEmpty`.

## ThreeNet.Avalonia

- `ThreeNetView` (Control): `Scene`, `Camera`, `IsRendering`, `RenderScale`, `MaxFramesPerSecond`,
  `RendererOptions`, `Renderer`, `Stats`; events `RendererCreated`, `Frame`, `RenderFailed`;
  `RenderFrame()`, `CaptureFrame()`.
- `OrbitController`: `Target`, `Distance`, `Yaw`, `Pitch`, speeds, `EnableRotate/Pan/Zoom`, `AutoRotate`,
  `AutoRotateSpeed`, `FrameBounds(bounds)`, `Apply()`.

## Interactivity

- `InteractionManager(scene, camera?)`: `On(node, NodeEventKind, handler)`, `OnClick`, `OnDoubleClick`,
  `OnPointerEnter/Leave/Down/Up`, `MakeDraggable(node, DragMode, onDrag?)`, `Remove(node)`, `Clear()`,
  `PointerMove/Down/Up(position, viewport)`, `PointerExit()`, `HandleInput(InputEvent, viewport)`,
  `HoveredNode`, `IsDragging`, `HasPointerCapture`, `ClickTolerance`, `DoubleClickTime`, `RaycastOptions`,
  `Event`; overlay: `OnClick/OnPointerEnter/OnPointerLeave(OverlayElement, ...)`, `OverlayClicked`,
  `HoveredOverlayElement`, `OverlayTargetSize`. `ThreeNetView.Interaction`, `EnableInteraction`.
- `Scene.Overlay`: `Add(OverlayElementOptions)`, `AddPanel`, `AddText`, `AddImage`, `AddButton`, `HitTest`,
  `MeasureText`, `LoadFont`, `Clear`, `Scale`, `Enabled`. `OverlayElement`: `Options`, `Update`, `Text`,
  `Visible`, `Color`, `Offset`, `Size`, `GetBounds`, `Remove`.
- `Gamepads`: `Update()`, `Connected`, `this[slot]`, `SlotCount`, `DeadZone`, `TriggerThreshold`,
  `SetVirtual`, `Rumble`, `PlatformError`, `ControllerConnected/Disconnected`. `GamepadState`: `IsDown`,
  `WasPressed`, `WasReleased`, `LeftStick`, `RightStick`, `LeftTrigger`, `RightTrigger`.
- `Node.SetShadowsRecursive(cast, receive)`.

See [interactivity.md](interactivity.md).

## Extensions

- `Scene.Physics` (`PhysicsWorld`): `Gravity`, `FixedTimestep`, `MaxSubsteps`, `AddBody`, `AddCollider`, `Add`,
  `Remove`, `HasBody`, `Step`, `Contact`, `TakeContactEvents`, `ApplyImpulse`, `AddForce`, `SetVelocity`,
  `GetVelocity`, `Teleport`, `IsSleeping`, `Raycast`, `AddJoint` (`PhysicsJoint.Remove`), `MoveCharacter`,
  `ConfigureCharacters`. Options: `RigidBodyOptions`, `ColliderOptions` (`Box`, `Sphere`, `Capsule`, `Cylinder`,
  `TriangleMesh`, `ConvexHull`).
- `AudioEngine`: `Open()`, `CreateOffline(rate)`, `LoadClip`, `CreateClip`, `Play(clip, SoundOptions)`,
  `SetListener`, `Update(scene, listener, dt)`, `Render`, `MasterGain`, `DopplerFactor`, `SpeedOfSound`,
  `DeviceName`, `SampleRate`, and the analysis tap: `EnableAnalysis(size)`, `AnalysisSize`, `Level`,
  `GetWaveform`, `GetSpectrum`. `SoundOptions.Flat/At/On`; `SoundInstance`: `IsPlaying`, `Time`, `Options`,
  `Update`, `Stop`.
- `ThreeNet.Networking`: `NetworkSession.Host/Connect`, `Update`, `Send`, `Replicate`, `StopReplicating`, `Peers`,
  `LocalPeerId`, events; `NetworkOptions`; `ReplicatedNode`.
- XR: `XrRuntime.Probe()` → `XrRuntimeInfo`; `Camera.OffAxis`; `StereoRig` (`Apply`, `RenderSideBySide`).

See [extensions.md](extensions.md).

## Geometry generators

Beyond `CreatePlane/Box/Sphere/Cylinder/Cone/Torus/GridGeometry`:
`CreateCircleGeometry`, `CreateCapsuleGeometry`, `CreateTorusKnotGeometry`, `CreateTubeGeometry`,
`CreateLatheGeometry`, `CreateExtrudeGeometry`, `CreatePolyhedronGeometry(Polyhedron, radius, detail)`.

## ThreeNet.Effects

- `WaterSimulation`: `AddDrop`, `AddDropUv`, `Step`, `Update(dt)`, `ComputeNormals`, `SampleHeight`,
  `SampleNormal`, `CreateTexture`, `Upload`, `Cells`; `Speed`, `Damping`, `StepRate`, `RainRate`, `Center`.
- `WaterSurface`: `Simulation`, `Node`, `Material`, `FloorShader`, `Style` (`WaterStyle.Pool/Lake/Ocean`),
  `SunDirection`, `AddFloor(options)`, `Splash`, `HeightAt`, `NormalAt`, `Update(dt)`.
- `FireEffect`: `Node`, `Material`, `Style` (`FireStyle.Campfire/Torch/Candle/Magic`), `Size`, `Flicker`,
  `Update(dt)`.
- `ParticleEffect`: `Shape` (`EmitterShape`), `Origin`, `Rate`, `Lifetime`, `Size`, `Growth`, `Rise`,
  `Spread`, `Radial`, `Gravity`, `Drag`, `Wind`, `Turbulence`, `Attractors`, `FloorHeight`, `Bounce`,
  `StretchBySpeed`, `Burst`, `Update`, `Upload`, `CreateGeometry`, `GlowMaterial`, `SmokeMaterial`.
- `EffectShaders`: `Water`, `WaterFloor`, `HeightField`, `Fire`, `Sprite`, `Particle`, `Smoke`.
- `PointsAndLines`: `BuildPoints`, `BuildLine`, `PointsMaterial`, `LineMaterial`, and the `Points` / `Lines`
  WGSL. WebGPU draws points and lines one pixel wide, so anything thicker is quads expanded in the vertex
  shader.

See [effects.md](effects.md).

## Scenes and plugins

- `ThreeNet.Scenes.SceneDocument`: `Build(scene)` → `SceneBuildResult` (`Nodes`, `Materials`, `Warnings`,
  `ActiveCamera`), `Save`/`Load`/`ToJson`/`FromJson`, `CreateDefault()`, `AllNodes`, `Find`, `ParentOf`,
  `Remove`, `NextId`, `BaseDirectory`. Definitions: `GeometryDefinition` (primitives or `Model`),
  `TextureDefinition`, `MaterialDefinition`, `LightDefinition`, `CameraDefinition`, `PhysicsDefinition`,
  `NodeDefinition`, `EnvironmentDefinition`.
- `ThreeNet.Plugins`: `IThreeNetPlugin` (`Initialize(IPluginHost)`), `PluginManager` (`LoadDirectory`,
  `Load`, `Run`, `Import`, `Unload`, `Commands`, `Importers`, `Logged`, `DefaultDirectory`),
  `PluginCommand`, `PluginImporter`, `PluginContext` (`Document`, `Selection`, `Scene`, `Report`,
  `RequestRebuild`).

See [editor.md](editor.md).

## Helpers

`MathHelpers.ToRadians/ToDegrees/Rgba/FromHex/SrgbToLinear/LinearToSrgb`, `Colors.*` (linear palette).
