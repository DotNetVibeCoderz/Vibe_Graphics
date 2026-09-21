using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThreeNet.Scenes;

/// <summary>Kind of geometry a <see cref="GeometryDefinition"/> creates.</summary>
public enum GeometryKind
{
    Box,
    Sphere,
    Plane,
    Cylinder,
    Cone,
    Torus,
    Grid,
    /// <summary>Imported from <see cref="GeometryDefinition.ModelPath"/> (glTF, FBX, OBJ).</summary>
    Model,
}

/// <summary>A geometry, either a primitive with parameters or a model file.</summary>
public sealed class GeometryDefinition
{
    public string Id { get; set; } = string.Empty;

    public GeometryKind Kind { get; set; }

    /// <summary>Box width / sphere or cone or torus radius / plane width / cylinder top radius / grid size.</summary>
    public float A { get; set; } = 1f;

    /// <summary>Box height / plane height / cylinder bottom radius / torus tube.</summary>
    public float B { get; set; } = 1f;

    /// <summary>Box depth / cylinder or cone height.</summary>
    public float C { get; set; } = 1f;

    /// <summary>Segments (width segments, radial segments, divisions).</summary>
    public int Segments { get; set; } = 32;

    /// <summary>Second segment count (height / tubular segments).</summary>
    public int Segments2 { get; set; } = 16;

    /// <summary>Model file, relative to the document when not rooted.</summary>
    public string? ModelPath { get; set; }

    public Geometry? Create(Scene scene)
    {
        return Kind switch
        {
            GeometryKind.Box => scene.CreateBoxGeometry(A, B, C, Math.Max(1, Segments / 32)),
            GeometryKind.Sphere => scene.CreateSphereGeometry(A, Math.Max(3, Segments), Math.Max(2, Segments2)),
            GeometryKind.Plane => scene.CreatePlaneGeometry(A, B, Math.Max(1, Segments / 32), Math.Max(1, Segments2 / 16)),
            GeometryKind.Cylinder => scene.CreateCylinderGeometry(A, B, C, Math.Max(3, Segments)),
            GeometryKind.Cone => scene.CreateConeGeometry(A, C, Math.Max(3, Segments)),
            GeometryKind.Torus => scene.CreateTorusGeometry(A, B, Math.Max(3, Segments2), Math.Max(3, Segments)),
            GeometryKind.Grid => scene.CreateGridGeometry(A, Math.Max(1, Segments)),
            _ => null,
        };
    }

    public static GeometryDefinition Box(string id, float width = 1f, float height = 1f, float depth = 1f) =>
        new() { Id = id, Kind = GeometryKind.Box, A = width, B = height, C = depth };

    public static GeometryDefinition Sphere(string id, float radius = 1f) =>
        new() { Id = id, Kind = GeometryKind.Sphere, A = radius, Segments = 32, Segments2 = 16 };

    public static GeometryDefinition Plane(string id, float width = 1f, float height = 1f) =>
        new() { Id = id, Kind = GeometryKind.Plane, A = width, B = height };

    public static GeometryDefinition Model(string id, string path) =>
        new() { Id = id, Kind = GeometryKind.Model, ModelPath = path };
}

public sealed class TextureDefinition
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Image file, relative to the document when not rooted.</summary>
    public string Path { get; set; } = string.Empty;

    public bool Srgb { get; set; } = true;
}

/// <summary>A material: <see cref="MaterialOptions"/> plus texture ids.</summary>
public sealed class MaterialDefinition
{
    public string Id { get; set; } = string.Empty;

    public ShadingModel Shading { get; set; } = ShadingModel.Pbr;

    public Vector4 BaseColor { get; set; } = Vector4.One;

    public float Metallic { get; set; }

    public float Roughness { get; set; } = 0.5f;

    public Vector3 Emissive { get; set; }

    public float EmissiveIntensity { get; set; } = 1f;

    public float Shininess { get; set; } = 32f;

    public AlphaMode AlphaMode { get; set; } = AlphaMode.Opaque;

