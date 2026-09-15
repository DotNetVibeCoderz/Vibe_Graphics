using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using ThreeAppGen.Plugins;

namespace ThreeAppGen.Services.Conversion;

/// <summary>
/// Static pass over Three.js sources. It does not try to be a JavaScript
/// interpreter: it recognises the idioms almost every Three.js scene uses
/// (constructors, transform setters, scene.add, loaders, the animation loop)
/// and records anything it cannot resolve as a note for the LLM and the report.
/// </summary>
public static partial class SceneInventoryExtractor
{
    private static readonly Dictionary<string, (string Primitive, bool Approximated)> GeometryMap = new(StringComparer.Ordinal)
    {
        ["BoxGeometry"] = ("Box", false),
        ["BoxBufferGeometry"] = ("Box", false),
        ["SphereGeometry"] = ("Sphere", false),
        ["SphereBufferGeometry"] = ("Sphere", false),
        ["PlaneGeometry"] = ("Plane", false),
        ["PlaneBufferGeometry"] = ("Plane", false),
        ["CylinderGeometry"] = ("Cylinder", false),
        ["ConeGeometry"] = ("Cone", false),
        ["TorusGeometry"] = ("Torus", false),
        ["TorusKnotGeometry"] = ("Compat", false),
        ["IcosahedronGeometry"] = ("Compat", false),
        ["OctahedronGeometry"] = ("Compat", false),
        ["DodecahedronGeometry"] = ("Sphere", true),
        ["TetrahedronGeometry"] = ("Compat", false),
        ["CapsuleGeometry"] = ("Compat", false),
        ["CircleGeometry"] = ("Compat", false),
        ["RingGeometry"] = ("Compat", false),
    };

    private static readonly Dictionary<string, string> MaterialMap = new(StringComparer.Ordinal)
    {
        ["MeshStandardMaterial"] = "Pbr",
        ["MeshPhysicalMaterial"] = "Pbr",
        ["MeshPhongMaterial"] = "Phong",
        ["MeshToonMaterial"] = "Lambert",
        ["MeshLambertMaterial"] = "Lambert",
        ["MeshBasicMaterial"] = "Basic",
        ["MeshNormalMaterial"] = "Pbr",
        ["MeshMatcapMaterial"] = "Pbr",
    };

    private static readonly Dictionary<string, string> LightMap = new(StringComparer.Ordinal)
    {
        ["AmbientLight"] = "Ambient",
        ["HemisphereLight"] = "Ambient",
        ["DirectionalLight"] = "Directional",
        ["PointLight"] = "Point",
        ["SpotLight"] = "Spot",
        ["RectAreaLight"] = "Area",
    };

    private static readonly Dictionary<string, string> UnsupportedAdvice = new(StringComparer.Ordinal)
    {
        ["ShaderMaterial"] = "ShaderMaterial: custom GLSL has no direct equivalent yet; approximated with a PBR material. Port the shader to WGSL when custom shader injection lands (roadmap phase 2).",
        ["RawShaderMaterial"] = "RawShaderMaterial: approximated with a PBR material; the GLSL needs a manual WGSL port.",
        ["Points"] = "THREE.Points: point clouds are approximated with small spheres; consider a custom geometry with PrimitiveTopology.PointList.",
        ["Line"] = "THREE.Line / LineSegments: use scene.CreateGeometry(..., PrimitiveTopology.LineList).",
        ["Sprite"] = "Sprites: replace with camera facing planes.",
        ["CSS2DRenderer"] = "CSS2DRenderer: HTML labels do not exist on desktop; use an Avalonia overlay or text meshes.",
        ["CSS3DRenderer"] = "CSS3DRenderer: HTML in 3D is not supported.",
        ["AnimationMixer"] = "AnimationMixer: skeletal / keyframe clips from glTF are not played yet (roadmap phase 3); transforms are static.",
        ["EffectComposer"] = "EffectComposer: only bloom and tone mapping map to the Three.Net post-processing chain.",
        ["PositionalAudio"] = "Positional audio: spatial audio is on the roadmap (phase 6).",
        ["AudioListener"] = "Audio: not converted; spatial audio is on the roadmap (phase 6).",
        ["VRButton"] = "WebXR: OpenXR support is on the roadmap (phase 6).",
        ["castShadow"] = "Shadows: shadow maps are not rendered yet; lighting is kept without shadows.",
        ["cannon"] = "cannon-es physics: port the simulation to C# or a .NET physics engine (for example BepuPhysics).",
        ["rapier"] = "Rapier physics: port the simulation to C# or BepuPhysics.",
        ["lil-gui"] = "lil-gui panel: rebuild the controls with Avalonia widgets.",
        ["dat.gui"] = "dat.gui panel: rebuild the controls with Avalonia widgets.",
    };

