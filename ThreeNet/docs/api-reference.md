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
| `LoadGltf(path or bytes, parent?)`, `LoadFbx(path or bytes, parent?)`, `LoadObj(path, parent?)`, `LoadModel(path, parent?)`, `LoadModelCached(path, parent?)` | Asset import, returns `ImportResult` (`AnimationCount`, `SkinCount` included) |
| `CreateShader(source, language, name?)` | Custom shader hooks (WGSL / GLSL) |
| `Animations`, `CreateAnimation(name)`, `UpdateAnimations(delta)` | Animation clips |
| `Raycast(ray, options?, maxHits)`, `CreateCameraRay(camera, ndcX, ndcY, aspect)` | Picking |

## Node

`Name`, `Position`, `Rotation` (Quaternion), `EulerAngles` (YXZ), `Scale`, `Visible` (set), `Layers` (set),
`Tag`, `Light` (set), `Camera`, `Parent`, `Children`, `WorldMatrix`, `WorldPosition`,
`CreateChild`, `AttachMesh`, `DetachMesh`, `SetTransform`, `LookAt(target, up?)`, `Translate`, `Rotate`, `Remove`.

## Instancing and lights

- `Node.Clone(parent)` copies a node and its subtree; the copy shares geometry, materials and textures, so
  import a glTF once and clone it for every placement.
- `Node.Light` has a getter: `node.Light = node.Light is { } l ? l with { Enabled = false } : null;`

## Resources

- `Geometry`: `Counts`, `Update(vertices, indices)`, `ComputeNormals()`, `ComputeTangents()`, `Destroy()`.
- `Material`: `Options` (get/set), `Update(Func<MaterialOptions, MaterialOptions>)`, `BaseColor`, `Destroy()`.
- `Texture`: `SetSampler(wrapU, wrapV, linearFilter, mipmaps, anisotropy)`, `State` (`TextureState`), `LoadError`, `Destroy()`.
- `Shader`: `Name`, `CompiledWgsl`, `Update(source, language)`, `Destroy()`.
- `AnimationClip`: `Name`, `Duration`, `AddTranslation`, `AddRotation`, `AddScale`, `AddChannel(node, path, interpolation, times, values)`, `Play(loop)`, `Destroy()`.
- `AnimationPlayer`: `Time`, `Speed`, `Weight`, `Loop`, `IsPlaying`, `Stop()`.
- `MaterialOptions` (struct): `Shading`, `AlphaMode`, `CullMode`, `BaseColor`, `Emissive`, `EmissiveIntensity`,
  `Metallic`, `Roughness`, `Specular`, `Shininess`, `Reflectance`, `NormalScale`, `OcclusionStrength`,
  `AlphaCutoff`, `UvScale`, `UvOffset`, `DepthWrite`, `DepthTest`, `Wireframe`, `RenderOrder`, `BaseColorMap`,
  `NormalMap`, `MetallicRoughnessMap`, `EmissiveMap`, `OcclusionMap`, `Shader`, `Custom0`, `Custom1`;
  factories `Pbr`, `Basic`, `Phong`, `Lambert`.

## Lighting and cameras

- `Light` (struct): `Type`, `Color`, `Intensity`, `Range`, `InnerConeAngle`, `OuterConeAngle`, `Size`,
  `CastShadow`, `Enabled`, `ShadowBias`, `ShadowNormalBias`, `ShadowStrength`;
  factories `Directional`, `Point`, `Spot`, `Ambient`.
- `Camera` (struct): `Perspective(fov, near, far)`, `Orthographic(height, near, far)`, `AspectRatio` (0 = auto).
- `SceneEnvironment` (struct): `Background`, `AmbientColor`, `AmbientIntensity`, `FogColor`, `FogDensity`,
  `FogStart`, `FogEnd`, `EnvironmentMap`, `EnvironmentIntensity`.

## Renderer : IDisposable

| Member | Description |
|---|---|
| `CreateOffscreen(options)`, `CreateForWin32(hwnd, options)`, `CreateForX11(window, display, screen, options)`, `CreateForAppKit(nsView, options)` | Factories |
| `Render(scene, camera?)` | Draws a frame |
| `Resize(width, height)`, `Options`, `Width`, `Height`, `AspectRatio` | Target |
| `ReadPixels()`, `ReadPixels(Span<byte>)`, `PixelBufferSize` | Offscreen readback |
| `Stats` (`FrameStats`), `AdapterName`, `IsOffscreen` | Diagnostics |

`RendererOptions`: `Width`, `Height`, `VSync`, `MsaaSamples`, `Exposure`, `ToneMapping`, `Bloom`,
`BloomIntensity`, `BloomThreshold`, `FrustumCulling`, `PowerPreference`, `BgraOutput`,
`Shadows`, `ShadowMapSize`, `ShadowDistance`, `ShadowCascades`, `ShadowSoftness`,
`Ssao`, `SsaoRadius`, `SsaoIntensity`, `SsaoBias`, `SsaoSamples`, `SsaoDirectStrength`,
`RenderPath` (`Forward` / `Deferred`), `DepthOfField`, `DofFocusDistance`, `DofFocusRange`, `DofMaxBlur`,
`MotionBlur`, `MotionBlurStrength`, `MotionBlurSamples`.

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
  `DeviceName`, `SampleRate`. `SoundOptions.Flat/At/On`; `SoundInstance`: `IsPlaying`, `Time`, `Options`,
  `Update`, `Stop`.
- `ThreeNet.Networking`: `NetworkSession.Host/Connect`, `Update`, `Send`, `Replicate`, `StopReplicating`, `Peers`,
  `LocalPeerId`, events; `NetworkOptions`; `ReplicatedNode`.
- XR: `XrRuntime.Probe()` → `XrRuntimeInfo`; `Camera.OffAxis`; `StereoRig` (`Apply`, `RenderSideBySide`).

See [extensions.md](extensions.md).

## Helpers

`MathHelpers.ToRadians/ToDegrees/Rgba/FromHex/SrgbToLinear/LinearToSrgb`, `Colors.*` (linear palette).