    public float AlphaCutoff { get; set; } = 0.5f;

    public CullMode CullMode { get; set; } = CullMode.Back;

    public bool Wireframe { get; set; }

    public Vector2 UvScale { get; set; } = Vector2.One;

    public string? BaseColorMap { get; set; }

    public string? NormalMap { get; set; }

    public string? MetallicRoughnessMap { get; set; }

    public string? EmissiveMap { get; set; }
}

public sealed class LightDefinition
{
    public LightType Type { get; set; } = LightType.Directional;

    public Vector3 Color { get; set; } = Vector3.One;

    public float Intensity { get; set; } = 3f;

    public float Range { get; set; }

    public float InnerConeAngle { get; set; } = 0.3f;

    public float OuterConeAngle { get; set; } = 0.5f;

    public bool CastShadow { get; set; }

    public bool Enabled { get; set; } = true;

    public Light ToLight() => new()
    {
        Type = Type,
        Color = Color,
        Intensity = Intensity,
        Range = Range,
        InnerConeAngle = InnerConeAngle,
        OuterConeAngle = OuterConeAngle,
        CastShadow = CastShadow,
        Enabled = Enabled,
    };

    public static LightDefinition From(in Light light) => new()
    {
        Type = light.Type,
        Color = light.Color,
        Intensity = light.Intensity,
        Range = light.Range,
        InnerConeAngle = light.InnerConeAngle,
        OuterConeAngle = light.OuterConeAngle,
        CastShadow = light.CastShadow,
        Enabled = light.Enabled,
    };
}

public sealed class CameraDefinition
{
    public bool Perspective { get; set; } = true;

    public float FieldOfView { get; set; } = 0.9f;

    public float OrthographicHeight { get; set; } = 10f;

    public float Near { get; set; } = 0.1f;

    public float Far { get; set; } = 1000f;

    public bool Active { get; set; }

    public Camera ToCamera() => Perspective
        ? Camera.Perspective(FieldOfView, Near, Far)
        : Camera.Orthographic(OrthographicHeight, Near, Far);
}

/// <summary>Optional rigid body and collider for a node.</summary>
public sealed class PhysicsDefinition
{
    public RigidBodyType Body { get; set; } = RigidBodyType.Dynamic;

    /// <summary>No rigid body: a static collider only.</summary>
    public bool ColliderOnly { get; set; }

    public ColliderShape Shape { get; set; } = ColliderShape.Box;

    public Vector3 HalfExtents { get; set; } = new(0.5f);

    public float Radius { get; set; } = 0.5f;

    public float HalfHeight { get; set; } = 0.5f;

    public float Mass { get; set; }

    public float Friction { get; set; } = 0.5f;

    public float Restitution { get; set; }

    public bool IsSensor { get; set; }
}

/// <summary>A node and its children.</summary>
public sealed class NodeDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = "node";

    public Vector3 Position { get; set; }

    /// <summary>Euler angles in radians (YXZ, as <see cref="Node.EulerAngles"/>).</summary>
    public Vector3 Rotation { get; set; }

    public Vector3 Scale { get; set; } = Vector3.One;

    public bool Visible { get; set; } = true;

    public bool CastShadow { get; set; } = true;

    public bool ReceiveShadow { get; set; } = true;

    public string? GeometryId { get; set; }

    public string? MaterialId { get; set; }

    public LightDefinition? Light { get; set; }

    public CameraDefinition? Camera { get; set; }

    public PhysicsDefinition? Physics { get; set; }

    public List<NodeDefinition> Children { get; set; } = [];

    /// <summary>This node and every descendant, depth first.</summary>
    public IEnumerable<NodeDefinition> Flatten()
    {
        yield return this;
        foreach (NodeDefinition child in Children)
        {
            foreach (NodeDefinition descendant in child.Flatten())
            {
                yield return descendant;
            }
        }
    }
}

public sealed class EnvironmentDefinition
{
    public Vector4 Background { get; set; } = new(0.02f, 0.02f, 0.03f, 1f);

