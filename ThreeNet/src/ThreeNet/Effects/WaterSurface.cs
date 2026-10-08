using System.Numerics;
using ThreeNet.Interop;

namespace ThreeNet.Effects;

/// <summary>How a body of water looks.</summary>
public readonly record struct WaterStyle
{
    /// <summary>The colour of water you cannot see the bottom of.</summary>
    public Vector3 DeepColor { get; init; }

    /// <summary>The colour of water you can.</summary>
    public Vector3 ShallowColor { get; init; }

    /// <summary>How fast light is lost with depth, per metre.</summary>
    public float Absorption { get; init; }

    /// <summary>How much white gathers on steep crests and along the shore.</summary>
    public float Foam { get; init; }

    /// <summary>Small ripples the simulation grid is too coarse to carry.</summary>
    public float Detail { get; init; }

    /// <summary>How fast those ripples travel.</summary>
    public float DetailSpeed { get; init; }

    /// <summary>How far the mesh follows the height field, in metres per unit.</summary>
    public float Displacement { get; init; }

    /// <summary>Brightness of the caustics the floor draws.</summary>
    public float Caustics { get; init; }

    /// <summary>A clear swimming pool.</summary>
    public static WaterStyle Pool => new()
    {
        DeepColor = new Vector3(0.02f, 0.21f, 0.30f),
        ShallowColor = new Vector3(0.22f, 0.62f, 0.70f),
        Absorption = 0.55f,
        Foam = 0.5f,
        Detail = 0.1f,
        DetailSpeed = 0.5f,
        Displacement = 1f,
        Caustics = 1.6f,
    };

    /// <summary>Open sea: dark, and you see nothing below it.</summary>
    public static WaterStyle Ocean => new()
    {
        DeepColor = new Vector3(0.004f, 0.045f, 0.085f),
        ShallowColor = new Vector3(0.05f, 0.33f, 0.40f),
        Absorption = 1.6f,
        Foam = 0.85f,
        Detail = 0.22f,
        DetailSpeed = 0.7f,
        Displacement = 1f,
        Caustics = 0.4f,
    };

    /// <summary>A lake or a pond: green, soft, slow.</summary>
    public static WaterStyle Lake => new()
    {
        DeepColor = new Vector3(0.02f, 0.10f, 0.09f),
        ShallowColor = new Vector3(0.15f, 0.35f, 0.28f),
        Absorption = 1.1f,
        Foam = 0.25f,
        Detail = 0.12f,
        DetailSpeed = 0.35f,
        Displacement = 1f,
        Caustics = 0.8f,
    };
}

/// <summary>
/// A body of water: a wave simulation, the surface mesh that follows it, and the
/// caustics it throws onto whatever is underneath.
/// </summary>
/// <remarks>
/// <para>
/// The surface mesh is a plane at the centre of the simulated field, lifted in
/// the vertex shader by the height the simulation computes and shaded from the
/// normals it stores. Because both read the same field, a ripple arrives on the
/// bottom of the pool at the moment it passes overhead.
/// </para>
/// <para>
/// Everything a scene has to do each frame is call <see cref="Update"/>:
/// </para>
/// <code>
/// water.Simulation.AddDrop(hit, 0.08f, -0.05f);   // a stone lands
/// water.Update(delta);                            // step, upload, publish
/// float y = water.HeightAt(boat.X, boat.Z);       // and the boat follows
/// </code>
/// </remarks>
public sealed class WaterSurface
{
    private readonly Scene _scene;
    private readonly Texture _field;
    private readonly List<Material> _floors = [];
    private WaterStyle _style;
    private Vector3 _sunDirection = Vector3.Normalize(new Vector3(0.4f, 0.8f, 0.35f));

