using System.Numerics;
using ThreeNet.Interop;

namespace ThreeNet.Effects;

/// <summary>
/// A particle system that lives entirely on the GPU: state in a storage buffer,
/// stepped by a compute kernel that also writes the billboard quads.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ParticleEffect"/> rewrites geometry on the CPU every frame, which
/// is honest and keeps everything inspectable, but it caps out in the low
/// thousands because every particle crosses the bus twice. This one never moves
/// particle state through system memory at all, so the cost of a particle is a
/// few GPU instructions and the count can go up by two orders of magnitude.
/// </para>
/// <para>
/// What it gives up is visibility. The CPU does not know where any particle is,
/// so there is no picking, no collision against the scene and no bounds that
/// follow the plume - the node bounds are whatever
/// <see cref="BoundsRadius"/> says. Reach for the CPU system when the particles
/// have to interact with anything, and this one when there are simply a lot of
/// them.
/// </para>
/// <para>
/// Needs <see cref="Renderer.SupportsCompute"/>; construction throws without it.
/// </para>
/// </remarks>
public sealed class GpuParticleEffect
{
    /// <summary>Bytes per particle: position, velocity, age, life, seed, size.</summary>
    private const int ParticleStride = 12 * sizeof(float);

    /// <summary>Floats in the parameter block handed to the kernel each step.</summary>
    private const int ParameterCount = 28;

    private readonly StorageBuffer _particles;
    private readonly StorageBuffer _parameters;
    private readonly ComputeKernel _kernel;
    private readonly float[] _scratch = new float[ParameterCount];
    private double _time;

    /// <summary>
    /// Builds a system of <paramref name="capacity"/> particles and the geometry
    /// it draws into.
    /// </summary>
    public GpuParticleEffect(Scene scene, Renderer renderer, int capacity)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        if (!renderer.SupportsCompute)
        {
            throw new NotSupportedException(
                "this adapter cannot run compute shaders; use ParticleEffect instead");
        }

        Capacity = capacity;
        _particles = scene.CreateStorageBuffer((long)capacity * ParticleStride);
        _parameters = scene.CreateStorageBuffer(ParameterCount * sizeof(float));
        _kernel = scene.CreateComputeKernel(GpuParticleShaders.Step, "step", "particles");

        // Four corners a particle and six indices a quad, uploaded once. Only
        // the vertices ever change after this, and only the GPU changes them.
        Vertex[] vertices = new Vertex[capacity * 4];
        uint[] indices = new uint[capacity * 6];
        for (int i = 0; i < capacity; i++)
        {
            uint baseVertex = (uint)(i * 4);
            int at = i * 6;
            indices[at] = baseVertex;
            indices[at + 1] = baseVertex + 1;
            indices[at + 2] = baseVertex + 2;
            indices[at + 3] = baseVertex;
            indices[at + 4] = baseVertex + 2;
            indices[at + 5] = baseVertex + 3;
        }

        Geometry = scene.CreateGeometry(vertices, indices);
    }

    /// <summary>How many particles the system holds.</summary>
    public int Capacity { get; }

    /// <summary>The mesh the kernel writes; give it a material and add it to the scene.</summary>
    public Geometry Geometry { get; }

    /// <summary>Where particles are born.</summary>
    public Vector3 Origin { get; set; }

    /// <summary>Radius of the sphere particles are born in.</summary>
    public float EmitterRadius { get; set; } = 0.25f;

    /// <summary>Seconds a particle lives.</summary>
    public float Lifetime { get; set; } = 2.5f;

    /// <summary>How much lifetimes vary, 0 to 1.</summary>
    public float LifetimeVariation { get; set; } = 0.35f;

    /// <summary>Initial speed along <see cref="Direction"/>.</summary>
    public float Speed { get; set; } = 2.5f;

    /// <summary>How far from <see cref="Direction"/> a particle may set off.</summary>
    public float Spread { get; set; } = 0.6f;

    /// <summary>Which way the plume goes.</summary>
    public Vector3 Direction { get; set; } = Vector3.UnitY;

    /// <summary>Constant acceleration.</summary>
    public Vector3 Gravity { get; set; } = new(0f, -1.5f, 0f);

    /// <summary>How much speed a particle loses each second, 0 to 1.</summary>
    public float Drag { get; set; } = 0.4f;

    /// <summary>Size of a particle when it is born.</summary>
    public float Size { get; set; } = 0.3f;

    /// <summary>How much a particle grows over its life; 1 keeps it the same.</summary>
    public float Growth { get; set; } = 1.6f;

    /// <summary>
    /// Radius of the bounds the node reports. The CPU cannot know where the
    /// particles went, so this is the one number culling has to go on; too
    /// small and the plume pops out of view at the edge of the frame.
    /// </summary>
    public float BoundsRadius { get; set; } = 20f;

    /// <summary>Steps every particle forward and rewrites the quads.</summary>
    /// <param name="renderer">The renderer whose device owns the buffers.</param>
    /// <param name="scene">The scene holding the geometry and buffers.</param>
    /// <param name="deltaSeconds">Time since the last step.</param>
    /// <param name="cameraWorld">
    /// The camera transform, so the quads can face it. Pass
    /// <see cref="Node.WorldMatrix"/> of the active camera.
    /// </param>
    public void Update(Renderer renderer, Scene scene, float deltaSeconds, Matrix4x4 cameraWorld)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(scene);

        float dt = Math.Clamp(deltaSeconds, 0f, 0.1f);
        _time += dt;

        Vector3 right = Vector3.Normalize(new Vector3(cameraWorld.M11, cameraWorld.M12, cameraWorld.M13));
        Vector3 up = Vector3.Normalize(new Vector3(cameraWorld.M21, cameraWorld.M22, cameraWorld.M23));
        Vector3 direction = Direction.LengthSquared() > 1e-6f
            ? Vector3.Normalize(Direction)
            : Vector3.UnitY;

        int i = 0;
        _scratch[i++] = dt;
        _scratch[i++] = (float)_time;
        _scratch[i++] = Lifetime;
        _scratch[i++] = LifetimeVariation;

        _scratch[i++] = Origin.X;
        _scratch[i++] = Origin.Y;
        _scratch[i++] = Origin.Z;
        _scratch[i++] = EmitterRadius;

        _scratch[i++] = direction.X;
        _scratch[i++] = direction.Y;
        _scratch[i++] = direction.Z;
        _scratch[i++] = Speed;

        _scratch[i++] = Gravity.X;
        _scratch[i++] = Gravity.Y;
        _scratch[i++] = Gravity.Z;
        _scratch[i++] = Drag;

        _scratch[i++] = right.X;
        _scratch[i++] = right.Y;
        _scratch[i++] = right.Z;
        _scratch[i++] = Spread;

        _scratch[i++] = up.X;
        _scratch[i++] = up.Y;
        _scratch[i++] = up.Z;
        _scratch[i++] = Size;

        _scratch[i] = Growth;

        _parameters.Write<float>(_scratch);

        // One invocation a particle; the kernel writes that particle's quad.
        renderer.DispatchToGeometry(
            scene,
            _kernel,
            [_particles, _parameters],
            Geometry,
            (Capacity + 63) / 64);
    }

    /// <summary>A material that draws the quads as soft additive sprites.</summary>
    public static MaterialOptions DefaultMaterial(Vector4 colour) => MaterialOptions.Basic(colour) with
    {
        AlphaMode = AlphaMode.Additive,
        CullMode = CullMode.None,
        DepthWrite = false,
        Shader = null,
    };
}
