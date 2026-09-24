# Advanced rendering and assets / Rendering lanjutan dan aset

Custom shaders, the deferred renderer, camera effects, animation, FBX, compressed textures, texture
streaming and the asset cache. Every feature below has a ThreeGallery sample.

Shader kustom, deferred renderer, efek kamera, animasi, FBX, tekstur terkompresi, streaming tekstur dan
cache aset. Semua fitur di bawah punya contoh di ThreeGallery.

![Custom shaders](images/gallery-custom-shaders.png)

## Custom shaders

The built-in shader is split into modules (`common.wgsl`, `material.wgsl`, `forward.wgsl`,
`deferred_gbuffer.wgsl`). A custom shader supplies one or both **hooks**; everything else (lighting,
shadows, SSAO, fog, both render paths) keeps working.

```wgsl
// Object space position after deformation.
fn user_vertex(context: VertexContext) -> vec3<f32>
// Change the surface before lighting.
fn user_surface(context: SurfaceContext, surface: Surface) -> Surface
```

| Struct | Fields |
|---|---|
| `VertexContext` | `position`, `normal`, `uv`, `time`, `custom0`, `custom1` |
| `SurfaceContext` | `world_position`, `world_normal`, `view_direction`, `uv`, `screen_uv`, `time`, `custom0`, `custom1` |
| `Surface` | `albedo`, `alpha`, `normal`, `metallic`, `emissive`, `roughness`, `specular`, `occlusion`, `shininess`, `reflectance`, `shading_model`, `receive_shadow` |

Hooks can be written in **WGSL** or **GLSL** (GLSL is translated to WGSL with naga; the same struct and
field names apply). Sources are validated on the CPU when created, so errors surface as a
`ThreeNetException` with the compiler message instead of a GPU crash.

```csharp
Shader glow = scene.CreateShader("""
    Surface user_surface(SurfaceContext context, Surface surface) {
        surface.emissive = context.custom0.rgb * (0.5 + 0.5 * sin(context.time * 3.0));
        return surface;
    }
    """, ShaderLanguage.Glsl, "pulse");

Material material = scene.CreateMaterial(MaterialOptions.Pbr(Colors.White) with
{
    Shader = glow,
    Custom0 = new Vector4(0.2f, 0.8f, 1f, 0f),   // free parameters: custom0 / custom1
});

glow.Update(newSource);          // hot reload; on error the previous version stays active
string wgsl = glow.CompiledWgsl; // inspect what the GLSL became
```

## Shadows from every light type

Directional lights use cascades, spot lights a single perspective map, and point lights a cube of six
faces picked per pixel from the major axis of the light-to-fragment vector:

```csharp
scene.AddLight(Light.Point(Vector3.One, 30f, range: 14f) with { CastShadow = true });
```

Layers are allocated on demand: eight by default (three cascades plus a few spot lights), growing to
sixteen when a scene has point lights, so a cube map costs memory only where one is used. `FrameStats.ShadowLayers`
reports what a frame planned - six per shadowed point light.

## Deferred renderer

`RendererOptions.RenderPath = RenderPath.Deferred` writes opaque geometry into a G-buffer (five
`Rgba16Float` targets plus depth) and lights every pixel in one fullscreen pass. Up to 128 lights are
supported in both paths; with many lights deferred is much cheaper. Transparent objects and lines are drawn
forward on top. MSAA does not apply to the deferred path.

![Deferred lights](images/gallery-deferred-lights.png)

## Depth of field and motion blur

```csharp
options with
{
    DepthOfField = true,
    DofFocusDistance = 11f,   // metres from the camera
    DofFocusRange = 2.5f,     // sharp band around the focus distance
    DofMaxBlur = 16f,         // radius in pixels
    MotionBlur = true,        // camera motion, reprojected from depth
    MotionBlurStrength = 0.8f,
    MotionBlurSamples = 12,
}
```

Both run on the HDR image before bloom and tone mapping, in forward and deferred rendering.

![Depth of field](images/gallery-dof.png)

## Animation

Clips hold channels (translation, rotation, scale) with `Step`, `Linear` or `CubicSpline` interpolation.
glTF and FBX imports create clips and skins automatically; clips can also be built in code.

```csharp
AnimationClip clip = scene.CreateAnimation("bob")
    .AddTranslation(node, [0f, 1f, 2f], [Vector3.Zero, Vector3.UnitY, Vector3.Zero])
    .AddRotation(node, [0f, 2f], [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI)]);

AnimationPlayer player = clip.Play(loop: true);
player.Speed = 1.5f;

// every frame
scene.UpdateAnimations(deltaSeconds);
```

`scene.Animations` lists imported clips (`Name`, `Duration`). Skinned meshes are deformed on the CPU when a
pose changes, so they render (and cast shadows) through the normal pipelines.

## FBX

`scene.LoadFbx(path or bytes)` or `scene.LoadModel(path)` (chooses by extension) imports binary and ASCII
FBX: meshes with normals/UVs, Lambert and Phong materials, embedded or external textures, the node
hierarchy with the full FBX transform stack, skin clusters and animation stacks. Units and the up axis are
converted to Y-up metres.

## Compressed textures (KTX2 / Basis Universal)

`LoadTexture` accepts `.ktx2` and `.basis` besides PNG/JPEG/BMP/TGA/HDR:

- KTX2 with raw formats (RGBA8, RGBA16F, RGBA32F), BC1/3/4/5/7, ETC2 and ASTC 4x4 blocks, optionally
  zstd or zlib supercompressed.
- Basis Universal ETC1S (BasisLZ) and UASTC, in `.basis` or KTX2 containers.

Textures stay compressed in memory until upload. Basis data is transcoded to BC7, ETC2 or ASTC depending
on what the GPU supports, and block formats the GPU cannot sample are decoded to RGBA8 on the CPU.

## Texture streaming and the asset cache

```csharp
Texture albedo = scene.LoadTextureAsync("textures/ground_4k.png");  // returns immediately
// A grey placeholder is used until the image is decoded on a worker thread.
// Rendering swaps finished textures in at the start of a frame.
scene.SetStreamingBudget(2);          // finished textures applied per frame
if (albedo.State == TextureState.Failed) Console.WriteLine(albedo.LoadError);
scene.FinishStreaming(TimeSpan.FromSeconds(5));   // e.g. before a screenshot

Texture shared = scene.LoadTextureCached("textures/bark.png");      // loaded once per path + colour space
ImportResult tree = scene.LoadModelCached("models/tree.glb");       // imported once, then cloned
Console.WriteLine(scene.AssetStats);  // pending, cached, hits, misses, streamed
scene.ClearAssetCache();
```

`LoadModelCached` keeps a hidden prototype and returns a clone that shares geometry, materials and
textures. Models with animations or skins are imported every time, because clips target the nodes of one
import.

---

Three.Net - Gravicode Studios, led by Kang Fadhil.
