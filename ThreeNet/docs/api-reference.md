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
| `LoadGltf(path or bytes, parent?)`, `LoadObj(path, parent?)` | Asset import, returns `ImportResult` |
| `Raycast(ray, options?, maxHits)`, `CreateCameraRay(camera, ndcX, ndcY, aspect)` | Picking |

## Node

`Name`, `Position`, `Rotation` (Quaternion), `EulerAngles` (YXZ), `Scale`, `Visible` (set), `Layers` (set),
`Tag`, `Light` (set), `Camera`, `Parent`, `Children`, `WorldMatrix`, `WorldPosition`,
`CreateChild`, `AttachMesh`, `DetachMesh`, `SetTransform`, `LookAt(target, up?)`, `Translate`, `Rotate`, `Remove`.

## Resources

- `Geometry`: `Counts`, `Update(vertices, indices)`, `ComputeNormals()`, `ComputeTangents()`, `Destroy()`.
- `Material`: `Options` (get/set), `Update(Func<MaterialOptions, MaterialOptions>)`, `BaseColor`, `Destroy()`.
- `Texture`: `SetSampler(wrapU, wrapV, linearFilter, mipmaps, anisotropy)`, `Destroy()`.
- `MaterialOptions` (struct): `Shading`, `AlphaMode`, `CullMode`, `BaseColor`, `Emissive`, `EmissiveIntensity`,
  `Metallic`, `Roughness`, `Specular`, `Shininess`, `Reflectance`, `NormalScale`, `OcclusionStrength`,
  `AlphaCutoff`, `UvScale`, `UvOffset`, `DepthWrite`, `DepthTest`, `Wireframe`, `RenderOrder`, `BaseColorMap`,
  `NormalMap`, `MetallicRoughnessMap`, `EmissiveMap`, `OcclusionMap`; factories `Pbr`, `Basic`, `Phong`, `Lambert`.

## Lighting and cameras

- `Light` (struct): `Type`, `Color`, `Intensity`, `Range`, `InnerConeAngle`, `OuterConeAngle`, `Size`,
  `CastShadow`, `Enabled`; factories `Directional`, `Point`, `Spot`, `Ambient`.
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
`BloomIntensity`, `BloomThreshold`, `FrustumCulling`, `PowerPreference`, `BgraOutput`.

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

## Helpers

`MathHelpers.ToRadians/ToDegrees/Rgba/FromHex/SrgbToLinear/LinearToSrgb`, `Colors.*` (linear palette).