    public Vector3 AmbientColor { get; set; } = Vector3.One;

    public float AmbientIntensity { get; set; } = 0.15f;

    public Vector3 FogColor { get; set; } = new(0.5f, 0.55f, 0.6f);

    public float FogDensity { get; set; }

    public string? EnvironmentMap { get; set; }

    public float EnvironmentIntensity { get; set; } = 1f;
}

/// <summary>Maps document ids to the objects created by <see cref="SceneDocument.Build"/>.</summary>
public sealed class SceneBuildResult
{
    public Dictionary<string, Node> Nodes { get; } = [];

    public Dictionary<string, Geometry> Geometries { get; } = [];

    public Dictionary<string, Material> Materials { get; } = [];

    public Dictionary<string, Texture> Textures { get; } = [];

    /// <summary>Assets that could not be loaded (missing files, unsupported formats).</summary>
    public List<string> Warnings { get; } = [];

    public Node? ActiveCamera { get; set; }
}

/// <summary>
/// An editable, serialisable description of a scene: geometries, textures,
/// materials, a node tree with lights, cameras and physics, and environment
/// settings. <see cref="Build"/> turns it into a live <see cref="Scene"/>;
/// <see cref="Save"/> and <see cref="Load"/> read and write JSON.
/// </summary>
public sealed class SceneDocument
{
    public string Name { get; set; } = "Scene";

    /// <summary>Format version, so older files can be migrated.</summary>
    public int Version { get; set; } = 1;

    public EnvironmentDefinition Environment { get; set; } = new();

    public List<TextureDefinition> Textures { get; set; } = [];

    public List<GeometryDefinition> Geometries { get; set; } = [];

    public List<MaterialDefinition> Materials { get; set; } = [];

    public List<NodeDefinition> Nodes { get; set; } = [];

    /// <summary>Directory the relative asset paths resolve against (not serialised).</summary>
    [JsonIgnore]
    public string? BaseDirectory { get; set; }

    /// <summary>Every node definition in the document, depth first.</summary>
    public IEnumerable<NodeDefinition> AllNodes() => Nodes.SelectMany(node => node.Flatten());

    public NodeDefinition? Find(string id) => AllNodes().FirstOrDefault(node => node.Id == id);

