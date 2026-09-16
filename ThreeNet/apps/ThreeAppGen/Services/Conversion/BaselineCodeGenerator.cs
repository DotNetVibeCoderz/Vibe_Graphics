using System.Globalization;
using System.Numerics;
using System.Text;

namespace ThreeAppGen.Services.Conversion;

/// <summary>
/// Turns a <see cref="SceneInventory"/> into C# that always compiles. The
/// result is the safety net of the conversion: the LLM pass improves on it,
/// and if that pass cannot be made to build, this is what ships.
/// </summary>
public static class BaselineCodeGenerator
{
    /// <summary>Files that belong to the conversion contract and are never replaced by the LLM.</summary>
    public static readonly string[] ProtectedFiles = ["ThreeJsCompat.cs", "OrbitRig.cs"];

    public static string GenerateConvertedScene(string rootNamespace, SceneInventory inventory, ThreeJsProject project)
    {
        StringBuilder fields = new();
        StringBuilder build = new();
        StringBuilder update = new();
        Dictionary<string, string> identifiers = new(StringComparer.Ordinal);
        HashSet<string> usedNames = new(StringComparer.Ordinal);

        string Id(string jsName)
        {
            if (identifiers.TryGetValue(jsName, out string? existing))
            {
                return existing;
            }

            string candidate = "_" + ToCamelIdentifier(jsName);
            string unique = candidate;
            for (int i = 2; !usedNames.Add(unique); i++)
            {
                unique = candidate + i.ToString(CultureInfo.InvariantCulture);
            }

            identifiers[jsName] = unique;
            return unique;
        }

        // --- environment -------------------------------------------------------
        Vector3 background = inventory.Background ?? Vector3.Zero;
        build.AppendLine("        Scene.Environment = SceneEnvironment.Default with");
        build.AppendLine("        {");
        build.AppendLine($"            Background = new Vector4({V3(background)}, 1f),");
        build.AppendLine("            // Three.js adds no ambient light unless the scene creates one.");
        build.AppendLine("            AmbientIntensity = 0f,");
        if (inventory.FogColor is { } fogColor && inventory.FogDensity > 0f)
        {
            build.AppendLine($"            FogColor = new Vector3({V3(fogColor)}),");
            build.AppendLine($"            FogDensity = {F(inventory.FogDensity)},");
            build.AppendLine($"            FogStart = {F(inventory.FogNear)},");
        }

        build.AppendLine("        };");
        build.AppendLine();

        // --- textures ----------------------------------------------------------
        HashSet<string> linearTextures = inventory.Materials
            .SelectMany(m => new[] { m.NormalMap, m.RoughnessMap })
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        foreach ((string name, string path) in inventory.Textures)
        {
            string id = Id(name);
            fields.AppendLine($"    private readonly Texture? {id};");
            bool srgb = !linearTextures.Contains(name);
            build.AppendLine($"        {id} = ThreeJsCompat.TryLoadTexture(Scene, _assetsRoot, {S(path)}, srgb: {(srgb ? "true" : "false")});");
        }

        if (inventory.Textures.Count > 0)
        {
            build.AppendLine();
        }

        // --- geometries --------------------------------------------------------
        foreach (GeometryDef geometry in inventory.Geometries)
        {
            string id = Id(geometry.Var);
            fields.AppendLine($"    private readonly Geometry {id};");
            build.AppendLine($"        // {geometry.SourceType}{(geometry.Approximated ? " (approximated)" : string.Empty)}");
            build.AppendLine($"        {id} = {GeometryCall(geometry)};");
        }

        if (inventory.Geometries.Count > 0)
        {
            build.AppendLine();
        }

        // --- materials ---------------------------------------------------------
        foreach (MaterialDef material in inventory.Materials)
        {
            string id = Id(material.Var);
            fields.AppendLine($"    private readonly Material {id};");
            build.AppendLine($"        // {material.SourceType}");
            build.AppendLine($"        {id} = Scene.CreateMaterial({MaterialOptionsExpression(material, Id, inventory)});");
        }

        if (inventory.Materials.Count > 0)
        {
            build.AppendLine();
        }

        // --- nodes, parents before children ------------------------------------
        List<SceneObjectDef> ordered = OrderByHierarchy(inventory.Objects);
        SceneObjectDef? camera = inventory.Camera;
        string fallbackGeometry = string.Empty;
        string fallbackMaterial = string.Empty;

        foreach (SceneObjectDef item in ordered)
        {
            string id = Id(item.Var);
            string parentArgument = item.Parent is not null && identifiers.ContainsKey(item.Parent) ? identifiers[item.Parent] : "null";

            switch (item.Kind)
            {
                case SceneObjectKind.Group:
                    fields.AppendLine($"    private readonly Node {id};");
                    build.AppendLine($"        {id} = Scene.CreateNode({parentArgument}, {S(item.Var)});");
                    break;

                case SceneObjectKind.Mesh:
                {
                    string geometry = item.Geometry is not null && inventory.Geometries.Any(g => g.Var == item.Geometry)
                        ? identifiers[item.Geometry]
                        : EnsureFallback(ref fallbackGeometry, fields, build, "_fallbackGeometry", "Scene.CreateBoxGeometry()");
                    string material = item.Material is not null && inventory.Materials.Any(m => m.Var == item.Material)
                        ? identifiers[item.Material]
                        : EnsureFallback(ref fallbackMaterial, fields, build, "_fallbackMaterial", "Scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 0f, 1f))");
                    fields.AppendLine($"    private readonly Node {id};");
                    build.AppendLine($"        {id} = Scene.AddMesh({geometry}, {material}, {parentArgument}, {S(item.Var)});");
                    break;
                }

                case SceneObjectKind.Light:
                    fields.AppendLine($"    private readonly Node {id};");
                    build.AppendLine($"        {id} = Scene.AddLight({LightExpression(item)}, {parentArgument}, {S(item.Var)});");
                    break;

                case SceneObjectKind.Camera when item == camera:
                    build.AppendLine(item.Perspective
                        ? $"        Camera = Scene.AddCamera(ThreeNet.Camera.Perspective({F(item.FovDegrees)} * (MathF.PI / 180f), {F(item.Near)}, {F(item.Far)}), default, {parentArgument});"
                        : $"        Camera = Scene.AddCamera(ThreeNet.Camera.Orthographic({F(item.FovDegrees)}, {F(item.Near)}, {F(item.Far)}), default, {parentArgument});");
                    identifiers[item.Var] = "Camera";
                    id = "Camera";
                    break;

                case SceneObjectKind.Camera:
                    continue;

                case SceneObjectKind.Model:
                    fields.AppendLine($"    private readonly Node? {id};");
                    build.AppendLine($"        {id} = ThreeJsCompat.TryLoadModel(Scene, _assetsRoot, {S(item.ModelPath ?? string.Empty)}, {parentArgument});");
                    break;
            }

            // Loaded models may be missing at runtime, so their transforms are guarded.
            string setter = item.Kind == SceneObjectKind.Model ? $"if ({id} is not null) {{ " : string.Empty;
            string setterEnd = item.Kind == SceneObjectKind.Model ? " }" : string.Empty;

            if (item.Position is { } position)
            {
                build.AppendLine($"        {setter}{id}.Position = new Vector3({V3(position)});{setterEnd}");
            }

            if (item.Rotation is { } rotation)
            {
                build.AppendLine($"        {setter}{id}.Rotation = ThreeJsCompat.EulerXyz({V3(rotation)});{setterEnd}");
            }

            if (item.Scale is { } scale)
            {
                build.AppendLine($"        {setter}{id}.Scale = new Vector3({V3(scale)});{setterEnd}");
            }

            if (item.LookAt is { } lookAt)
            {
                build.AppendLine($"        {setter}{id}.LookAt(new Vector3({V3(lookAt)}));{setterEnd}");
            }
            else if (item.Kind == SceneObjectKind.Light && item.LightType is "Directional" or "Spot")
            {
                // Three.js directional and spot lights aim at their target, (0, 0, 0) by default.
                build.AppendLine($"        {id}.LookAt(Vector3.Zero);");
            }

            // Three.js meshes cast and receive nothing unless asked; Three.Net
            // defaults to both, so the flags are written out explicitly.
            if (inventory.Shadows && item.Kind == SceneObjectKind.Mesh)
            {
                build.AppendLine($"        {id}.CastShadow = {(item.CastShadow ? "true" : "false")};");
                build.AppendLine($"        {id}.ReceiveShadow = {(item.ReceiveShadow ? "true" : "false")};");
            }

            build.AppendLine();
        }

        if (camera is null)
        {
            build.AppendLine("        // No camera was found in the source: a default one frames the origin.");
            build.AppendLine("        Camera = Scene.AddCamera(ThreeNet.Camera.Perspective(50f * (MathF.PI / 180f), 0.1f, 2000f), new Vector3(0f, 2f, 6f));");
            build.AppendLine("        Camera.LookAt(Vector3.Zero);");
        }
        else if (camera.LookAt is null)
        {
            Vector3 target = inventory.OrbitTarget ?? Vector3.Zero;
            build.AppendLine($"        Camera.LookAt(new Vector3({V3(target)}));");
        }

        // --- animations --------------------------------------------------------
        // Rotations accumulate in an Euler field per node (Three.js semantics),
        // applied once per frame after every axis has been advanced.
        foreach (IGrouping<string, AnimationDef> group in inventory.Animations.GroupBy(a => a.Target))
        {
            if (!identifiers.TryGetValue(group.Key, out string? id) || id == "Camera")
            {
                continue;
            }

            SceneObjectDef? target = inventory.Objects.FirstOrDefault(o => o.Var == group.Key);
            List<AnimationDef> rotations = group.Where(a => a.Property == "rotation").ToList();
            if (rotations.Count > 0)
            {
                string state = $"{id}Euler";
                fields.AppendLine($"    private Vector3 {state} = new({V3(target?.Rotation ?? Vector3.Zero)});");
                foreach (AnimationDef rotation in rotations)
                {
                    update.AppendLine($"        {state}.{char.ToUpperInvariant(rotation.Axis)} += {F(rotation.PerSecond)} * deltaSeconds;");
                }

                update.AppendLine($"        {id}.Rotation = ThreeJsCompat.EulerXyz({state}.X, {state}.Y, {state}.Z);");
            }

            foreach (AnimationDef movement in group.Where(a => a.Property == "position"))
            {
                string axis = movement.Axis switch { 'x' => "UnitX", 'y' => "UnitY", _ => "UnitZ" };
                update.AppendLine($"        {id}.Position += Vector3.{axis} * ({F(movement.PerSecond)} * deltaSeconds);");
            }
        }

        if (update.Length == 0)
        {
            update.AppendLine("        // The source animation loop only rendered; nothing to animate.");
        }

        string rendererOptions = $"""
            RendererOptions.Default with
                {"{"}
                    MsaaSamples = {(inventory.Antialias ? 4 : 1)},
                    ToneMapping = ToneMapping.{inventory.ToneMapping},
                    Exposure = {F(inventory.Exposure)},
                    Bloom = {(inventory.Bloom ? "true" : "false")},
                    BloomIntensity = {F(inventory.BloomStrength)},
                    BloomThreshold = {F(inventory.BloomThreshold)},
                    Shadows = {(inventory.Shadows ? "true" : "false")},
                {"}"}
            """;

        Vector3 orbitTarget = inventory.OrbitTarget ?? Vector3.Zero;
        string source = project.EntryScript?.RelativePath ?? "(no entry script)";

        return $$"""
            // <auto-generated>
            //   Converted from the Three.js project '{{project.SuggestedName}}' ({{source}})
            //   by Three.Net App Generator. Baseline pass: deterministic mapping of the scene inventory.
            // </auto-generated>

            using System.Numerics;
            using ThreeNet;

            namespace {{rootNamespace}}.Core;

            /// <summary>
            /// The converted Three.js scene. Hosts (desktop window, Avalonia preview, browser,
            /// Android) create it, call <see cref="Update"/> every frame and render
            /// <see cref="Scene"/> from <see cref="Camera"/>.
            /// </summary>
            public sealed partial class ConvertedScene : IDisposable
            {
                private readonly string _assetsRoot;
            {{fields.ToString().TrimEnd()}}

                public ConvertedScene(string assetsRoot)
                {
                    _assetsRoot = assetsRoot;
                    Scene = new Scene();

            {{build.ToString().TrimEnd()}}
                }

                /// <summary>The scene graph to render.</summary>
                public Scene Scene { get; }

                /// <summary>Camera node used by the hosts.</summary>
                public Node Camera { get; }

                /// <summary>Renderer settings matching the source WebGLRenderer.</summary>
                public RendererOptions RendererOptions { get; } = {{rendererOptions}};

                /// <summary>True when the source used OrbitControls, so hosts enable mouse orbiting.</summary>
                public bool UsesOrbitControls => {{(inventory.UsesOrbitControls ? "true" : "false")}};

                /// <summary>Point the orbit controls circle around.</summary>
                public Vector3 OrbitTarget => new({{V3(orbitTarget)}});

                /// <summary>Per frame logic, the equivalent of the requestAnimationFrame loop body.</summary>
                public void Update(float deltaSeconds, double totalSeconds)
                {
            {{update.ToString().TrimEnd()}}
                }

                /// <summary>Keyboard, pointer and resize events forwarded by the host.</summary>
                public void OnInput(InputEvent input)
                {
                }

                public void Dispose() => Scene.Dispose();
            }
            """;
    }

