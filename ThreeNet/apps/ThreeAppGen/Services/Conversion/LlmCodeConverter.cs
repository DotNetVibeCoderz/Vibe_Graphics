using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ThreeAppGen.Plugins;

namespace ThreeAppGen.Services.Conversion;

/// <summary>A file proposed by the model, relative to the Core project folder.</summary>
public sealed record GeneratedFile(string RelativePath, string Content);

/// <summary>What the model returned: files plus its own conversion notes.</summary>
public sealed record LlmConversionOutput(IReadOnlyList<GeneratedFile> Files, string Notes);

/// <summary>
/// The LLM half of the conversion. The prompts follow a few rules that make
/// source-to-source translation reliable:
/// <list type="bullet">
/// <item>a fixed output contract (class name, members) so hosts never change;</item>
/// <item>grounding: the Three.js to Three.Net mapping table, the real API reference
/// and the statically extracted inventory, so the model does not invent members;</item>
/// <item>a compiling baseline to start from, so the model edits rather than guesses;</item>
/// <item>a strict, machine parseable response format;</item>
/// <item>repair prompts that carry exact compiler diagnostics and the current files.</item>
/// </list>
/// </summary>
public sealed partial class LlmCodeConverter(AppSettings settings)
{
    /// <summary>Approximate character budget for source code in a single request.</summary>
    private const int SourceBudget = 60_000;

    private Kernel? _kernel;

    public bool IsAvailable => settings.Active.IsConfigured;

    public string ModelDescription => $"{settings.ActiveProvider} / {settings.Active.Model}";

    private IChatCompletionService Chat
    {
        get
        {
            _kernel ??= KernelFactory.Create(settings, []);
            return _kernel.GetRequiredService<IChatCompletionService>();
        }
    }

    /// <summary>Summarises a module that does not fit the main request.</summary>
    public async Task<string> SummarizeModuleAsync(SourceFile script, CancellationToken cancellationToken)
    {
        string system = """
            You analyse JavaScript modules of a Three.js application so that another step can port them to C#.
            Describe precisely, as a compact bullet list: exported functions/classes, objects created (geometry,
            material, light, camera, mesh with exact numeric parameters and colours), transforms, animation logic
            as formulas, input handling, and asset paths. Keep numbers and names exact. No prose, no code fences.
            """;
        string user = $"Module `{script.RelativePath}`:\n\n{Truncate(script.Content, 24_000)}";
        return await CompleteAsync(system, user, cancellationToken);
    }

    /// <summary>Full conversion request: Three.js sources to the ConvertedScene contract.</summary>
    public async Task<LlmConversionOutput> ConvertAsync(
        string rootNamespace,
        ThreeJsProject project,
        SceneInventory inventory,
        string baselineCode,
        IReadOnlyDictionary<string, string> moduleSummaries,
        CancellationToken cancellationToken)
    {
        StringBuilder user = new();
        user.AppendLine($"Convert this Three.js project to Three.Net. Root namespace: `{rootNamespace}.Core`.");
        if (project.ThreeVersion is not null)
        {
            user.AppendLine($"Three.js version: {project.ThreeVersion}.");
        }

        user.AppendLine();
        user.AppendLine("## Assets (copied to the Assets folder, same relative paths)");
        foreach (AssetFile asset in project.Assets.Take(200))
        {
            user.AppendLine($"- {asset.RelativePath} ({asset.Kind})");
        }

        user.AppendLine();
        user.AppendLine("## Detected features");
        user.AppendLine(string.Join(", ", project.DetectedFeatures));
        user.AppendLine();
        user.AppendLine("## Static scene inventory (JSON, extracted from the source; values are reliable)");
        user.AppendLine(JsonSerializer.Serialize(inventory, JsonOptions));
        user.AppendLine();
        user.AppendLine("## Baseline C# (compiles today; improve it, keep what is right)");
        user.AppendLine("```csharp");
        user.AppendLine(baselineCode);
        user.AppendLine("```");
        user.AppendLine();
        user.AppendLine("## Three.js sources");

        int budget = SourceBudget;
        foreach (SourceFile script in project.ScriptsByPriority)
        {
            if (moduleSummaries.TryGetValue(script.RelativePath, out string? summary))
            {
                user.AppendLine($"### {script.RelativePath} (summary, the module was too large to include)");
                user.AppendLine(summary);
                continue;
            }

            if (budget <= 0)
            {
                user.AppendLine($"### {script.RelativePath} (omitted: over the request budget)");
                continue;
            }

            string content = Truncate(script.Content, budget);
            budget -= content.Length;
            user.AppendLine($"### {script.RelativePath}");
            user.AppendLine("```javascript");
            user.AppendLine(content);
            user.AppendLine("```");
        }

        foreach (SourceFile page in project.Pages.Take(2))
        {
            user.AppendLine($"### {page.RelativePath} (HTML, for UI overlays and import maps only)");
            user.AppendLine("```html");
            user.AppendLine(Truncate(page.Content, 6_000));
            user.AppendLine("```");
        }

        string response = await CompleteAsync(BuildSystemPrompt(rootNamespace), user.ToString(), cancellationToken);
        return ParseResponse(response);
    }