    /// <summary>Removes a node (and its children) from wherever it sits in the tree.</summary>
    public bool Remove(NodeDefinition node)
    {
        if (Nodes.Remove(node))
        {
            return true;
        }

        foreach (NodeDefinition parent in AllNodes())
        {
            if (parent.Children.Remove(node))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The parent of a node, or null when it is at the root.</summary>
    public NodeDefinition? ParentOf(NodeDefinition node) =>
        AllNodes().FirstOrDefault(candidate => candidate.Children.Contains(node));

    /// <summary>Unused id of the form "prefix-1".</summary>
    public string NextId(string prefix)
    {
        HashSet<string> used = [.. AllNodes().Select(n => n.Id), .. Geometries.Select(g => g.Id), .. Materials.Select(m => m.Id), .. Textures.Select(t => t.Id)];
        for (int i = 1; ; i++)
        {
            string candidate = $"{prefix}-{i}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Creates everything in <paramref name="scene"/> (which is not cleared first).</summary>
    public SceneBuildResult Build(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        SceneBuildResult result = new();

        foreach (TextureDefinition texture in Textures)
        {
            try
            {
                result.Textures[texture.Id] = scene.LoadTextureCached(Resolve(texture.Path), texture.Srgb);
            }
            catch (Exception exception) when (exception is ThreeNetException or IOException)
            {
                result.Warnings.Add($"texture '{texture.Id}': {exception.Message}");
            }
        }

        foreach (GeometryDefinition geometry in Geometries)
        {
            if (geometry.Kind == GeometryKind.Model)
            {
                continue; // imported per node, models carry their own materials
            }

            if (geometry.Create(scene) is { } created)
            {
                result.Geometries[geometry.Id] = created;
            }
        }

        foreach (MaterialDefinition material in Materials)
        {
            result.Materials[material.Id] = scene.CreateMaterial(ToOptions(material, result));
        }

        scene.Environment = new SceneEnvironment
        {
            Background = Environment.Background,
            AmbientColor = Environment.AmbientColor,
            AmbientIntensity = Environment.AmbientIntensity,
            FogColor = Environment.FogColor,
            FogDensity = Environment.FogDensity,
            FogStart = 10f,
            FogEnd = 100f,
            EnvironmentIntensity = Environment.EnvironmentIntensity,
            EnvironmentMap = Environment.EnvironmentMap is { Length: > 0 } map && TryLoadTexture(scene, map, result) is { } environmentMap
                ? environmentMap
                : null,
        };

        foreach (NodeDefinition node in Nodes)
        {
            BuildNode(scene, node, null, result);
        }

        return result;
    }

    private Texture? TryLoadTexture(Scene scene, string path, SceneBuildResult result)
    {
        try
        {
            return scene.LoadTextureCached(Resolve(path), srgb: true);
        }
        catch (Exception exception) when (exception is ThreeNetException or IOException)
        {
            result.Warnings.Add($"environment map: {exception.Message}");
            return null;
        }
    }

    private void BuildNode(Scene scene, NodeDefinition definition, Node? parent, SceneBuildResult result)
    {
        Node node = scene.CreateNode(parent, definition.Name);
        node.Position = definition.Position;
        node.EulerAngles = definition.Rotation;
        node.Scale = definition.Scale;
        node.Visible = definition.Visible;

        GeometryDefinition? geometry = definition.GeometryId is { } id ? Geometries.FirstOrDefault(g => g.Id == id) : null;
        if (geometry is { Kind: GeometryKind.Model, ModelPath: { Length: > 0 } path })
        {
            try
            {
                scene.LoadModelCached(Resolve(path), node);
            }
            catch (Exception exception) when (exception is ThreeNetException or NotSupportedException or IOException)
            {
                result.Warnings.Add($"model '{geometry.Id}': {exception.Message}");
            }
        }
        else if (geometry is not null && result.Geometries.TryGetValue(geometry.Id, out Geometry? mesh))
        {
            Material material = definition.MaterialId is { } materialId && result.Materials.TryGetValue(materialId, out Material? found)
                ? found
                : scene.CreateMaterial(Vector4.One);
            node.AttachMesh(mesh, material);
            node.CastShadow = definition.CastShadow;
            node.ReceiveShadow = definition.ReceiveShadow;
        }

        if (definition.Light is { } light)
        {
            node.Light = light.ToLight();
        }

        if (definition.Camera is { } camera)
        {
            node.Camera = camera.ToCamera();
            if (camera.Active)
            {
                result.ActiveCamera = node;
                scene.ActiveCamera = node;
            }
        }

        if (definition.Physics is { } physics)
        {
            ApplyPhysics(scene, node, physics, result);
        }

        result.Nodes[definition.Id] = node;
        foreach (NodeDefinition child in definition.Children)
        {
            BuildNode(scene, child, node, result);
        }
    }

    private static void ApplyPhysics(Scene scene, Node node, PhysicsDefinition physics, SceneBuildResult result)
    {
        ColliderOptions collider = physics.Shape switch
        {
            ColliderShape.Sphere => ColliderOptions.Sphere(physics.Radius),
            ColliderShape.Capsule => ColliderOptions.Capsule(physics.HalfHeight, physics.Radius),
            ColliderShape.Cylinder => ColliderOptions.Cylinder(physics.HalfHeight, physics.Radius),
            _ => ColliderOptions.Box(physics.HalfExtents),
        };
        collider = collider with { Friction = physics.Friction, Restitution = physics.Restitution, IsSensor = physics.IsSensor };

        try
        {
            if (!physics.ColliderOnly)
            {
                scene.Physics.AddBody(node, RigidBodyOptions.Dynamic with { Type = physics.Body, AdditionalMass = physics.Mass });
            }

            scene.Physics.AddCollider(node, collider);
        }
        catch (ThreeNetException exception)
        {
            result.Warnings.Add($"physics on '{node.Name}': {exception.Message}");
        }
    }

    private static MaterialOptions ToOptions(MaterialDefinition material, SceneBuildResult result)
    {
        Texture? Map(string? id) => id is { Length: > 0 } && result.Textures.TryGetValue(id, out Texture? texture) ? texture : null;
        return new MaterialOptions
        {
            Shading = material.Shading,
            BaseColor = material.BaseColor,
            Metallic = material.Metallic,
            Roughness = material.Roughness,
            Emissive = material.Emissive,
            EmissiveIntensity = material.EmissiveIntensity,
            Shininess = material.Shininess,
            AlphaMode = material.AlphaMode,
            AlphaCutoff = material.AlphaCutoff,
            CullMode = material.CullMode,
            Wireframe = material.Wireframe,
            UvScale = material.UvScale,
            BaseColorMap = Map(material.BaseColorMap),
            NormalMap = Map(material.NormalMap),
            MetallicRoughnessMap = Map(material.MetallicRoughnessMap),
            EmissiveMap = Map(material.EmissiveMap),
        };
    }

    /// <summary>Makes an asset path absolute using <see cref="BaseDirectory"/>.</summary>
    public string Resolve(string path) =>
        System.IO.Path.IsPathRooted(path) || BaseDirectory is null ? path : System.IO.Path.Combine(BaseDirectory, path);

    // ------------------------------------------------------------------ json

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static SceneDocument FromJson(string json) =>
        JsonSerializer.Deserialize<SceneDocument>(json, Options) ?? throw new InvalidDataException("empty scene document");

    public void Save(string path)
    {
        File.WriteAllText(path, ToJson());
        BaseDirectory ??= System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
    }

    public static SceneDocument Load(string path)
    {
        SceneDocument document = FromJson(File.ReadAllText(path));
        document.BaseDirectory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        return document;
    }

    /// <summary>A small starter scene: ground plane, a box, a sun and a camera.</summary>
    public static SceneDocument CreateDefault()
    {
        SceneDocument document = new()
        {
            Geometries =
            {
                // A flat box, not a rotated plane: the collider then matches the visible ground.
                GeometryDefinition.Box("ground", 20f, 0.2f, 20f),
                GeometryDefinition.Box("box"),
            },
            Materials =
            {
                new MaterialDefinition { Id = "ground", BaseColor = new Vector4(0.35f, 0.38f, 0.42f, 1f), Roughness = 0.9f },
                new MaterialDefinition { Id = "box", BaseColor = new Vector4(0.85f, 0.45f, 0.25f, 1f), Roughness = 0.4f },
            },
        };
        document.Nodes.Add(new NodeDefinition
        {
            Id = "ground-1",
            Name = "ground",
            Position = new Vector3(0f, -0.1f, 0f),
            GeometryId = "ground",
            MaterialId = "ground",
            CastShadow = false,
            Physics = new PhysicsDefinition { ColliderOnly = true, HalfExtents = new Vector3(10f, 0.1f, 10f) },
        });
        document.Nodes.Add(new NodeDefinition
        {
            Id = "box-1",
            Name = "box",
            Position = new Vector3(0f, 0.5f, 0f),
            GeometryId = "box",
            MaterialId = "box",
            Physics = new PhysicsDefinition(),
        });
        document.Nodes.Add(new NodeDefinition
        {
            Id = "sun-1",
            Name = "sun",
            Position = new Vector3(4f, 6f, 4f),
            Rotation = new Vector3(-0.9f, 0.7f, 0f),
            Light = new LightDefinition { Type = LightType.Directional, Intensity = 3f, CastShadow = true },
        });
        document.Nodes.Add(new NodeDefinition
        {
            Id = "camera-1",
            Name = "camera",
            Position = new Vector3(0f, 2.5f, 7f),
            Rotation = new Vector3(-0.25f, 0f, 0f),
            Camera = new CameraDefinition { Active = true },
        });
        return document;
    }
}