    /// <param name="scene">The scene the water belongs to.</param>
    /// <param name="center">Centre of the water, at its rest height.</param>
    /// <param name="size">Width and length of the water, in metres.</param>
    /// <param name="floorHeight">Height of the bottom, which is what gives depth.</param>
    /// <param name="resolution">Cells of the wave simulation along each axis.</param>
    /// <param name="segments">Segments of the surface mesh along each axis.</param>
    public WaterSurface(
        Scene scene,
        Vector3 center,
        float size,
        float floorHeight,
        int resolution = 128,
        int segments = 128)
    {
        _scene = scene;
        FloorHeight = floorHeight;
        _style = WaterStyle.Pool;

        Simulation = new WaterSimulation(resolution, size) { Center = center };
        _field = Simulation.CreateTexture(scene);

        Shader surfaceShader = scene.CreateShader(EffectShaders.Water, name: "threenet.water.surface");
        FloorShader = scene.CreateShader(EffectShaders.WaterFloor, name: "threenet.water.floor");

        Material = scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(1f, 1f, 1f, 1f), 0f, 0.05f) with
        {
            AlphaMode = AlphaMode.Blend,
            // A wave seen from under the surface is still a wave.
            CullMode = CullMode.None,
            // Transparent water must not hide the water behind it.
            DepthWrite = false,
            Shader = surfaceShader,
            CustomMap = _field,
        });

        Node = scene.AddMesh(BuildGrid(scene, size, segments), Material, name: "water");
        Node.Position = center;
        // The vertex hook reads the field in object space, which only lines up
        // with the world while the node sits square on the field.
        Node.CastShadow = false;

        Apply();
    }

    /// <summary>The wave field: push it about, read heights off it, make it rain.</summary>
    public WaterSimulation Simulation { get; }

    /// <summary>The surface mesh.</summary>
    public Node Node { get; }

    /// <summary>The surface material.</summary>
    public Material Material { get; }

    /// <summary>
    /// The shader a floor under the water should use, so it catches caustics and
    /// loses colour with depth. Hand it to <see cref="AddFloor"/>.
    /// </summary>
    public Shader FloorShader { get; }

    /// <summary>Height of the bottom, in metres.</summary>
    public float FloorHeight { get; }

    /// <summary>Rest height of the water, in metres.</summary>
    public float Level => Simulation.Center.Y;

    /// <summary>How the water looks. Setting it pushes the change straight through.</summary>
    public WaterStyle Style
    {
        get => _style;
        set
        {
            _style = value;
            Apply();
        }
    }

    /// <summary>
    /// The direction towards the sun, which is the light the caustics refract.
    /// Keep it the same as the scene's own sun.
    /// </summary>
    public Vector3 SunDirection
    {
        get => _sunDirection;
        set
        {
            _sunDirection = value.LengthSquared() > 1e-6f ? Vector3.Normalize(value) : Vector3.UnitY;
            Apply();
        }
    }

    /// <summary>
    /// Makes a material that lives under this water: it keeps its own colour and
    /// textures and gains caustics and depth.
    /// </summary>
    public Material AddFloor(MaterialOptions options)
    {
        Material material = _scene.CreateMaterial(options with
        {
            Shader = FloorShader,
            CustomMap = _field,
        });
        _floors.Add(material);
        Apply();
        return material;
    }

    /// <summary>Height of the surface at a world position, rest level included.</summary>
    public float HeightAt(float worldX, float worldZ) =>
        Level + (Simulation.SampleHeight(worldX, worldZ) * _style.Displacement);

    /// <summary>Surface normal at a world position.</summary>
    public Vector3 NormalAt(float worldX, float worldZ) => Simulation.SampleNormal(worldX, worldZ);

    /// <summary>
    /// Drops a stone in: pushes the surface down at a world position, which is
    /// what starts a ripple.
    /// </summary>
    public void Splash(Vector3 worldPosition, float radius = 0.12f, float strength = 0.04f) =>
        Simulation.AddDrop(worldPosition, radius, -strength);

    /// <summary>Steps the simulation and uploads the result. Call it once a frame.</summary>
    public void Update(float deltaSeconds)
    {
        if (Simulation.Update(deltaSeconds) > 0)
        {
            Simulation.Upload(_field);
        }
    }

    /// <summary>
    /// A flat grid on the xz plane facing up, centred on the origin. The
    /// built-in plane stands on xy facing +z, and the vertex hook reads the
    /// height field by the object space x and z, so the water brings its own.
    /// </summary>
    private static Geometry BuildGrid(Scene scene, float size, int segments)
    {
        segments = Math.Clamp(segments, 1, 1024);
        int stride = segments + 1;
        Vertex[] vertices = new Vertex[stride * stride];
        uint[] indices = new uint[segments * segments * 6];
        float step = size / segments;
        float half = size * 0.5f;

        for (int z = 0; z < stride; z++)
        {
            for (int x = 0; x < stride; x++)
            {
                vertices[(z * stride) + x] = new Vertex(
                    new Vector3((x * step) - half, 0f, (z * step) - half),
                    Vector3.UnitY,
                    new Vector2(x / (float)segments, z / (float)segments));
            }
        }

        int index = 0;
        for (int z = 0; z < segments; z++)
        {
            for (int x = 0; x < segments; x++)
            {
                uint a = (uint)((z * stride) + x);
                uint b = a + 1;
                uint c = a + (uint)stride;
                uint d = c + 1;
                indices[index++] = a;
                indices[index++] = c;
                indices[index++] = b;
                indices[index++] = b;
                indices[index++] = c;
                indices[index++] = d;
            }
        }

        return scene.CreateGeometry(vertices, indices);
    }

    /// <summary>Writes the style and the geometry of the water into the material slots.</summary>
    private void Apply()
    {
        Vector3 center = Simulation.Center;
        Vector4 field = new(center.X, center.Z, Simulation.Size, _style.Displacement);

        MaterialOptions surface = Material.Options;
        Material.Options = surface with
        {
            Custom0 = field,
            Custom1 = new Vector4(_style.DeepColor, _style.Absorption),
            Custom2 = new Vector4(_style.ShallowColor, FloorHeight),
            Custom3 = new Vector4(_style.Foam, _style.Detail, _style.DetailSpeed, Level),
        };

        foreach (Material floor in _floors)
        {
            MaterialOptions options = floor.Options;
            floor.Options = options with
            {
                Custom0 = new Vector4(center.X, center.Z, Simulation.Size, Level),
                Custom1 = new Vector4(_sunDirection, _style.Caustics),
                Custom2 = new Vector4(_style.DeepColor, _style.Absorption),
            };
        }
    }
}
