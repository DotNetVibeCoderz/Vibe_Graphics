using System.Numerics;
using ThreeNet.Interop;

namespace ThreeNet.Effects;

/// <summary>Where a particle is born.</summary>
public enum EmitterShape
{
    /// <summary>All from one place.</summary>
    Point,
    /// <summary>Anywhere inside a box of <see cref="ParticleEffect.EmitterSize"/>.</summary>
    Box,
    /// <summary>Anywhere inside a ball, or on it when the emitter is surface only.</summary>
    Sphere,
    /// <summary>A flat disc on the xz plane.</summary>
    Disc,
    /// <summary>A cone opening upwards, which is what a jet or a fire wants.</summary>
    Cone,
    /// <summary>A ring: the rim of the disc, nothing inside it.</summary>
    Ring,
}

/// <summary>A point that pulls particles towards it, or spins them around it.</summary>
public readonly record struct Attractor(Vector3 Position, float Strength, float Radius = 0f, bool Vortex = false);

/// <summary>
/// A CPU particle system that writes camera facing quads into one geometry and
/// re-uploads it every frame: sparks, embers, smoke, rain, dust, magic.
/// </summary>
/// <remarks>
/// <para>
/// One system is one draw call. The simulation is the familiar one - emit, age,
/// integrate, retire - with the pieces that make an effect look deliberate
/// rather than sprayed: emitter shapes, curl noise turbulence, attractors and a
/// floor to bounce off, after the vocabulary
/// <see href="https://github.com/mustache-dev/Three-VFX">Three VFX</see>
/// settled on.
/// </para>
/// <para>
/// The age and a per particle random are packed into the uv of each quad, so
/// <see cref="EffectShaders.Particle"/> can tint a particle by its own age
/// without a vertex colour channel. Pair it with
/// <see cref="AlphaMode.Additive"/> for anything that glows and
/// <see cref="AlphaMode.Blend"/> for anything that does not.
/// </para>
/// </remarks>
public sealed class ParticleEffect
{
    private readonly Particle[] _particles;
    private readonly Vertex[] _vertices;
    private readonly uint[] _indices;
    private readonly Random _random;
    private int _alive;
    private float _carry;

