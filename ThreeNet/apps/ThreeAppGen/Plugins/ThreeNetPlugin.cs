using System.ComponentModel;
using System.Text;
using Microsoft.SemanticKernel;
using ThreeAppGen.Services;

namespace ThreeAppGen.Plugins;

/// <summary>
/// Gives Jack an accurate picture of the Three.Net API. Without this the model
/// invents plausible but wrong method names; with it the generated code
/// compiles on the first try far more often.
/// </summary>
public sealed class ThreeNetPlugin
{
    [KernelFunction, Description("Returns the Three.Net API reference for a topic: overview, scene, node, geometry, material, light, camera, renderer, window, avalonia, raycast, loader, postprocessing.")]
    public string GetThreeNetApi([Description("Topic name; 'overview' lists everything.")] string topic = "overview")
    {
        string key = topic.Trim().ToLowerInvariant();
        if (ApiReference.TryGetValue(key, out string? section))
        {
            return section;
        }

        StringBuilder builder = new();
        builder.AppendLine($"Unknown topic '{topic}'. Available topics:");
        foreach (string available in ApiReference.Keys.Order())
        {
            builder.AppendLine($"- {available}");
        }

        return builder.ToString();
    }

    [KernelFunction, Description("Lists the project templates that can be passed to CreateProject.")]
    public string ListTemplates()
    {
        StringBuilder builder = new();
        foreach (ProjectTemplate template in ProjectTemplates.All)
        {
            builder.AppendLine($"- {template.Id}: {template.Title} ({template.Category}) - {template.Description}");
        }

        return builder.ToString();
    }

    [KernelFunction, Description("Returns a complete, compiling Three.Net program for a template id, to use as a starting point.")]
    public string GetTemplateCode([Description("Template id from ListTemplates.")] string templateId)
    {
        ProjectTemplate? template = ProjectTemplates.Find(templateId);
        return template is null
            ? $"Unknown template '{templateId}'."
            : template.ProgramCode;
    }