    private static readonly Dictionary<string, Vector3> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = new(1f, 1f, 1f), ["black"] = new(0f, 0f, 0f), ["red"] = new(1f, 0f, 0f), ["green"] = new(0f, 0.5f, 0f),
        ["lime"] = new(0f, 1f, 0f), ["blue"] = new(0f, 0f, 1f), ["yellow"] = new(1f, 1f, 0f), ["cyan"] = new(0f, 1f, 1f),
        ["magenta"] = new(1f, 0f, 1f), ["orange"] = new(1f, 0.647f, 0f), ["purple"] = new(0.5f, 0f, 0.5f),
        ["gray"] = new(0.5f, 0.5f, 0.5f), ["grey"] = new(0.5f, 0.5f, 0.5f), ["skyblue"] = new(0.53f, 0.81f, 0.92f),
        ["hotpink"] = new(1f, 0.41f, 0.71f), ["gold"] = new(1f, 0.84f, 0f), ["silver"] = new(0.75f, 0.75f, 0.75f),
    };

    public static SceneInventory Extract(ThreeJsProject project, Action<ConversionLogLevel, string>? log = null)
    {
        SceneInventory inventory = new();
        Dictionary<string, string> constants = new(StringComparer.Ordinal);

        foreach (SourceFile script in project.ScriptsByPriority)
        {
            string code = StripComments(script.Content);
            CollectConstants(code, constants);
            ExtractFromScript(code, inventory, constants, project);
        }

        foreach (string feature in UnsupportedAdvice.Keys)
        {
            if (project.Scripts.Any(s => s.Content.Contains(feature, StringComparison.Ordinal)))
            {
                inventory.Unsupported.Add(UnsupportedAdvice[feature]);
            }
        }

        if (inventory.Camera is null)
        {
            inventory.Notes.Add("No camera constructor was found; a default perspective camera at (0, 2, 6) is used.");
        }

        log?.Invoke(ConversionLogLevel.Info,
            $"Inventory: {inventory.Geometries.Count} geometries, {inventory.Materials.Count} materials, " +
            $"{inventory.Objects.Count(o => o.Kind == SceneObjectKind.Mesh)} meshes, " +
            $"{inventory.Objects.Count(o => o.Kind == SceneObjectKind.Light)} lights, " +
            $"{inventory.Objects.Count(o => o.Kind == SceneObjectKind.Model)} models, {inventory.Animations.Count} animations");
        foreach (string unsupported in inventory.Unsupported)
        {
            log?.Invoke(ConversionLogLevel.Warning, unsupported);
        }

        return inventory;
    }

    // --------------------------------------------------------------- parsing

    private static void ExtractFromScript(string code, SceneInventory inventory, Dictionary<string, string> constants, ThreeJsProject project)
    {
        int anonymous = inventory.Objects.Count;

        // Texture loads assigned to a variable: const tex = loader.load('path')
        foreach (Match match in TextureAssignment().Matches(code))
        {
            inventory.Textures[match.Groups["var"].Value] = NormalizeAssetPath(match.Groups["path"].Value, project);
        }

        // texture.repeat.set(u, v) becomes the UV scale of every material using the texture.
        foreach (Match match in TextureRepeat().Matches(code))
        {
            string[] parts = SplitArguments(ReadBalanced(code, match.Index + match.Length - 1));
            if (parts.Length >= 2 && EvaluateNumber(parts[0], constants) is { } u && EvaluateNumber(parts[1], constants) is { } v)
            {
                inventory.TextureRepeats[match.Groups["var"].Value] = new Vector2(u, v);
            }
        }

        // Objects created while iterating exist many times at runtime: a static
        // pass would record a single misleading instance, so leave them to the LLM.
        List<(int Start, int End)> iterations = IterationRanges(code);
        bool InsideIteration(int index) => iterations.Any(range => index > range.Start && index < range.End);

        // Named constructors: const name = new THREE.Type(args)
        foreach (Match match in NamedConstructor().Matches(code))
        {
            string name = match.Groups["var"].Value;
            string type = match.Groups["type"].Value;
            if (InsideIteration(match.Index))
            {
                inventory.Notes.Add($"'{name}' ({type}) is created inside a loop; the LLM pass reproduces the loop, the baseline omits it.");
                continue;
            }

            string args = ReadBalanced(code, match.Index + match.Length - 1);
            RegisterConstructor(name, type, args, inventory, constants, project);
        }

        // Meshes created inline: scene.add(new THREE.Mesh(new BoxGeometry(), material))
        foreach (Match match in AnonymousMesh().Matches(code))
        {
            if (IsNamedAssignment(code, match.Index) || InsideIteration(match.Index))
            {
                continue;
            }

            string args = ReadBalanced(code, match.Index + match.Length - 1);
            string name = $"mesh{++anonymous}";
            RegisterConstructor(name, "Mesh", args, inventory, constants, project);
            Match addedTo = AddedInline().Match(code[..match.Index].Length > 80 ? code[(match.Index - 80)..match.Index] : code[..match.Index]);
            if (addedTo.Success && inventory.Objects.FirstOrDefault(o => o.Var == name) is { } mesh)
            {
                mesh.Parent = addedTo.Groups["parent"].Value == "scene" ? null : addedTo.Groups["parent"].Value;
            }
        }

        foreach (Match match in VectorSetter().Matches(code))
        {
            SceneObjectDef? target = Find(inventory, match.Groups["var"].Value);
            if (target is null)
            {
                continue;
            }

            string[] parts = SplitArguments(ReadBalanced(code, match.Index + match.Length - 1));
            Vector3 value = ParseVector(parts, constants, match.Groups["prop"].Value == "scale" ? 1f : 0f, inventory);
            switch (match.Groups["prop"].Value)
            {
                case "position": target.Position = value; break;
                case "rotation": target.Rotation = value; break;
                case "scale": target.Scale = value; break;
            }
        }

        foreach (Match match in ComponentAssignment().Matches(code))
        {
            SceneObjectDef? target = Find(inventory, match.Groups["var"].Value);
            if (target is null || IsInsideLoop(code, match.Index))
            {
                continue;
            }

            float? number = EvaluateNumber(match.Groups["value"].Value, constants);
            if (number is null)
            {
                continue;
            }

            char axis = match.Groups["axis"].Value[0];
            switch (match.Groups["prop"].Value)
            {
                case "position": target.Position = WithAxis(target.Position ?? Vector3.Zero, axis, number.Value); break;
                case "rotation": target.Rotation = WithAxis(target.Rotation ?? Vector3.Zero, axis, number.Value); break;
                case "scale": target.Scale = WithAxis(target.Scale ?? Vector3.One, axis, number.Value); break;
            }
        }

        foreach (Match match in ScaleScalar().Matches(code))
        {
            if (Find(inventory, match.Groups["var"].Value) is { } target && EvaluateNumber(ReadBalanced(code, match.Index + match.Length - 1), constants) is { } s)
            {
                target.Scale = new Vector3(s);
            }
        }

        foreach (Match match in LookAtCall().Matches(code))
        {
            if (Find(inventory, match.Groups["var"].Value) is not { } target)
            {
                continue;
            }

            string[] parts = SplitArguments(ReadBalanced(code, match.Index + match.Length - 1));
            if (parts.Length >= 3)
            {
                target.LookAt = ParseVector(parts, constants, 0f, inventory);
            }
            else if (parts.Length == 1 && Find(inventory, parts[0].Trim()) is { Position: { } p })
            {
                target.LookAt = p;
            }
            else if (parts.Length == 1)
            {
                target.LookAt = Vector3.Zero;
            }
        }

        foreach (Match match in AddCall().Matches(code))
        {
            string parent = match.Groups["var"].Value;
            foreach (string child in SplitArguments(ReadBalanced(code, match.Index + match.Length - 1)))
            {
                if (Find(inventory, child.Trim()) is { } target && parent != "scene" && Find(inventory, parent) is not null && target.Var != parent)
                {
                    target.Parent = parent;
                }
            }
        }

        foreach (Match match in ModelLoad().Matches(code))
        {
            string path = NormalizeAssetPath(match.Groups["path"].Value, project);
            if (inventory.Objects.Any(o => o.ModelPath == path))
            {
                continue;
            }

            inventory.Objects.Add(new SceneObjectDef
            {
                Var = $"model{inventory.Objects.Count(o => o.Kind == SceneObjectKind.Model) + 1}",
                Kind = SceneObjectKind.Model,
                SourceType = Path.GetExtension(path).TrimStart('.').ToUpperInvariant() + "Loader",
                ModelPath = path,
            });
        }

        Match background = BackgroundColor().Match(code);
        if (background.Success && ParseColor(ReadBalanced(code, background.Index + background.Length - 1)) is { } bg)
        {
            inventory.Background = bg;
        }

        Match fog = FogConstructor().Match(code);
        if (fog.Success)
        {
            string[] parts = SplitArguments(ReadBalanced(code, fog.Index + fog.Length - 1));
            inventory.FogColor = parts.Length > 0 ? ParseColor(parts[0]) : null;
            if (fog.Groups["type"].Value == "FogExp2")
            {
                inventory.FogDensity = parts.Length > 1 ? EvaluateNumber(parts[1], constants) ?? 0.02f : 0.02f;
            }
            else
            {
                inventory.FogNear = parts.Length > 1 ? EvaluateNumber(parts[1], constants) ?? 1f : 1f;
                inventory.FogFar = parts.Length > 2 ? EvaluateNumber(parts[2], constants) ?? 100f : 100f;
                // Linear fog has no direct equivalent: pick an exp2 density that
                // is ~95% opaque at the far distance.
                inventory.FogDensity = MathF.Sqrt(3f) / MathF.Max(inventory.FogFar - inventory.FogNear, 1f);
                inventory.Notes.Add($"Linear fog ({inventory.FogNear}..{inventory.FogFar}) approximated with exponential squared fog.");
            }
        }

        Match toneMapping = ToneMappingAssignment().Match(code);
        if (toneMapping.Success)
        {
            inventory.ToneMapping = toneMapping.Groups["mode"].Value switch
            {
                "ACESFilmicToneMapping" or "AgXToneMapping" or "NeutralToneMapping" => "Aces",
                "ReinhardToneMapping" => "Reinhard",
                "CineonToneMapping" => "Filmic",
                "NoToneMapping" or "LinearToneMapping" => "None",
                _ => "Aces",
            };
        }

        Match exposure = ExposureAssignment().Match(code);
        if (exposure.Success && EvaluateNumber(exposure.Groups["value"].Value, constants) is { } e)
        {
            inventory.Exposure = e;
        }

        Match bloom = BloomPass().Match(code);
        if (bloom.Success)
        {
            string[] parts = SplitArguments(ReadBalanced(code, bloom.Index + bloom.Length - 1));
            inventory.Bloom = true;
            inventory.BloomStrength = parts.Length > 1 ? EvaluateNumber(parts[1], constants) ?? 1f : 1f;
            inventory.BloomThreshold = parts.Length > 3 ? EvaluateNumber(parts[3], constants) ?? 0.85f : 0.85f;
        }

        if (code.Contains("OrbitControls", StringComparison.Ordinal))
        {
            inventory.UsesOrbitControls = true;
            Match target = OrbitTarget().Match(code);
            if (target.Success)
            {
                inventory.OrbitTarget = ParseVector(SplitArguments(ReadBalanced(code, target.Index + target.Length - 1)), constants, 0f, inventory);
            }
        }

        ExtractAnimations(code, inventory, constants);
    }

    private static void RegisterConstructor(
        string name,
        string type,
        string args,
        SceneInventory inventory,
        Dictionary<string, string> constants,
        ThreeJsProject project)
    {
        string[] parts = SplitArguments(args);

        if (GeometryMap.TryGetValue(type, out (string Primitive, bool Approximated) geometry))
        {
            if (inventory.Geometries.Any(g => g.Var == name))
            {
                return;
            }

            inventory.Geometries.Add(new GeometryDef
            {
                Var = name,
                SourceType = type,
                Primitive = geometry.Primitive,
                Arguments = parts.Select(p => EvaluateNumber(p, constants) ?? float.NaN).ToArray(),
                Approximated = geometry.Approximated,
            });
            if (geometry.Approximated)
            {
                inventory.Notes.Add($"{type} '{name}' approximated with a {geometry.Primitive}.");
            }

            return;
        }

        if (MaterialMap.TryGetValue(type, out string? shading) || type is "ShaderMaterial" or "RawShaderMaterial")
        {
            if (inventory.Materials.Any(m => m.Var == name))
            {
                return;
            }

            MaterialDef material = new() { Var = name, SourceType = type, Shading = shading ?? "Pbr" };
            if (type is "MeshBasicMaterial")
            {
                material.Roughness = 1f;
            }

            ApplyMaterialLiteral(material, parts.FirstOrDefault() ?? string.Empty, inventory, constants, project);
            inventory.Materials.Add(material);
            return;
        }

        if (LightMap.TryGetValue(type, out string? lightType))
        {
            SceneObjectDef light = new() { Var = name, Kind = SceneObjectKind.Light, SourceType = type, LightType = lightType };
            light.LightColor = parts.Length > 0 ? ParseColor(parts[0]) ?? Vector3.One : Vector3.One;
            light.Intensity = parts.Length > 1 ? EvaluateNumber(parts[type == "HemisphereLight" ? 2 : 1], constants) ?? 1f : 1f;
            if (type is "PointLight" or "SpotLight" && parts.Length > 2)
            {
                light.Range = EvaluateNumber(parts[2], constants) ?? 0f;
            }

            if (type == "SpotLight")
            {
                light.Angle = parts.Length > 3 ? EvaluateNumber(parts[3], constants) ?? MathF.PI / 3f : MathF.PI / 3f;
                light.Penumbra = parts.Length > 4 ? EvaluateNumber(parts[4], constants) ?? 0f : 0f;
            }

            if (type == "HemisphereLight")
            {
                inventory.Notes.Add($"HemisphereLight '{name}' approximated with an ambient light.");
            }

            AddObject(inventory, light);
            return;
        }

        switch (type)
        {
            case "PerspectiveCamera":
                AddObject(inventory, new SceneObjectDef
                {
                    Var = name,
                    Kind = SceneObjectKind.Camera,
                    SourceType = type,
                    FovDegrees = parts.Length > 0 ? EvaluateNumber(parts[0], constants) ?? 50f : 50f,
                    Near = parts.Length > 2 ? EvaluateNumber(parts[2], constants) ?? 0.1f : 0.1f,
                    Far = parts.Length > 3 ? EvaluateNumber(parts[3], constants) ?? 2000f : 2000f,
                });
                break;

            case "OrthographicCamera":
                float top = parts.Length > 2 ? EvaluateNumber(parts[2], constants) ?? 5f : 5f;
                float bottom = parts.Length > 3 ? EvaluateNumber(parts[3], constants) ?? -5f : -5f;
                AddObject(inventory, new SceneObjectDef
                {
                    Var = name,
                    Kind = SceneObjectKind.Camera,
                    SourceType = type,
                    Perspective = false,
                    FovDegrees = MathF.Abs(top - bottom),
                    Near = parts.Length > 4 ? EvaluateNumber(parts[4], constants) ?? 0.1f : 0.1f,
                    Far = parts.Length > 5 ? EvaluateNumber(parts[5], constants) ?? 2000f : 2000f,
                });
                break;

            case "Group":
            case "Object3D":
                AddObject(inventory, new SceneObjectDef { Var = name, Kind = SceneObjectKind.Group, SourceType = type });
                break;

            case "Mesh":
            case "InstancedMesh":
            case "Points":
            {
                SceneObjectDef mesh = new() { Var = name, Kind = SceneObjectKind.Mesh, SourceType = type };
                if (parts.Length > 0)
                {
                    mesh.Geometry = ResolveInline(parts[0].Trim(), name, "Geometry", inventory, constants, project);
                }

                if (parts.Length > 1)
                {
                    mesh.Material = ResolveInline(parts[1].Trim(), name, "Material", inventory, constants, project);
                }

                if (type == "InstancedMesh")
                {
                    inventory.Notes.Add($"InstancedMesh '{name}' converted to a single mesh; instance transforms need a loop creating nodes.");
                }

                AddObject(inventory, mesh);
                break;
            }
        }
    }

    /// <summary>Resolves a Mesh argument that is either a variable or an inline constructor.</summary>
    private static string? ResolveInline(
        string argument,
        string owner,
        string suffix,
        SceneInventory inventory,
        Dictionary<string, string> constants,
        ThreeJsProject project)
    {
        Match inline = InlineConstructor().Match(argument);
        if (inline.Success)
        {
            string name = $"{owner}{suffix}";
            int open = argument.IndexOf('(', inline.Index);
            RegisterConstructor(name, inline.Groups["type"].Value, ReadBalanced(argument, open), inventory, constants, project);
            return inventory.Geometries.Any(g => g.Var == name) || inventory.Materials.Any(m => m.Var == name) ? name : null;
        }

        return IdentifierOnly().IsMatch(argument) ? argument : null;
    }

    private static void ApplyMaterialLiteral(MaterialDef material, string literal, SceneInventory inventory, Dictionary<string, string> constants, ThreeJsProject project)
    {
        foreach (Match property in ObjectProperty().Matches(literal))
        {
            string key = property.Groups["key"].Value;
            string value = property.Groups["value"].Value.Trim();
            switch (key)
            {
                case "color":
                    if (ParseColor(value) is { } color)
                    {
                        material.Color = new Vector4(color, material.Color.W);
                    }

                    break;
                case "emissive":
                    material.Emissive = ParseColor(value) ?? Vector3.Zero;
                    break;
                case "emissiveIntensity":
                    material.EmissiveIntensity = EvaluateNumber(value, constants) ?? 1f;
                    break;
                case "metalness":
                    material.Metalness = EvaluateNumber(value, constants) ?? 0f;
                    break;
                case "roughness":
                    material.Roughness = EvaluateNumber(value, constants) ?? 1f;
                    break;
                case "shininess":
                    material.Shininess = EvaluateNumber(value, constants) ?? 30f;
                    break;
                case "opacity":
                    material.Color = material.Color with { W = EvaluateNumber(value, constants) ?? 1f };
                    break;
                case "transparent":
                    material.Transparent = value == "true";
                    break;
                case "wireframe":
                    material.Wireframe = value == "true";
                    break;
                case "side":
                    material.DoubleSided = value.Contains("DoubleSide", StringComparison.Ordinal);
                    break;
                case "map":
                case "normalMap":
                case "roughnessMap":
                case "emissiveMap":
                    string? texture = ResolveTexture(value, inventory, project);
                    if (key == "map") material.Map = texture;
                    else if (key == "normalMap") material.NormalMap = texture;
                    else if (key == "roughnessMap") material.RoughnessMap = texture;
                    else material.EmissiveMap = texture;
                    break;
            }
        }
    }

    private static string? ResolveTexture(string value, SceneInventory inventory, ThreeJsProject project)
    {
        Match load = InlineLoad().Match(value);
        if (load.Success)
        {
            string path = NormalizeAssetPath(load.Groups["path"].Value, project);
            string name = $"texture{inventory.Textures.Count + 1}";
            inventory.Textures[name] = path;
            return name;
        }

        return inventory.Textures.ContainsKey(value) ? value : null;
    }

    private static void ExtractAnimations(string code, SceneInventory inventory, Dictionary<string, string> constants)
    {
        foreach (Match match in IncrementAssignment().Matches(code))
        {
            if (!IsInsideLoop(code, match.Index))
            {
                continue;
            }

            string target = match.Groups["var"].Value;
            if (Find(inventory, target) is null)
            {
                continue;
            }

            string value = match.Groups["value"].Value;
            float sign = match.Groups["op"].Value == "-" ? -1f : 1f;
            bool timeScaled = DeltaReference().IsMatch(value);
            string numeric = DeltaReference().Replace(value, "1");
            if (EvaluateNumber(numeric, constants) is not { } amount)
            {
                inventory.Notes.Add($"Animation of {target}.{match.Groups["prop"].Value}.{match.Groups["axis"].Value} uses '{value.Trim()}', which needs the LLM pass.");
                continue;
            }

            // Frame based increments assume the browser's usual 60 Hz.
            float perSecond = sign * amount * (timeScaled ? 1f : 60f);
            inventory.Animations.Add(new AnimationDef(target, match.Groups["prop"].Value, match.Groups["axis"].Value[0], perSecond));
        }
    }

    // --------------------------------------------------------------- helpers

    private static void AddObject(SceneInventory inventory, SceneObjectDef definition)
    {
        if (inventory.Objects.All(o => o.Var != definition.Var))
        {
            inventory.Objects.Add(definition);
        }
    }

    private static SceneObjectDef? Find(SceneInventory inventory, string name) =>
        inventory.Objects.FirstOrDefault(o => o.Var == name);

    private static bool IsNamedAssignment(string code, int index)
    {
        int lineStart = code.LastIndexOf('\n', Math.Max(index - 1, 0)) + 1;
        return NamedPrefix().IsMatch(code[lineStart..index]);
    }

    /// <summary>Character ranges of for / while loop bodies and forEach / map callbacks.</summary>
    private static List<(int Start, int End)> IterationRanges(string code)
    {
        List<(int Start, int End)> ranges = [];
        foreach (Match match in IterationHeader().Matches(code))
        {
            int open = code.IndexOf('(', match.Index);
            if (open < 0)
            {
                continue;
            }

            int close = FindClosing(code, open, '(', ')');
            if (match.Value.StartsWith('.'))
            {
                // Callback style: the whole argument list is the body.
                ranges.Add((open, close));
                continue;
            }

            int body = close + 1;
            while (body < code.Length && char.IsWhiteSpace(code[body]))
            {
                body++;
            }

            if (body < code.Length && code[body] == '{')
            {
                ranges.Add((body, FindClosing(code, body, '{', '}')));
            }
        }

        return ranges;
    }

    /// <summary>True when the offset sits inside the render loop function body.</summary>
    private static bool IsInsideLoop(string code, int index)
    {
        foreach (Match loop in LoopFunction().Matches(code))
        {
            int open = code.IndexOf('{', loop.Index + loop.Length - 1);
            if (open < 0 || open > index)
            {
                continue;
            }

            int close = FindClosing(code, open, '{', '}');
            if (index > open && index < close)
            {
                return true;
            }
        }

        return false;
    }

    private static void CollectConstants(string code, Dictionary<string, string> constants)
    {
        foreach (Match match in NumericConstant().Matches(code))
        {
            constants[match.Groups["name"].Value] = match.Groups["value"].Value;
        }
    }

    /// <summary>Returns the text between a '(' at <paramref name="openIndex"/> and its matching ')'.</summary>
    public static string ReadBalanced(string text, int openIndex)
    {
        if (openIndex < 0 || openIndex >= text.Length || text[openIndex] != '(')
        {
            return string.Empty;
        }

        int close = FindClosing(text, openIndex, '(', ')');
        return close > openIndex ? text[(openIndex + 1)..close] : text[(openIndex + 1)..];
    }

    private static int FindClosing(string text, int openIndex, char open, char close)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = openIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'' or '`')
            {
                quote = c;
            }
            else if (c == open)
            {
                depth++;
            }
            else if (c == close && --depth == 0)
            {
                return i;
            }
        }

        return text.Length;
    }

    /// <summary>Splits a call's argument list on top level commas.</summary>
    public static string[] SplitArguments(string arguments)
    {
        List<string> parts = [];
        StringBuilder current = new();
        int depth = 0;
        char quote = '\0';

        foreach (char c in arguments)
        {
            if (quote != '\0')
            {
                current.Append(c);
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'' or '`':
                    quote = c;
                    current.Append(c);
                    break;
                case '(' or '[' or '{':
                    depth++;
                    current.Append(c);
                    break;
                case ')' or ']' or '}':
                    depth--;
                    current.Append(c);
                    break;
                case ',' when depth == 0:
                    parts.Add(current.ToString().Trim());
                    current.Clear();
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        if (current.ToString().Trim().Length > 0)
        {
            parts.Add(current.ToString().Trim());
        }

        return [.. parts];
    }

    /// <summary>Evaluates simple numeric JS expressions (Math.PI / 2, 0.5 * 3, named constants).</summary>
    public static float? EvaluateNumber(string expression, IReadOnlyDictionary<string, string>? constants = null)
    {
        string text = expression.Trim().TrimEnd(';');
        if (text.Length == 0)
        {
            return null;
        }

        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float direct))
        {
            return direct;
        }

        if (constants is not null)
        {
            // One level of constant substitution covers `const SIZE = 2; new BoxGeometry(SIZE, ...)`.
            text = Identifier().Replace(text, m => constants.TryGetValue(m.Value, out string? v) ? $"({v})" : m.Value);
        }

        text = text
            .Replace("Math.PI", "pi", StringComparison.Ordinal)
            .Replace("THREE.MathUtils.degToRad", "rad", StringComparison.Ordinal)
            .Replace("MathUtils.degToRad", "rad", StringComparison.Ordinal)
            .Replace("Math.", string.Empty, StringComparison.Ordinal)
            .Replace("**", "^", StringComparison.Ordinal);

        try
        {
            return (float)ExpressionEvaluator.Evaluate(text);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Parses 0xRRGGBB, '#rrggbb', '#rgb', CSS names, 'rgb(r,g,b)' and new Color(...).</summary>
    public static Vector3? ParseColor(string value)
    {
        string text = value.Trim();
        Match wrapped = ColorConstructor().Match(text);
        if (wrapped.Success)
        {
            text = ReadBalanced(text, text.IndexOf('(', wrapped.Index));
            string[] rgb = SplitArguments(text);
            if (rgb.Length == 3 && rgb.All(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                return new Vector3(
                    float.Parse(rgb[0], CultureInfo.InvariantCulture),
                    float.Parse(rgb[1], CultureInfo.InvariantCulture),
                    float.Parse(rgb[2], CultureInfo.InvariantCulture));
            }
        }

        text = text.Trim('\'', '"', '`', ' ');
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
        {
            return FromSrgbHex(hex);
        }

        if (text.StartsWith('#'))
        {
            string digits = text[1..];
            if (digits.Length == 3)
            {
                digits = string.Concat(digits.Select(c => $"{c}{c}"));
            }

            if (uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint css))
            {
                return FromSrgbHex(css);
            }
        }

        if (NamedColors.TryGetValue(text, out Vector3 named))
        {
            return new Vector3(ThreeNet.MathHelpers.SrgbToLinear(named.X), ThreeNet.MathHelpers.SrgbToLinear(named.Y), ThreeNet.MathHelpers.SrgbToLinear(named.Z));
        }

        Match rgbFunction = RgbFunction().Match(text);
        if (rgbFunction.Success)
        {
            return new Vector3(
                ThreeNet.MathHelpers.SrgbToLinear(float.Parse(rgbFunction.Groups["r"].Value, CultureInfo.InvariantCulture) / 255f),
                ThreeNet.MathHelpers.SrgbToLinear(float.Parse(rgbFunction.Groups["g"].Value, CultureInfo.InvariantCulture) / 255f),
                ThreeNet.MathHelpers.SrgbToLinear(float.Parse(rgbFunction.Groups["b"].Value, CultureInfo.InvariantCulture) / 255f));
        }

        if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint integer))
        {
            return FromSrgbHex(integer);
        }

        return null;
    }

    /// <summary>Three.js (r152+) treats hex colours as sRGB and converts them to linear.</summary>
    private static Vector3 FromSrgbHex(uint hex)
    {
        Vector4 linear = ThreeNet.MathHelpers.FromHex(hex);
        return new Vector3(linear.X, linear.Y, linear.Z);
    }

    private static Vector3 ParseVector(string[] parts, Dictionary<string, string> constants, float fallback, SceneInventory inventory)
    {
        float Component(int index)
        {
            if (index >= parts.Length)
            {
                return fallback;
            }

            float? value = EvaluateNumber(parts[index], constants);
            if (value is null)
            {
                inventory.Notes.Add($"Could not evaluate '{parts[index]}'; used {fallback}.");
            }

            return value ?? fallback;
        }

        return new Vector3(Component(0), Component(1), Component(2));
    }

    private static Vector3 WithAxis(Vector3 vector, char axis, float value) => axis switch
    {
        'x' => vector with { X = value },
        'y' => vector with { Y = value },
        _ => vector with { Z = value },
    };

    /// <summary>Makes an asset path relative to the project root, as it will be copied.</summary>
    private static string NormalizeAssetPath(string path, ThreeJsProject project)
    {
        string trimmed = path.Replace('\\', '/').TrimStart('.', '/');
        AssetFile? match = project.Assets.FirstOrDefault(a => a.RelativePath.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                           ?? project.Assets.FirstOrDefault(a => a.RelativePath.EndsWith(trimmed, StringComparison.OrdinalIgnoreCase))
                           ?? project.Assets.FirstOrDefault(a => Path.GetFileName(a.RelativePath).Equals(Path.GetFileName(trimmed), StringComparison.OrdinalIgnoreCase));
        return match?.RelativePath ?? trimmed;
    }

    private static string StripComments(string code)
    {
        // Keeps string contents intact while dropping // and /* */ comments.
        StringBuilder builder = new(code.Length);
        char quote = '\0';
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (quote != '\0')
            {
                builder.Append(c);
                if (c == '\\' && i + 1 < code.Length)
                {
                    builder.Append(code[++i]);
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'' or '`')
            {
                quote = c;
                builder.Append(c);
            }
            else if (c == '/' && i + 1 < code.Length && code[i + 1] == '/' && (i == 0 || code[i - 1] != ':'))
            {
                while (i < code.Length && code[i] != '\n')
                {
                    i++;
                }

                builder.Append('\n');
            }
            else if (c == '/' && i + 1 < code.Length && code[i + 1] == '*')
            {
                int end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? code.Length : end + 1;
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"(?:const|let|var)\s+(?<var>[A-Za-z_$][\w$]*)\s*=\s*new\s+(?:THREE\.)?(?<type>[A-Z]\w*)\s*\(")]
    private static partial Regex NamedConstructor();

    [GeneratedRegex(@"\b(?:for|while)\s*\(|\.(?:forEach|map)\s*\(")]
    private static partial Regex IterationHeader();

    [GeneratedRegex(@"\b(?<var>[A-Za-z_$][\w$]*)\.repeat\.set\s*\(")]
    private static partial Regex TextureRepeat();

    [GeneratedRegex(@"new\s+(?:THREE\.)?(?:Mesh|InstancedMesh)\s*\(")]
    private static partial Regex AnonymousMesh();

    [GeneratedRegex(@"(?<parent>[A-Za-z_$][\w$]*)\.add\(\s*$")]
    private static partial Regex AddedInline();

    [GeneratedRegex(@"(?:const|let|var)\s+[\w$]+\s*=\s*$")]
    private static partial Regex NamedPrefix();

    [GeneratedRegex(@"^new\s+(?:THREE\.)?(?<type>[A-Z]\w*)\s*\(")]
    private static partial Regex InlineConstructor();

    [GeneratedRegex(@"^[A-Za-z_$][\w$]*$")]
    private static partial Regex IdentifierOnly();

    [GeneratedRegex(@"\b(?<var>[A-Za-z_$][\w$]*)\.(?<prop>position|rotation|scale)\.set\s*\(")]
    private static partial Regex VectorSetter();

    [GeneratedRegex(@"\b(?<var>[A-Za-z_$][\w$]*)\.(?<prop>position|rotation|scale)\.(?<axis>[xyz])\s*=\s*(?<value>[^;\n]+)")]
    private static partial Regex ComponentAssignment();

    [GeneratedRegex(@"\b(?<var>[A-Za-z_$][\w$]*)\.(?<prop>position|rotation)\.(?<axis>[xyz])\s*(?<op>[+\-])=\s*(?<value>[^;\n]+)")]
    private static partial Regex IncrementAssignment();

    [GeneratedRegex(@"\b(?<var>[A-Za-z_$][\w$]*)\.scale\.setScalar\s*\(")]
    private static partial Regex ScaleScalar();

    [GeneratedRegex(@"\b(?<var>[A-Za-z_$][\w$]*)\.lookAt\s*\(")]
    private static partial Regex LookAtCall();

    [GeneratedRegex(@"\b(?<var>[A-Za-z_$][\w$]*)\.add\s*\(")]
    private static partial Regex AddCall();

    [GeneratedRegex(@"(?:const|let|var)\s+(?<var>[A-Za-z_$][\w$]*)\s*=\s*(?:new\s+(?:THREE\.)?TextureLoader\s*\(\s*\)|[\w$]+)\s*\.load(?:Async)?\s*\(\s*['""`](?<path>[^'""`]+\.(?:png|jpe?g|webp|bmp|tga|hdr|ktx2))['""`]", RegexOptions.IgnoreCase)]
    private static partial Regex TextureAssignment();

    [GeneratedRegex(@"\.load(?:Async)?\s*\(\s*['""`](?<path>[^'""`]+\.(?:png|jpe?g|webp|bmp|tga|hdr|ktx2))['""`]", RegexOptions.IgnoreCase)]
    private static partial Regex InlineLoad();

    [GeneratedRegex(@"\.load(?:Async)?\s*\(\s*['""`](?<path>[^'""`]+\.(?:glb|gltf|obj))['""`]", RegexOptions.IgnoreCase)]
    private static partial Regex ModelLoad();

    [GeneratedRegex(@"(?<key>[A-Za-z_$][\w$]*)\s*:\s*(?<value>(?:new\s+[\w.]+\s*\([^)]*\)|[\w$.]+\s*\.\s*load\s*\([^)]*\)|'[^']*'|""[^""]*""|[^,}\n]+))")]
    private static partial Regex ObjectProperty();

    [GeneratedRegex(@"\.background\s*=\s*new\s+(?:THREE\.)?Color\s*\(")]
    private static partial Regex BackgroundColor();

    [GeneratedRegex(@"\.fog\s*=\s*new\s+(?:THREE\.)?(?<type>FogExp2|Fog)\s*\(")]
    private static partial Regex FogConstructor();

    [GeneratedRegex(@"toneMapping\s*=\s*(?:THREE\.)?(?<mode>\w+ToneMapping)")]
    private static partial Regex ToneMappingAssignment();

    [GeneratedRegex(@"toneMappingExposure\s*=\s*(?<value>[^;\n]+)")]
    private static partial Regex ExposureAssignment();

    [GeneratedRegex(@"new\s+(?:THREE\.)?UnrealBloomPass\s*\(")]
    private static partial Regex BloomPass();

    [GeneratedRegex(@"\.target\.set\s*\(")]
    private static partial Regex OrbitTarget();

    [GeneratedRegex(@"(?:function\s+(?:animate|render|loop|tick|update)\s*\([^)]*\)|setAnimationLoop\s*\(\s*(?:\([^)]*\)|\w+)\s*=>|(?:const|let)\s+(?:animate|render|loop|tick|update)\s*=\s*\([^)]*\)\s*=>)\s*")]
    private static partial Regex LoopFunction();

    [GeneratedRegex(@"\b(?:delta|dt|deltaTime|elapsed|clock\.getDelta\(\))\b")]
    private static partial Regex DeltaReference();

    [GeneratedRegex(@"(?:const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)\s*=\s*(?<value>-?[\d.]+(?:\s*[*/+\-]\s*(?:[\d.]+|Math\.PI))*)\s*;?\s*$", RegexOptions.Multiline)]
    private static partial Regex NumericConstant();

    [GeneratedRegex(@"[A-Za-z_$][\w$]*")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"^new\s+(?:THREE\.)?Color\s*\(")]
    private static partial Regex ColorConstructor();

    [GeneratedRegex(@"rgb\(\s*(?<r>\d+)\s*,\s*(?<g>\d+)\s*,\s*(?<b>\d+)\s*\)")]
    private static partial Regex RgbFunction();
}
