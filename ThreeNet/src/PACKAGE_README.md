# Three.Net

Native multiplatform 3D for .NET: a Rust + wgpu rendering core (DirectX 12, Metal, Vulkan) behind a
Three.js style C# API. Made by Gravicode Studios, led by Kang Fadhil.

| Package | Contents |
|---|---|
| `ThreeNet` | Scene graph, geometry, PBR / Phong / Lambert / Basic materials, lights, cameras, renderer (HDR, bloom, tone mapping), glTF/OBJ loading, raycasting, native window with input |
| `ThreeNet.Native` | The native core for win-x64, linux-x64 and osx-arm64 (pulled in automatically) |
| `ThreeNet.Avalonia` | `ThreeNetView` control and `OrbitController` for Avalonia 12 |

```bash
dotnet add package ThreeNet
```

```csharp
using System.Numerics;
using ThreeNet;

using Scene scene = new();
Node cube = scene.AddMesh(scene.CreateBoxGeometry(), scene.CreateMaterial(MaterialOptions.Pbr(Colors.Orange, 0.1f, 0.4f)));
Node sun = scene.AddLight(Light.Directional(Vector3.One, 3f));
sun.Position = new Vector3(4, 6, 4);
sun.LookAt(Vector3.Zero);
Node camera = scene.AddCamera(Camera.Perspective(55f.ToRadians()), new Vector3(0, 2, 6));
camera.LookAt(Vector3.Zero);

AppWindow window = new(WindowOptions.Default with { Title = "Hello Three.Net" });
float t = 0;
window.Render += (renderer, dt) =>
{
    cube.EulerAngles = new Vector3(0, t += dt, 0);
    renderer.Render(scene, camera);
};
window.Run();
```

Documentation, samples (ThreeGallery), the AI app generator (ThreeAppGen) and the Three.js converter:
https://github.com/DotNetVibeCoderz/Vibe_Graphics/tree/main/ThreeNet

Requirements: .NET 10 and a GPU with DirectX 12, Metal or Vulkan.