    private struct Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Age;
        public float Life;
        public float Size;
        public float Spin;
        public float Seed;
    }

    public ParticleEffect(int capacity, int seed = 1337)
    {
        Capacity = Math.Max(16, capacity);
        _particles = new Particle[Capacity];
        _vertices = new Vertex[Capacity * 4];
        _indices = new uint[Capacity * 6];
        _random = new Random(seed);
        for (int i = 0; i < Capacity; i++)
        {
            uint quad = (uint)(i * 4);
            int index = i * 6;
            _indices[index] = quad;
            _indices[index + 1] = quad + 1;
            _indices[index + 2] = quad + 2;
            _indices[index + 3] = quad;
            _indices[index + 4] = quad + 2;
            _indices[index + 5] = quad + 3;
        }
    }

    /// <summary>The most particles that can be alive at once.</summary>
    public int Capacity { get; }

    /// <summary>How many are alive now.</summary>
    public int Alive => _alive;

    /// <summary>Particles emitted per second. Zero emits only on <see cref="Burst"/>.</summary>
    public float Rate { get; set; } = 400f;

    /// <summary>Seconds a particle lives, before the spread below.</summary>
    public float Lifetime { get; set; } = 2.4f;

    /// <summary>How much lifetime, size and speed vary between particles, 0 to 1.</summary>
    public float Variation { get; set; } = 0.35f;

    /// <summary>Size at birth, in metres.</summary>
    public float Size { get; set; } = 0.35f;

    /// <summary>Size at death relative to birth.</summary>
    public float Growth { get; set; } = 2.2f;

    /// <summary>Where particles are born.</summary>
    public EmitterShape Shape { get; set; } = EmitterShape.Disc;

    /// <summary>Centre of the emitter in world space.</summary>
    public Vector3 Origin { get; set; }

    /// <summary>Radius of a sphere, disc, ring or cone emitter.</summary>
    public float EmitterRadius { get; set; } = 0.35f;

    /// <summary>Half extents of a box emitter.</summary>
    public Vector3 EmitterSize { get; set; } = new(0.5f, 0.5f, 0.5f);

    /// <summary>Half angle of a cone emitter, in radians.</summary>
    public float ConeAngle { get; set; } = MathF.PI / 6f;

    /// <summary>Emit from the shell of a sphere rather than from inside it.</summary>
    public bool SurfaceOnly { get; set; }

    /// <summary>Speed along the emitter's own direction at birth, in metres per second.</summary>
    public float Rise { get; set; } = 2.4f;

    /// <summary>Sideways speed at birth, in metres per second.</summary>
    public float Spread { get; set; } = 0.7f;

    /// <summary>
    /// Send particles outward from the emitter centre instead of along
    /// <see cref="Direction"/>: what an explosion does.
    /// </summary>
    public bool Radial { get; set; }

    /// <summary>The way a cone points and <see cref="Rise"/> pushes.</summary>
    public Vector3 Direction { get; set; } = Vector3.UnitY;

    /// <summary>Acceleration, in metres per second squared. Fire wants it positive.</summary>
    public Vector3 Gravity { get; set; } = new(0f, -1.2f, 0f);

    /// <summary>Velocity lost per second to the air, 0 to 1.</summary>
    public float Drag { get; set; } = 0.55f;

    /// <summary>Wind, in metres per second, that particles are carried along by.</summary>
    public Vector3 Wind { get; set; }

    /// <summary>How much of the wind a particle picks up.</summary>
    public float WindInfluence { get; set; } = 1f;

    /// <summary>Strength of the curl noise that keeps a plume from rising straight.</summary>
    public float Turbulence { get; set; }

    /// <summary>Size of the turbulence, in cycles per metre.</summary>
    public float TurbulenceScale { get; set; } = 0.6f;

    /// <summary>How fast the turbulence field itself moves.</summary>
    public float TurbulenceSpeed { get; set; } = 0.4f;

    /// <summary>Up to four points that pull particles in or spin them around.</summary>
    public IList<Attractor> Attractors { get; } = new List<Attractor>(4);

    /// <summary>Height of a floor particles bounce off; null lets them fall through.</summary>
    public float? FloorHeight { get; set; }

    /// <summary>How much speed survives a bounce off the floor.</summary>
    public float Bounce { get; set; } = 0.35f;

    /// <summary>How fast a particle turns on its own axis, in radians per second.</summary>
    public float SpinRate { get; set; } = 1f;

    /// <summary>Stretches a particle along the way it is going; 0 keeps it round.</summary>
    public float StretchBySpeed { get; set; }

    /// <summary>Removes every live particle.</summary>
    public void Clear()
    {
        _alive = 0;
        _carry = 0f;
    }

    /// <summary>Emits <paramref name="count"/> particles at once, for an impact or a puff.</summary>
    public void Burst(int count)
    {
        for (int i = 0; i < count && _alive < Capacity; i++)
        {
            Emit();
        }
    }

    /// <summary>Ages every particle, emits new ones and moves them all.</summary>
    public void Update(float deltaSeconds, double totalSeconds = 0)
    {
        float dt = Math.Clamp(deltaSeconds, 0f, 0.1f);
        Vector3 wind = Wind * WindInfluence;
        float time = (float)totalSeconds;

        for (int i = 0; i < _alive; i++)
        {
            ref Particle particle = ref _particles[i];
            particle.Age += dt;
            if (particle.Age >= particle.Life)
            {
                // Swap the dead one with the last live one: no allocation, no
                // gaps, and the order never matters for a sprite.
                _particles[i] = _particles[--_alive];
                i--;
                continue;
            }

            particle.Velocity += Gravity * dt;
            particle.Velocity += (wind - particle.Velocity) * MathF.Min(1f, dt * Drag * 2f);

            if (Turbulence > 0f)
            {
                particle.Velocity += Curl(particle.Position, time) * (Turbulence * dt);
            }

            for (int a = 0; a < Attractors.Count && a < 4; a++)
            {
                Attractor attractor = Attractors[a];
                Vector3 toward = attractor.Position - particle.Position;
                float distance = toward.Length();
                if (distance < 1e-4f || (attractor.Radius > 0f && distance > attractor.Radius))
                {
                    continue;
                }

                // Falls off with distance, but never blows up at the centre.
                float pull = attractor.Strength / MathF.Max(distance, 0.25f);
                Vector3 axis = toward / distance;
                particle.Velocity += (attractor.Vortex
                    ? Vector3.Cross(axis, Vector3.UnitY) * pull
                    : axis * pull) * dt;
            }

            particle.Position += particle.Velocity * dt;

            if (FloorHeight is { } floor && particle.Position.Y < floor)
            {
                particle.Position.Y = floor;
                particle.Velocity.Y = MathF.Abs(particle.Velocity.Y) * Bounce;
                particle.Velocity.X *= 0.7f;
                particle.Velocity.Z *= 0.7f;
            }
        }

        _carry += Rate * dt;
        int spawn = (int)_carry;
        _carry -= spawn;
        for (int i = 0; i < spawn && _alive < Capacity; i++)
        {
            Emit();
        }
    }

    /// <summary>
    /// Rebuilds the quads facing the camera and uploads them. Particles past the
    /// live range collapse to nothing, so the index buffer never changes size.
    /// </summary>
    public void Upload(Geometry geometry, Matrix4x4 cameraWorld)
    {
        Vector3 right = Vector3.Normalize(new Vector3(cameraWorld.M11, cameraWorld.M12, cameraWorld.M13));
        Vector3 up = Vector3.Normalize(new Vector3(cameraWorld.M21, cameraWorld.M22, cameraWorld.M23));
        Vector3 normal = Vector3.Normalize(new Vector3(cameraWorld.M31, cameraWorld.M32, cameraWorld.M33));

        for (int i = 0; i < _alive; i++)
        {
            Particle particle = _particles[i];
            float t = Math.Clamp(particle.Age / MathF.Max(particle.Life, 1e-3f), 0f, 1f);
            float size = particle.Size * float.Lerp(1f, Growth, t) * 0.5f;
            float spin = particle.Spin * particle.Age;
            Vector3 r = ((right * MathF.Cos(spin)) + (up * MathF.Sin(spin))) * size;
            Vector3 u = ((up * MathF.Cos(spin)) - (right * MathF.Sin(spin))) * size;

            if (StretchBySpeed > 0f)
            {
                // A fast particle is a streak, drawn along where it is going.
                float speed = particle.Velocity.Length();
                if (speed > 0.01f)
                {
                    Vector3 along = particle.Velocity / speed;
                    Vector3 screenAlong = along - (normal * Vector3.Dot(along, normal));
                    if (screenAlong.LengthSquared() > 1e-6f)
                    {
                        screenAlong = Vector3.Normalize(screenAlong);
                        Vector3 across = Vector3.Normalize(Vector3.Cross(normal, screenAlong));
                        float stretch = 1f + (speed * StretchBySpeed);
                        u = screenAlong * size * stretch;
                        r = across * size;
                    }
                }
            }

            // The age and the per particle random ride in the whole part of the
            // uv, the corner in the fraction: see EffectShaders.Sprite.
            float age = MathF.Floor(t * 255f);
            float seed = MathF.Floor(particle.Seed * 255f);
            Vector2 uv00 = new(age + 0.25f, seed + 0.25f);
            Vector2 uv10 = new(age + 0.75f, seed + 0.25f);
            Vector2 uv11 = new(age + 0.75f, seed + 0.75f);
            Vector2 uv01 = new(age + 0.25f, seed + 0.75f);

            int vertex = i * 4;
            _vertices[vertex] = new Vertex(particle.Position - r - u, normal, uv00);
            _vertices[vertex + 1] = new Vertex(particle.Position + r - u, normal, uv10);
            _vertices[vertex + 2] = new Vertex(particle.Position + r + u, normal, uv11);
            _vertices[vertex + 3] = new Vertex(particle.Position - r + u, normal, uv01);
        }

        for (int i = _alive; i < Capacity; i++)
        {
            int vertex = i * 4;
            _vertices[vertex] = default;
            _vertices[vertex + 1] = default;
            _vertices[vertex + 2] = default;
            _vertices[vertex + 3] = default;
        }

        geometry.Update(_vertices, _indices);
    }

    /// <summary>An empty geometry sized for this system, ready to be updated.</summary>
    public Geometry CreateGeometry(Scene scene) => scene.CreateGeometry(_vertices, _indices);

    /// <summary>
    /// A material for glowing particles: unlit, additive, no depth writing and
    /// no culling, with the sprite shader already attached.
    /// </summary>
    public static MaterialOptions GlowMaterial(Shader sprite, Vector3 birth, Vector3 death, float brightness = 2.5f) =>
        MaterialOptions.Default with
        {
            Shading = ShadingModel.Basic,
            AlphaMode = AlphaMode.Additive,
            CullMode = CullMode.None,
            DepthWrite = false,
            BaseColor = new Vector4(1f, 1f, 1f, 1f),
            Shader = sprite,
            Custom0 = new Vector4(birth, 1.6f),
            Custom1 = new Vector4(death, brightness),
            Custom2 = new Vector4(1f, 0f, 0f, 0f),
        };

    /// <summary>A material for smoke and dust: lit, alpha blended, no depth writing.</summary>
    public static MaterialOptions SmokeMaterial(Shader smoke, Vector3 color, float opacity = 0.35f) =>
        MaterialOptions.Default with
        {
            Shading = ShadingModel.Lambert,
            AlphaMode = AlphaMode.Blend,
            CullMode = CullMode.None,
            DepthWrite = false,
            Roughness = 1f,
            BaseColor = new Vector4(color, 1f),
            Shader = smoke,
            Custom0 = new Vector4(color, 1.1f),
            Custom1 = new Vector4(opacity, 0f, 0f, 0f),
        };

    private void Emit()
    {
        (Vector3 offset, Vector3 outward) = Spawn();
        float vary = Math.Clamp(Variation, 0f, 1f);
        float Jitter() => 1f + (((float)_random.NextDouble() - 0.5f) * 2f * vary);

        Vector3 along = Direction.LengthSquared() > 1e-6f ? Vector3.Normalize(Direction) : Vector3.UnitY;
        Vector3 launch = Radial ? outward : along;
        Vector3 sideways = new(
            ((float)_random.NextDouble() - 0.5f) * Spread,
            ((float)_random.NextDouble() - 0.5f) * Spread,
            ((float)_random.NextDouble() - 0.5f) * Spread);

        _particles[_alive++] = new Particle
        {
            Position = Origin + offset,
            Velocity = (launch * Rise * Jitter()) + sideways,
            Age = 0f,
            Life = MathF.Max(0.05f, Lifetime * Jitter()),
            Size = MathF.Max(0.001f, Size * Jitter()),
            Spin = ((float)_random.NextDouble() - 0.5f) * 2f * SpinRate,
            Seed = (float)_random.NextDouble(),
        };
    }

    /// <summary>Where this particle starts, and which way "out" is from there.</summary>
    private (Vector3 Offset, Vector3 Outward) Spawn()
    {
        float Unit() => (float)_random.NextDouble();
        float Signed() => (Unit() - 0.5f) * 2f;

        switch (Shape)
        {
            case EmitterShape.Point:
                return (Vector3.Zero, Vector3.UnitY);

            case EmitterShape.Box:
            {
                Vector3 offset = new(Signed() * EmitterSize.X, Signed() * EmitterSize.Y, Signed() * EmitterSize.Z);
                return (offset, Normalise(offset));
            }

            case EmitterShape.Sphere:
            {
                Vector3 direction = RandomDirection();
                float radius = SurfaceOnly ? EmitterRadius : EmitterRadius * MathF.Cbrt(Unit());
                return (direction * radius, direction);
            }

            case EmitterShape.Ring:
            {
                float angle = Unit() * MathF.Tau;
                Vector3 offset = new(MathF.Cos(angle) * EmitterRadius, 0f, MathF.Sin(angle) * EmitterRadius);
                return (offset, Normalise(offset));
            }

            case EmitterShape.Cone:
            {
                // Uniform over the cap, then pushed out along the slant.
                float angle = Unit() * MathF.Tau;
                float tilt = ConeAngle * MathF.Sqrt(Unit());
                Vector3 direction = new(
                    MathF.Sin(tilt) * MathF.Cos(angle),
                    MathF.Cos(tilt),
                    MathF.Sin(tilt) * MathF.Sin(angle));
                float radius = EmitterRadius * MathF.Sqrt(Unit());
                return (new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius), direction);
            }

            default:
            {
                float angle = Unit() * MathF.Tau;
                float radius = EmitterRadius * MathF.Sqrt(Unit());
                Vector3 offset = new(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
                return (offset, Normalise(offset));
            }
        }
    }

    private Vector3 RandomDirection()
    {
        // Uniform on the sphere: a cosine spread in z, an even angle around it.
        float z = ((float)_random.NextDouble() * 2f) - 1f;
        float angle = (float)_random.NextDouble() * MathF.Tau;
        float radius = MathF.Sqrt(MathF.Max(0f, 1f - (z * z)));
        return new Vector3(radius * MathF.Cos(angle), z, radius * MathF.Sin(angle));
    }

    private static Vector3 Normalise(Vector3 value) =>
        value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : Vector3.UnitY;

    /// <summary>
    /// Curl of a noise field: divergence free, so particles swirl through it
    /// instead of piling up where the noise happens to be large.
    /// </summary>
    private Vector3 Curl(Vector3 position, float time)
    {
        const float Epsilon = 0.08f;
        Vector3 p = (position * TurbulenceScale) + new Vector3(0f, time * TurbulenceSpeed, 0f);

        float x1 = Potential(p + new Vector3(0f, Epsilon, 0f)).Z - Potential(p - new Vector3(0f, Epsilon, 0f)).Z;
        float x2 = Potential(p + new Vector3(0f, 0f, Epsilon)).Y - Potential(p - new Vector3(0f, 0f, Epsilon)).Y;
        float y1 = Potential(p + new Vector3(0f, 0f, Epsilon)).X - Potential(p - new Vector3(0f, 0f, Epsilon)).X;
        float y2 = Potential(p + new Vector3(Epsilon, 0f, 0f)).Z - Potential(p - new Vector3(Epsilon, 0f, 0f)).Z;
        float z1 = Potential(p + new Vector3(Epsilon, 0f, 0f)).Y - Potential(p - new Vector3(Epsilon, 0f, 0f)).Y;
        float z2 = Potential(p + new Vector3(0f, Epsilon, 0f)).X - Potential(p - new Vector3(0f, Epsilon, 0f)).X;

        return new Vector3(x1 - x2, y1 - y2, z1 - z2) / (2f * Epsilon);
    }

    private static Vector3 Potential(Vector3 p) =>
        new(Noise(p), Noise(p + new Vector3(31.4f, 17.7f, 9.3f)), Noise(p + new Vector3(-7.1f, 23.9f, 41.2f)));

    /// <summary>Value noise, smooth enough that its curl is smooth too.</summary>
    private static float Noise(Vector3 p)
    {
        Vector3 cell = new(MathF.Floor(p.X), MathF.Floor(p.Y), MathF.Floor(p.Z));
        Vector3 f = p - cell;
        Vector3 w = f * f * (new Vector3(3f) - (2f * f));

        float Corner(int x, int y, int z)
        {
            float h = MathF.Sin(((cell.X + x) * 127.1f) + ((cell.Y + y) * 311.7f) + ((cell.Z + z) * 74.7f)) * 43758.5453f;
            return h - MathF.Floor(h);
        }

        float x00 = float.Lerp(Corner(0, 0, 0), Corner(1, 0, 0), w.X);
        float x10 = float.Lerp(Corner(0, 1, 0), Corner(1, 1, 0), w.X);
        float x01 = float.Lerp(Corner(0, 0, 1), Corner(1, 0, 1), w.X);
        float x11 = float.Lerp(Corner(0, 1, 1), Corner(1, 1, 1), w.X);
        return float.Lerp(float.Lerp(x00, x10, w.Y), float.Lerp(x01, x11, w.Y), w.Z);
    }
}
