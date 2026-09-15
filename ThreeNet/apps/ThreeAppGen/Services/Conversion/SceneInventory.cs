using System.Numerics;
using System.Text.Json.Serialization;

namespace ThreeAppGen.Services.Conversion;

/// <summary>A geometry constructor call, mapped to a Three.Net primitive.</summary>
public sealed class GeometryDef
{
    public required string Var { get; init; }

    /// <summary>Three.js class name, for example BoxGeometry.</summary>
    public required string SourceType { get; init; }

    /// <summary>Three.Net primitive: Box, Sphere, Plane, Cylinder, Cone, Torus.</summary>
    public required string Primitive { get; init; }

    public float[] Arguments { get; init; } = [];

    /// <summary>True when the primitive only approximates the source shape.</summary>
    public bool Approximated { get; init; }
}

public sealed class MaterialDef
{
    public required string Var { get; init; }

    public required string SourceType { get; init; }

    /// <summary>Three.Net shading model: Basic, Lambert, Phong, Pbr.</summary>
    public required string Shading { get; init; }

    public Vector4 Color { get; set; } = Vector4.One;

    public Vector3 Emissive { get; set; }

    public float EmissiveIntensity { get; set; } = 1f;

    public float Metalness { get; set; }

    public float Roughness { get; set; } = 1f;

    public float Shininess { get; set; } = 30f;

    public bool Transparent { get; set; }

    public bool Wireframe { get; set; }

    public bool DoubleSided { get; set; }

    /// <summary>Texture variable or asset path used as the base colour map.</summary>
    public string? Map { get; set; }

    public string? NormalMap { get; set; }

    public string? RoughnessMap { get; set; }

    public string? EmissiveMap { get; set; }
}

public enum SceneObjectKind
{
    Mesh,
    Group,
    Light,
    Camera,
    Model,
}

/// <summary>Anything placed in the graph: meshes, groups, lights, cameras and loaded models.</summary>
public sealed class SceneObjectDef
{
    public required string Var { get; init; }

    public required SceneObjectKind Kind { get; init; }

    public string SourceType { get; init; } = string.Empty;

    public string? Geometry { get; set; }

    public string? Material { get; set; }

    public string? Parent { get; set; }

    public Vector3? Position { get; set; }

    /// <summary>Euler angles in radians, as written in the source.</summary>
    public Vector3? Rotation { get; set; }

    public Vector3? Scale { get; set; }

    public Vector3? LookAt { get; set; }

    // Lights
    public string? LightType { get; set; }

    public Vector3 LightColor { get; set; } = Vector3.One;

    public float Intensity { get; set; } = 1f;

    public float Range { get; set; }

    public float Angle { get; set; } = MathF.PI / 3f;

    public float Penumbra { get; set; }

    // Cameras
    public bool Perspective { get; set; } = true;

    public float FovDegrees { get; set; } = 50f;

    public float Near { get; set; } = 0.1f;

    public float Far { get; set; } = 2000f;

    /// <summary>Asset path of a loaded model (GLTF, GLB, OBJ).</summary>
    public string? ModelPath { get; set; }
}

/// <summary>A per frame change found in the animation loop, normalised to units per second.</summary>
public sealed record AnimationDef(string Target, string Property, char Axis, float PerSecond);

/// <summary>
/// Intermediate representation of the scene: what a static read of the
/// Three.js code could establish with confidence. It drives the baseline
/// C# generator and grounds the LLM conversion.
/// </summary>
public sealed class SceneInventory
{
    public List<GeometryDef> Geometries { get; } = [];

    public List<MaterialDef> Materials { get; } = [];

    public List<SceneObjectDef> Objects { get; } = [];

    public Dictionary<string, string> Textures { get; } = new(StringComparer.Ordinal);

    /// <summary>`texture.repeat.set(u, v)` per texture variable, applied as material UV scale.</summary>
    public Dictionary<string, Vector2> TextureRepeats { get; } = new(StringComparer.Ordinal);

    public List<AnimationDef> Animations { get; } = [];

    public Vector3? Background { get; set; }

    public Vector3? FogColor { get; set; }

    public float FogDensity { get; set; }

    public float FogNear { get; set; }

    public float FogFar { get; set; }

    /// <summary>Three.js renders without tone mapping unless the app asks for it.</summary>
    public string ToneMapping { get; set; } = "None";

    public float Exposure { get; set; } = 1f;

    public bool Antialias { get; set; } = true;

    public bool Bloom { get; set; }

    public float BloomStrength { get; set; } = 1f;

    public float BloomThreshold { get; set; } = 0.85f;

    public bool UsesOrbitControls { get; set; }

    public Vector3? OrbitTarget { get; set; }

    /// <summary>Features that have no Three.Net equivalent yet, with advice.</summary>
    public List<string> Unsupported { get; } = [];

    /// <summary>Values the extractor had to guess.</summary>
    public List<string> Notes { get; } = [];

    [JsonIgnore]
    public SceneObjectDef? Camera => Objects.FirstOrDefault(o => o.Kind == SceneObjectKind.Camera);
}