    /// <summary>Repair request: exact diagnostics plus the current Core files.</summary>
    public async Task<LlmConversionOutput> FixAsync(
        string rootNamespace,
        IReadOnlyList<BuildDiagnostic> errors,
        IReadOnlyList<GeneratedFile> currentFiles,
        int attempt,
        CancellationToken cancellationToken)
    {
        StringBuilder user = new();
        user.AppendLine($"Attempt {attempt}: the converted project does not compile. Fix every error below.");
        user.AppendLine("Change only what is needed. Do not remove features to silence errors unless the API truly does not exist;");
        user.AppendLine("in that case keep the intent with the closest Three.Net equivalent and a `// TODO(threenet):` comment.");
        user.AppendLine();
        user.AppendLine("## Compiler errors");
        foreach (BuildDiagnostic error in errors.Take(40))
        {
            user.AppendLine($"- {error}");
        }

        user.AppendLine();
        user.AppendLine("## Current files (paths relative to the Core project)");
        // The contract helpers are fixed and documented in the system prompt; resending them wastes context.
        foreach (GeneratedFile file in currentFiles.Where(f => !BaselineCodeGenerator.ProtectedFiles.Contains(Path.GetFileName(f.RelativePath), StringComparer.OrdinalIgnoreCase)))
        {
            user.AppendLine($"<file path=\"{file.RelativePath}\">");
            user.AppendLine(file.Content);
            user.AppendLine("</file>");
        }

        user.AppendLine();
        user.AppendLine("Return the complete corrected content of every file you change, using the required format.");

        string response = await CompleteAsync(BuildSystemPrompt(rootNamespace), user.ToString(), cancellationToken);
        return ParseResponse(response);
    }