    private static readonly Dictionary<string, string> ApiReference = new(StringComparer.OrdinalIgnoreCase)
    {
        ["overview"] = """
            THREE.NET - native 3D for .NET (Rust + wgpu core, C# API).

            Namespaces: ThreeNet (core), ThreeNet.Avalonia (UI control), ThreeNet.Interop (Vertex struct).
            Coordinate system: right handed, Y up, -Z forward. Angles are radians (use 45f.ToRadians()).
            Colours are linear Vector4; use MathHelpers.FromHex(0xRRGGBB) or the Colors class.

            Typical program:
                using Scene scene = new();
                Geometry geometry = scene.CreateBoxGeometry(1, 1, 1);
                Material material = scene.CreateMaterial(MaterialOptions.Pbr(Colors.Orange, metallic: 0.1f, roughness: 0.4f));
                Node mesh = scene.AddMesh(geometry, material);
                Node sun = scene.AddLight(Light.Directional(Vector3.One, 3f));
                sun.Position = new Vector3(4, 6, 4);
                sun.LookAt(Vector3.Zero);
                Node camera = scene.AddCamera(Camera.Perspective(55f.ToRadians()), new Vector3(0, 2, 6));
                camera.LookAt(Vector3.Zero);

                AppWindow window = new(WindowOptions.Default with { Title = "Demo" });
                window.Render += (renderer, dt) => renderer.Render(scene, camera);
                window.Run();

            Topics: scene, node, geometry, material, light, camera, renderer, window, avalonia, raycast, loader, postprocessing.
            """,

        ["scene"] = """
            Scene (IDisposable) owns the node graph and every resource.
                Node Root { get; }                  int NodeCount { get; }
                SceneEnvironment Environment { get; set; }   Node? ActiveCamera { get; set; }
                Node CreateNode(Node? parent = null, string? name = null)
                void Remove(Node node)              Node? FindByName(string name)
                BoundingBox GetBounds(Node? node = null)
                Node AddMesh(Geometry g, Material m, Node? parent = null, string? name = null)
                Node AddLight(Light light, Node? parent = null, string? name = null)
                Node AddCamera(Camera camera, Vector3 position = default, Node? parent = null, bool makeActive = true)
                Geometry CreateGeometry(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices = default, PrimitiveTopology topology = TriangleList)
                Material CreateMaterial(in MaterialOptions options) / CreateMaterial(Vector4 color, float metallic = 0, float roughness = 0.5f)
                Texture LoadTexture(string path, bool srgb = true) / LoadTexture(ReadOnlySpan<byte>, bool) / CreateTexture(int w, int h, ReadOnlySpan<byte> pixels, TextureFormat)
                ImportResult LoadGltf(string path, Node? parent = null) / LoadObj(string path, Node? parent = null)
                IReadOnlyList<RayHit> Raycast(Ray ray, RaycastOptions? options = null, int maxHits = 32)
                Ray CreateCameraRay(Node cameraNode, float ndcX, float ndcY, float aspectRatio)

            SceneEnvironment (struct, use `with`): Background, AmbientColor, AmbientIntensity, FogColor,
            FogDensity (0 disables), FogStart, FogEnd, EnvironmentMap, EnvironmentIntensity.
            """,

        ["node"] = """
            Node - a handle into the scene graph (class; equality is by id).
                string Name { get; set; }           Vector3 Position { get; set; }
                Quaternion Rotation { get; set; }   Vector3 EulerAngles { get; set; }  // radians, YXZ
                Vector3 Scale { get; set; }         bool Visible { set; }
                uint Layers { set; }                ulong Tag { get; set; }
                Light? Light { set; }               Camera? Camera { get; set; }
                Node? Parent { get; set; }          IReadOnlyList<Node> Children { get; }
                Matrix4x4 WorldMatrix { get; }      Vector3 WorldPosition { get; }
                Node CreateChild(string? name = null)
                void AttachMesh(Geometry g, Material m)   void DetachMesh()
                void SetTransform(Vector3 p, Quaternion r, Vector3 s)
                void LookAt(Vector3 target, Vector3? up = null)   // -Z axis points at the target
                void Translate(Vector3 delta)      void Rotate(Quaternion rotation)    void Remove()

            Parenting is how you build orbits: rotate the parent, the children follow.
            """,

        ["geometry"] = """
            Primitive factories on Scene:
                CreatePlaneGeometry(width, height, widthSegments, heightSegments)
                CreateBoxGeometry(width, height, depth, segments)
                CreateSphereGeometry(radius, widthSegments, heightSegments)
                CreateCylinderGeometry(radiusTop, radiusBottom, height, radialSegments, heightSegments, capped)
                CreateConeGeometry(radius, height, radialSegments)
                CreateTorusGeometry(radius, tube, radialSegments, tubularSegments, arc)
                CreateGridGeometry(size, divisions)      // line list helper

            Custom geometry uses ThreeNet.Interop.Vertex { Vector3 Position; Vector3 Normal; Vector2 TexCoord; Vector4 Tangent; }
                Geometry g = scene.CreateGeometry(vertices, indices);
                g.Update(vertices, indices);   // re-upload, for animated meshes
                g.ComputeNormals(); g.ComputeTangents();
                (int Vertices, int Indices) counts = g.Counts;
            """,

        ["material"] = """
            MaterialOptions (struct; build with `with`):
                Shading (Basic | Lambert | Phong | Pbr), AlphaMode (Opaque | Mask | Blend), CullMode,
                BaseColor, Emissive, EmissiveIntensity, Metallic, Roughness, Specular, Shininess,
                Reflectance, NormalScale, OcclusionStrength, AlphaCutoff, UvScale, UvOffset,
                DepthWrite, DepthTest, Wireframe, RenderOrder,
                BaseColorMap, NormalMap, MetallicRoughnessMap, EmissiveMap, OcclusionMap (Texture?).

            Factories: MaterialOptions.Pbr(color, metallic, roughness), .Basic(color), .Phong(color, shininess), .Lambert(color).
            Material handle: material.Options (get/set), material.BaseColor, material.Update(o => o with { ... }).
            Emissive values above 1 bloom when the renderer has Bloom enabled.
            """,

        ["light"] = """
            Light (struct): Type (Directional | Point | Spot | Area | Ambient), Color (linear RGB), Intensity,
            Range (0 = infinite), InnerConeAngle, OuterConeAngle, Size, CastShadow, Enabled.
            Factories: Light.Directional(color, intensity), Light.Point(color, intensity, range),
                       Light.Spot(color, intensity, range, innerAngle, outerAngle), Light.Ambient(color, intensity).

            A light is attached to a node and emits along the node -Z axis, so aim it with node.LookAt(target).
            Up to 64 lights are uploaded per frame.
            """,

        ["camera"] = """
            Camera (struct): Camera.Perspective(fieldOfView, near, far) or Camera.Orthographic(height, near, far).
            Fields: IsPerspective, FieldOfView (radians), OrthographicHeight, AspectRatio (0 follows the target), Near, Far.
                Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0, 2, 6));
                camera.LookAt(Vector3.Zero);
            """,

        ["renderer"] = """
            RendererOptions (struct): Width, Height, VSync, MsaaSamples (1/2/4/8), Exposure,
            ToneMapping (None | Reinhard | Aces | Filmic), Bloom, BloomIntensity, BloomThreshold,
            FrustumCulling, PowerPreference, BgraOutput (offscreen only).

            Renderer: CreateOffscreen(options), CreateForWin32(hwnd, options), CreateForX11(...), CreateForAppKit(...).
                void Render(Scene scene, Node? camera = null)
                void Resize(int width, int height)      RendererOptions Options { get; set; }
                FrameStats Stats { get; }               string AdapterName { get; }
                byte[] ReadPixels() / int ReadPixels(Span<byte>)   // offscreen only
            FrameStats: DrawCalls, Triangles, VisibleNodes, CulledNodes, Lights, CpuTimeMs.
            """,

        ["window"] = """
            AppWindow opens a native window with a render loop (Windows, Linux, macOS).
                WindowOptions options = WindowOptions.Default with { Title = "App", Width = 1280, Height = 720,
                    Renderer = RendererOptions.Default with { MsaaSamples = 4, Bloom = true } };
                AppWindow window = new(options);
                window.Load   += renderer => { };              // once, after the GPU is ready
                window.Render += (renderer, deltaSeconds) => renderer.Render(scene, camera);
                window.Input  += (renderer, input) => { };      // keyboard, mouse, touch, resize
                window.Closing += request => request.Cancel = false;
                window.Run();                                   // blocks until the window closes

            InputEvent: Kind (KeyDown, KeyUp, MouseDown, MouseUp, MouseMove, MouseWheel, Touch*, Resized, Focus,
            CloseRequested), Key, Button, Modifiers, Position, Delta, IsRepeat, Size.
            """,

        ["avalonia"] = """
            ThreeNet.Avalonia hosts a scene inside an Avalonia layout.
                <tn:ThreeNetView x:Name="Viewport" />   with xmlns:tn="clr-namespace:ThreeNet.Avalonia;assembly=ThreeNet.Avalonia"
                Viewport.Scene = scene; Viewport.Camera = camera;
                Viewport.Frame += (s, e) => { /* animate with e.DeltaSeconds */ };
                Viewport.RendererCreated += (s, renderer) => { };
                Viewport.RenderScale = 0.75;      // trade sharpness for speed
                WriteableBitmap? shot = Viewport.CaptureFrame();

            OrbitController adds mouse navigation:
                OrbitController orbit = new(Viewport, camera) { AutoRotate = true, AutoRotateSpeed = 0.3f };
                orbit.FrameBounds(scene.GetBounds());
            """,

        ["raycast"] = """
            Picking from screen coordinates:
                float ndcX = (float)(x / width  * 2 - 1);
                float ndcY = (float)(1 - y / height * 2);
                Ray ray = scene.CreateCameraRay(cameraNode, ndcX, ndcY, renderer.AspectRatio);
                IReadOnlyList<RayHit> hits = scene.Raycast(ray);   // nearest first
                if (hits.Count > 0) { Node picked = hits[0].Node; Vector3 point = hits[0].Point; }

            RaycastOptions: MaxDistance (0 = unlimited), Layers, VisibleOnly, IncludeBackFaces.
            RayHit: Node, Distance, Point, Normal, TriangleIndex, Barycentric.
            """,

        ["loader"] = """
            ImportResult result = scene.LoadGltf("model.glb");          // .gltf and .glb
            ImportResult objResult = scene.LoadObj("model.obj");
            result.Root is the node holding the imported hierarchy; NodeCount, GeometryCount,
            MaterialCount and TextureCount report what was created.
            Textures can be loaded separately: scene.LoadTexture("albedo.png", srgb: true).
            """,

        ["postprocessing"] = """
            The renderer draws into an HDR target and then tone maps it.
                RendererOptions.Default with {
                    Bloom = true, BloomIntensity = 0.8f, BloomThreshold = 1.0f,
                    ToneMapping = ToneMapping.Aces, Exposure = 1.2f, MsaaSamples = 4 }
            Emissive materials above 1.0 are what the bloom bright pass picks up:
                MaterialOptions.Basic(color) with { Emissive = colorRgb, EmissiveIntensity = 4f }
            """,
    };
}
