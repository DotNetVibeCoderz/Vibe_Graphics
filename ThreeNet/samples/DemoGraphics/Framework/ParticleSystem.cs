using System.Numerics;
using ThreeNet;
using ThreeNet.Interop;

namespace DemoGraphics.Framework;

/// <summary>
/// A CPU particle system that writes camera facing quads into one geometry per
/// group and re-uploads it every frame. There is no GPU particle path in the
/// library yet, and this shows what the dynamic geometry API can carry: a few
/// thousand particles per group at one draw call each.
/// </summary>
public sealed class ParticleSystem
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
    }

    public ParticleSystem(int capacity, int seed = 1337)
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

    public int Capacity { get; }

    public int Alive => _alive;

    /// <summary>Particles emitted per second.</summary>
    public float Rate { get; set; } = 400f;

    public float Lifetime { get; set; } = 2.4f;

    public float Size { get; set; } = 0.35f;

    /// <summary>Metres per second upwards at birth.</summary>
    public float Rise { get; set; } = 2.4f;

    /// <summary>Sideways spread at birth, in metres per second.</summary>
    public float Spread { get; set; } = 0.7f;

    /// <summary>Downward acceleration; negative values make embers float.</summary>
    public float Gravity { get; set; } = -1.2f;

    /// <summary>How much of the shared wind the particles pick up.</summary>
    public float WindInfluence { get; set; } = 1f;

    /// <summary>Radius of the emitter disc.</summary>
    public float EmitterRadius { get; set; } = 0.35f;

    public Vector3 Origin { get; set; }

    /// <summary>Size at the end of life relative to the start.</summary>
    public float Growth { get; set; } = 2.2f;

    public void Clear()
    {
        _alive = 0;
        _carry = 0f;
    }

    /// <summary>Ages every particle, emits new ones and applies the wind.</summary>
    public void Update(float deltaSeconds, Vector2 wind)
    {
        Vector3 windVector = new(wind.X * WindInfluence, 0f, wind.Y * WindInfluence);

        for (int i = 0; i < _alive; i++)
        {
            ref Particle particle = ref _particles[i];
            particle.Age += deltaSeconds;
            if (particle.Age >= particle.Life)
            {
                // Swap the dead particle with the last live one: no allocation,
                // no gaps, and the order never matters for additive blending.
                _particles[i] = _particles[--_alive];
                i--;
                continue;
            }

            particle.Velocity += new Vector3(0f, Gravity, 0f) * deltaSeconds;
            particle.Velocity += (windVector - particle.Velocity) * MathF.Min(1f, deltaSeconds * 0.8f);
            particle.Position += particle.Velocity * deltaSeconds;
        }

        _carry += Rate * deltaSeconds;
        int spawn = (int)_carry;
        _carry -= spawn;
        for (int i = 0; i < spawn && _alive < Capacity; i++)
        {
            float angle = (float)_random.NextDouble() * MathF.Tau;
            float radius = MathF.Sqrt((float)_random.NextDouble()) * EmitterRadius;
            _particles[_alive++] = new Particle
            {
                Position = Origin + new Vector3(MathF.Sin(angle) * radius, 0f, MathF.Cos(angle) * radius),
                Velocity = new Vector3(
                    ((float)_random.NextDouble() - 0.5f) * Spread,
                    Rise * (0.6f + ((float)_random.NextDouble() * 0.8f)),
                    ((float)_random.NextDouble() - 0.5f) * Spread),
                Age = 0f,
                Life = Lifetime * (0.65f + ((float)_random.NextDouble() * 0.7f)),
                Size = Size * (0.7f + ((float)_random.NextDouble() * 0.6f)),
                Spin = ((float)_random.NextDouble() - 0.5f) * 2f,
            };
        }
    }

    /// <summary>
    /// Rebuilds the quads facing the camera and uploads them. Particles outside
    /// the live range collapse to a degenerate quad, so the index buffer can stay
    /// the same size all the time.
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
            float spin = particle.Spin * t;
            Vector3 r = ((right * MathF.Cos(spin)) + (up * MathF.Sin(spin))) * size;
            Vector3 u = ((up * MathF.Cos(spin)) - (right * MathF.Sin(spin))) * size;

            int vertex = i * 4;
            _vertices[vertex] = new Vertex(particle.Position - r - u, normal, new Vector2(0f, 0f));
            _vertices[vertex + 1] = new Vertex(particle.Position + r - u, normal, new Vector2(1f, 0f));
            _vertices[vertex + 2] = new Vertex(particle.Position + r + u, normal, new Vector2(1f, 1f));
            _vertices[vertex + 3] = new Vertex(particle.Position - r + u, normal, new Vector2(0f, 1f));
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
}