    private async Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken)
    {
        ChatHistory history = [];
        history.AddSystemMessage(system);
        history.AddUserMessage(user);

        // Translation wants determinism, not creativity, and room for whole files.
        PromptExecutionSettings execution = KernelFactory.CreateExecutionSettings(
            settings, Math.Min(settings.Temperature, 0.2), Math.Max(settings.MaxTokens, 32_000), autoInvokeFunctions: false);

        ChatMessageContent reply = await Chat.GetChatMessageContentAsync(history, execution, cancellationToken: cancellationToken);
        return reply.Content ?? string.Empty;
    }

    public static string BuildSystemPrompt(string rootNamespace)
    {
        ThreeNetPlugin api = new();
        StringBuilder reference = new();
        foreach (string topic in new[] { "overview", "scene", "node", "geometry", "material", "light", "camera", "renderer", "window", "raycast", "loader", "postprocessing" })
        {
            reference.AppendLine(api.GetThreeNetApi(topic));
            reference.AppendLine();
        }

        return $$"""
            You are a senior graphics engineer porting Three.js (JavaScript/TypeScript) applications to
            Three.Net, a .NET 10 3D library with a Rust wgpu core. You write idiomatic, compiling C# 13.

            # Output contract (mandatory)
            Produce the file `ConvertedScene.cs` in namespace `{{rootNamespace}}.Core` containing:

                public sealed partial class ConvertedScene : IDisposable
                {
                    public ConvertedScene(string assetsRoot)            // build the whole scene here
                    public Scene Scene { get; }
                    public Node Camera { get; }
                    public RendererOptions RendererOptions { get; }
                    public bool UsesOrbitControls { get; }              // true if the source used OrbitControls
                    public Vector3 OrbitTarget { get; }
                    public void Update(float deltaSeconds, double totalSeconds)   // body of the animation loop
                    public void OnInput(InputEvent input)               // keyboard / pointer / resize handlers
                    public void Dispose()
                }

            You may add more files (helper classes, systems) inside the Core project, all in namespace
            `{{rootNamespace}}.Core`. Never write `ThreeJsCompat.cs` or `OrbitRig.cs`: they exist already and
            you may use them:
                ThreeJsCompat.EulerXyz(x, y, z) -> Quaternion   (Three.js default XYZ Euler order)
                ThreeJsCompat.DegToRad(float), ThreeJsCompat.Color(uint hex, float alpha = 1) -> Vector4 (linear)
                ThreeJsCompat.ColorRgb(uint hex) -> Vector3 (linear), for lights and emissive
                ThreeJsCompat.TryLoadTexture(Scene scene, string assetsRoot, string relativePath, bool srgb = true) -> Texture?
                ThreeJsCompat.TryLoadModel(Scene scene, string assetsRoot, string relativePath, Node? parent = null) -> Node?
                ThreeJsCompat.CreateTorusKnotGeometry(Scene scene, float radius, float tube, int tubularSegments, int radialSegments, int p = 2, int q = 3) -> Geometry
                ThreeJsCompat.CreatePolyhedronGeometry(Scene scene, string kind /* "tetrahedron" | "octahedron" | "icosahedron" */, float radius) -> Geometry
                ThreeJsCompat.CreateCircleGeometry(Scene scene, float radius, int segments) -> Geometry
                ThreeJsCompat.CreateRingGeometry(Scene scene, float innerRadius, float outerRadius, int segments) -> Geometry
                ThreeJsCompat.CreateCapsuleGeometry(Scene scene, float radius, float length, int capSegments, int radialSegments) -> Geometry
                ThreeJsCompat.CreateLatheGeometry(Scene scene, IReadOnlyList<Vector2> points, int segments) -> Geometry
            Hosts own the window, the render loop, orbit controls and resizing. Do not create windows,
            renderers or render loops, do not reference Avalonia.

            # Mapping rules
            - `new THREE.Scene()` -> `Scene = new Scene()`; `scene.add(x)` -> nodes are created with a parent argument.
            - Group/Object3D -> `Scene.CreateNode(parent, name)`; Mesh -> `Scene.AddMesh(geometry, material, parent, name)`.
            - Geometries: Box/Sphere/Plane/Cylinder/Cone/Torus map 1:1 (same argument order). TorusKnot,
              Tetrahedron/Octahedron/Icosahedron, Circle, Ring, Capsule and Lathe use the ThreeJsCompat factories above
              with the Three.js argument order (note TorusKnot is tubularSegments THEN radialSegments). Anything else:
              build it with `Scene.CreateGeometry(vertices, indices)` using `ThreeNet.Interop.Vertex`.
            - MeshStandardMaterial/MeshPhysicalMaterial -> MaterialOptions.Pbr(color, metalness, roughness);
              MeshBasicMaterial -> Basic; MeshPhongMaterial -> Phong(color, shininess); MeshLambertMaterial -> Lambert.
              `transparent`/`opacity` -> AlphaMode.Blend and colour alpha; `side: DoubleSide` -> CullMode.None;
              `map` -> BaseColorMap, `normalMap` -> NormalMap (srgb: false), `emissive` -> Emissive + EmissiveIntensity.
            - Colours: Three.js hex values are sRGB, use ThreeJsCompat.Color / ColorRgb (they linearise).
            - Lights attach to nodes and shine along the node -Z axis: for DirectionalLight and SpotLight set the
              node position then `LookAt(target)` (the Three.js target defaults to the origin).
              AmbientLight/HemisphereLight -> Light.Ambient. SpotLight angle -> outer angle, inner = angle * (1 - penumbra).
            - PerspectiveCamera(fov in DEGREES, aspect, near, far) -> Camera.Perspective(DegToRad(fov), near, far);
              the aspect ratio is handled by the host. Assign the camera node to the `Camera` property.
            - `object.rotation.set(x, y, z)` / `rotation.x = v` -> keep an Euler Vector3 field and assign
              `node.Rotation = ThreeJsCompat.EulerXyz(...)`. `position`/`scale` map to Node.Position/Scale.
            - Animation loop: move the body of `animate()`/`setAnimationLoop` into `Update`. Per-frame increments
              written without a clock assume 60 frames per second: `x += 0.01` becomes `x += 0.6f * deltaSeconds`.
              `clock.getElapsedTime()` -> `totalSeconds`; `clock.getDelta()` -> `deltaSeconds`.
            - Loaders: TextureLoader/GLTFLoader/OBJLoader calls become ThreeJsCompat.TryLoadTexture/TryLoadModel with the
              path relative to the assets root. Loading is synchronous: move `onLoad` callback bodies inline.
            - EffectComposer + UnrealBloomPass -> RendererOptions Bloom/BloomIntensity/BloomThreshold.
              renderer.toneMapping -> ToneMapping (ACESFilmic -> Aces, Reinhard -> Reinhard, none -> None);
              toneMappingExposure -> Exposure; antialias -> MsaaSamples = 4.
            - scene.background Color -> Scene.Environment Background; Fog/FogExp2 -> FogColor/FogDensity/FogStart.
              Set `AmbientIntensity = 0f` in the environment unless the source adds ambient light.
            - Input: `window.addEventListener('keydown')` and pointer handlers move into OnInput. Example:
              `if (input.Kind == InputEventKind.KeyDown && input.Key == Key.Space && !input.IsRepeat) _paused = !_paused;`
              and honour such flags in Update exactly like the source does. Key names follow `Key` (A-Z, Digit0-9,
              Space, Enter, Escape, Left/Right/Up/Down, ShiftLeft...).
            - Control flow: reproduce loops, helper functions and arrays from the source (for example a function that
              creates N objects in a loop becomes a C# method with the same loop). The static inventory only knows
              top level objects; objects created inside loops or helper functions are NOT in it.
            - Raycaster from pointer events -> in OnInput use Scene.CreateCameraRay + Scene.Raycast (the host gives
              window pixel positions; use the size from InputEventKind.Resized events to build NDC).
            - DOM, CSS, lil-gui, stats.js, window resize code: drop them; note them in <notes>.
            - Shaders (ShaderMaterial), physics engines, audio, WebXR, skeletal animation: not available yet.
              Keep the visual intent with the closest material/transform logic and add TODO comments.

            # Code quality
            - Fields for every object the loop touches; clear names that follow the JavaScript variable names.
            - Use `float` literals (`0.5f`), `System.Numerics` types, file scoped namespace, `using ThreeNet;`.
            - Only use APIs from the reference below. If something is not listed, it does not exist.

            # Response format (strict)
            Reply with one or more blocks, and nothing else outside them:
            <file path="ConvertedScene.cs">
            ...complete C# file...
            </file>
            <notes>
            - bullet list of approximations, dropped features and follow-up advice
            </notes>

            # Three.Net API reference
            {{reference}}
            """;
    }

    public static LlmConversionOutput ParseResponse(string response)
    {
        List<GeneratedFile> files = [];
        foreach (Match match in FileBlock().Matches(response))
        {
            string path = match.Groups["path"].Value.Trim().Replace('\\', '/').TrimStart('/');
            // The model sometimes repeats the project folder in the path.
            int coreIndex = path.IndexOf(".Core/", StringComparison.OrdinalIgnoreCase);
            if (coreIndex >= 0)
            {
                path = path[(coreIndex + ".Core/".Length)..];
            }

            if (path.Contains("..", StringComparison.Ordinal) || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (BaselineCodeGenerator.ProtectedFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            files.Add(new GeneratedFile(path, StripFences(match.Groups["content"].Value).Trim() + Environment.NewLine));
        }

        // Fallback: a single fenced C# block is treated as ConvertedScene.cs.
        if (files.Count == 0)
        {
            Match fence = CSharpFence().Match(response);
            if (fence.Success && fence.Groups["code"].Value.Contains("class ConvertedScene", StringComparison.Ordinal))
            {
                files.Add(new GeneratedFile("ConvertedScene.cs", fence.Groups["code"].Value.Trim() + Environment.NewLine));
            }
        }

        Match notes = NotesBlock().Match(response);
        return new LlmConversionOutput(files, notes.Success ? notes.Groups["notes"].Value.Trim() : string.Empty);
    }

    private static string StripFences(string content)
    {
        string trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewLine = trimmed.IndexOf('\n');
            trimmed = firstNewLine < 0 ? string.Empty : trimmed[(firstNewLine + 1)..];
            int closing = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (closing >= 0)
            {
                trimmed = trimmed[..closing];
            }
        }

        return trimmed;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "\n/* ... truncated ... */";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = true,
    };

    [GeneratedRegex(@"<file\s+path\s*=\s*""(?<path>[^""]+)""\s*>(?<content>.*?)</file>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FileBlock();

    [GeneratedRegex(@"<notes>(?<notes>.*?)</notes>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex NotesBlock();

    [GeneratedRegex(@"```(?:csharp|cs|c#)\s*\n(?<code>.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CSharpFence();
}