    /// <summary>ThreeJsCompat.cs: colours, Euler order, asset loading and the extra Three.js geometries.</summary>
    public static string GenerateCompat(string rootNamespace) => LoadTemplate("ThreeJsCompat.cs.template", rootNamespace);

    /// <summary>OrbitRig.cs: OrbitControls for the native desktop host.</summary>
    public static string GenerateOrbitRig(string rootNamespace) => LoadTemplate("OrbitRig.cs.template", rootNamespace);

    /// <summary>Templates are embedded real C# files, so they stay readable and reviewable.</summary>
    private static string LoadTemplate(string fileName, string rootNamespace)
    {
        System.Reflection.Assembly assembly = typeof(BaselineCodeGenerator).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(fileName, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)!;
        using StreamReader reader = new(stream);
        return reader.ReadToEnd().Replace("__ROOT_NAMESPACE__", rootNamespace, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------- pieces

    private static string EnsureFallback(ref string name, StringBuilder fields, StringBuilder build, string field, string expression)
    {
        if (name.Length == 0)
        {
            name = field;
            fields.AppendLine($"    private readonly {(field.Contains("Geometry") ? "Geometry" : "Material")} {field};");
            build.AppendLine($"        // The source built this resource in a way the static pass could not read.");
            build.AppendLine($"        {field} = {expression};");
        }

        return name;
    }

    private static string GeometryCall(GeometryDef geometry)
    {
        float Arg(int index, float fallback) =>
            index < geometry.Arguments.Length && !float.IsNaN(geometry.Arguments[index]) ? geometry.Arguments[index] : fallback;

        return (geometry.SourceType, geometry.Primitive) switch
        {
            (_, "Box") => $"Scene.CreateBoxGeometry({F(Arg(0, 1))}, {F(Arg(1, 1))}, {F(Arg(2, 1))})",
            ("CircleGeometry", _) => $"ThreeJsCompat.CreateCircleGeometry(Scene, {F(Arg(0, 1))}, {Int(Arg(1, 32))})",
            ("CapsuleGeometry", _) => $"ThreeJsCompat.CreateCapsuleGeometry(Scene, {F(Arg(0, 1))}, {F(Arg(1, 1))}, {Int(Arg(2, 4))}, {Int(Arg(3, 8))})",
            ("RingGeometry", _) => $"ThreeJsCompat.CreateRingGeometry(Scene, {F(Arg(0, 0.5f))}, {F(Arg(1, 1))}, {Int(Arg(2, 32))})",
            ("TorusKnotGeometry", _) => $"ThreeJsCompat.CreateTorusKnotGeometry(Scene, {F(Arg(0, 1))}, {F(Arg(1, 0.4f))}, {Int(Arg(2, 64))}, {Int(Arg(3, 8))}, {Int(Arg(4, 2))}, {Int(Arg(5, 3))})",
            ("TetrahedronGeometry" or "OctahedronGeometry" or "IcosahedronGeometry", _) =>
                $"ThreeJsCompat.CreatePolyhedronGeometry(Scene, \"{geometry.SourceType.Replace("Geometry", string.Empty).ToLowerInvariant()}\", {F(Arg(0, 1))})",
            (_, "Sphere") when geometry.SourceType is "DodecahedronGeometry" =>
                $"Scene.CreateSphereGeometry({F(Arg(0, 1))}, 24, 16)",
            (_, "Sphere") => $"Scene.CreateSphereGeometry({F(Arg(0, 1))}, {Int(Arg(1, 32))}, {Int(Arg(2, 16))})",
            (_, "Plane") => $"Scene.CreatePlaneGeometry({F(Arg(0, 1))}, {F(Arg(1, 1))}, {Int(Arg(2, 1))}, {Int(Arg(3, 1))})",
            (_, "Cylinder") => $"Scene.CreateCylinderGeometry({F(Arg(0, 1))}, {F(Arg(1, 1))}, {F(Arg(2, 1))}, {Int(Arg(3, 32))}, {Int(Arg(4, 1))})",
            (_, "Cone") => $"Scene.CreateConeGeometry({F(Arg(0, 1))}, {F(Arg(1, 1))}, {Int(Arg(2, 32))})",
            (_, "Torus") => $"Scene.CreateTorusGeometry({F(Arg(0, 1))}, {F(Arg(1, 0.4f))}, {Int(Arg(2, 12))}, {Int(Arg(3, 48))}, {F(Arg(4, MathF.Tau))})",
            _ => "Scene.CreateBoxGeometry()",
        };
    }

    private static string MaterialOptionsExpression(MaterialDef material, Func<string, string> id, SceneInventory inventory)
    {
        string color = $"new Vector4({V3(new Vector3(material.Color.X, material.Color.Y, material.Color.Z))}, {F(material.Color.W)})";
        string factory = material.Shading switch
        {
            "Basic" => $"MaterialOptions.Basic({color})",
            "Lambert" => $"MaterialOptions.Lambert({color})",
            "Phong" => $"MaterialOptions.Phong({color}, {F(material.Shininess)})",
            _ => $"MaterialOptions.Pbr({color}, {F(material.Metalness)}, {F(Math.Clamp(material.Roughness, 0.02f, 1f))})",
        };

        List<string> overrides = [];
        if (material.Emissive != Vector3.Zero)
        {
            overrides.Add($"Emissive = new Vector3({V3(material.Emissive)})");
            overrides.Add($"EmissiveIntensity = {F(material.EmissiveIntensity)}");
        }

        if (material.Transparent || material.Color.W < 1f)
        {
            overrides.Add("AlphaMode = AlphaMode.Blend");
        }

        if (material.DoubleSided)
        {
            overrides.Add("CullMode = CullMode.None");
        }

        if (material.Wireframe)
        {
            overrides.Add("Wireframe = true");
        }

        void Map(string? texture, string property)
        {
            if (texture is not null && inventory.Textures.ContainsKey(texture))
            {
                overrides.Add($"{property} = {id(texture)}");
            }
        }

        if (material.Map is not null && inventory.TextureRepeats.TryGetValue(material.Map, out Vector2 repeat))
        {
            overrides.Add($"UvScale = new Vector2({F(repeat.X)}, {F(repeat.Y)})");
        }

        Map(material.Map, "BaseColorMap");
        Map(material.NormalMap, "NormalMap");
        Map(material.RoughnessMap, "MetallicRoughnessMap");
        Map(material.EmissiveMap, "EmissiveMap");

        return overrides.Count == 0
            ? factory
            : $"{factory} with {{ {string.Join(", ", overrides)} }}";
    }

    private static string LightExpression(SceneObjectDef light)
    {
        string color = $"new Vector3({V3(light.LightColor)})";
        string factory = light.LightType switch
        {
            "Ambient" => $"Light.Ambient({color}, {F(light.Intensity)})",
            "Point" => $"Light.Point({color}, {F(light.Intensity)}, {F(light.Range)})",
            "Spot" => $"Light.Spot({color}, {F(light.Intensity)}, {F(light.Range)}, {F(light.Angle * (1f - Math.Clamp(light.Penumbra, 0f, 1f)))}, {F(light.Angle)})",
            "Area" => $"Light.Default with {{ Type = LightType.Area, Color = {color}, Intensity = {F(light.Intensity)} }}",
            _ => $"Light.Directional({color}, {F(light.Intensity)})",
        };

        // Point lights have no shadow map yet, so only directional and spot
        // lights carry the flag over.
        return light.CastShadow && light.LightType is "Directional" or "Spot"
            ? $"{factory} with {{ CastShadow = true }}"
            : factory;
    }

    private static List<SceneObjectDef> OrderByHierarchy(IReadOnlyList<SceneObjectDef> objects)
    {
        List<SceneObjectDef> ordered = [];
        HashSet<string> placed = new(StringComparer.Ordinal);
        List<SceneObjectDef> pending = [.. objects];

        // Repeatedly place objects whose parent is already placed (or absent).
        while (pending.Count > 0)
        {
            int before = pending.Count;
            foreach (SceneObjectDef item in pending.ToList())
            {
                bool parentKnown = item.Parent is null || objects.All(o => o.Var != item.Parent) || placed.Contains(item.Parent);
                if (parentKnown)
                {
                    ordered.Add(item);
                    placed.Add(item.Var);
                    pending.Remove(item);
                }
            }

            if (pending.Count == before)
            {
                // A cycle in the source: break it by dropping the parent links.
                foreach (SceneObjectDef item in pending)
                {
                    item.Parent = null;
                }
            }
        }

        return ordered;
    }

    private static string ToCamelIdentifier(string value)
    {
        StringBuilder builder = new();
        bool upper = false;
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            else
            {
                upper = builder.Length > 0;
            }
        }

        string result = builder.Length == 0 ? "item" : builder.ToString();
        if (char.IsDigit(result[0]))
        {
            result = "n" + result;
        }

        return char.ToLowerInvariant(result[0]) + result[1..];
    }

    private static string F(float value) =>
        (float.IsFinite(value) ? value.ToString("0.######", CultureInfo.InvariantCulture) : "0") + "f";

    private static string Int(float value) => ((int)MathF.Max(1f, MathF.Round(value))).ToString(CultureInfo.InvariantCulture);

    private static string V3(Vector3 value) => $"{F(value.X)}, {F(value.Y)}, {F(value.Z)}";

    private static string S(string value) => "\"" + value.Replace("\\", "/").Replace("\"", "\\\"") + "\"";
}
