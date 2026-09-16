using System.Numerics;
using ThreeNet;

namespace MotoCross.Game;

/// <summary>
/// Billboarded dust, roost and landing puffs. A fixed pool of quads is recycled,
/// so the effect never allocates once the game is running.
/// </summary>
public sealed class DustSystem
{
    private struct Particle
    {
        public Node Node;
        public Vector3 Position;
        public Vector3 Velocity;
        public float Life;
        public float MaxLife;
        public float Size;
        public float Spin;
        public bool Active;
    }

    private readonly Particle[] _particles;
    private int _cursor;

    public DustSystem(Scene scene, int capacity = 160)
    {
        Texture puff = ProceduralTextures.RadialPuff(scene, new Vector3(0.55f, 0.44f, 0.32f));
        Material material = scene.CreateMaterial(MaterialOptions.Basic(new Vector4(1f, 0.92f, 0.8f, 0.55f)) with
        {
            BaseColorMap = puff,
            AlphaMode = AlphaMode.Blend,
            CullMode = CullMode.None,
            DepthWrite = false,
            RenderOrder = 100,
        });
        Geometry quad = scene.CreatePlaneGeometry();

        _particles = new Particle[capacity];
        for (int i = 0; i < capacity; i++)
        {
            Node node = scene.AddMesh(quad, material, name: "dust");
            node.Visible = false;
            node.CastShadow = false;
            node.ReceiveShadow = false;
            _particles[i] = new Particle { Node = node };
        }
    }

    /// <summary>Spawns a burst; velocities are randomised around <paramref name="velocity"/>.</summary>
    public void Spawn(Vector3 position, Vector3 velocity, int count, float size, float life = 1.1f)
    {
        for (int i = 0; i < count; i++)
        {
            ref Particle particle = ref _particles[_cursor];
            _cursor = (_cursor + 1) % _particles.Length;

            float spread = 1.4f;
            particle.Position = position + new Vector3(Random(0.3f), Random(0.15f), Random(0.3f));
            particle.Velocity = velocity + new Vector3(Random(spread), Random(spread * 0.5f) + 0.6f, Random(spread));
            particle.MaxLife = life * (0.7f + (Shared.NextSingle() * 0.6f));
            particle.Life = particle.MaxLife;
            particle.Size = size * (0.7f + (Shared.NextSingle() * 0.8f));
            particle.Spin = Random(1.6f);
            particle.Active = true;
            particle.Node.Visible = true;
        }
    }

    public void Update(float dt, Vector3 cameraPosition)
    {
        for (int i = 0; i < _particles.Length; i++)
        {
            ref Particle particle = ref _particles[i];
            if (!particle.Active)
            {
                continue;
            }

            particle.Life -= dt;
            if (particle.Life <= 0f)
            {
                particle.Active = false;
                particle.Node.Visible = false;
                continue;
            }

            particle.Velocity *= MathF.Exp(-1.6f * dt);
            particle.Velocity.Y += 0.5f * dt;
            particle.Position += particle.Velocity * dt;

            float age = 1f - (particle.Life / particle.MaxLife);
            // Puffs grow as they rise, then shrink away as they fade.
            float scale = particle.Size * (0.4f + (age * 1.8f)) * (1f - (age * age * 0.7f));
            particle.Node.Position = particle.Position;
            particle.Node.Scale = new Vector3(scale);
            // Billboard: face the camera, with a slow roll for variety.
            particle.Node.LookAt(cameraPosition);
            particle.Node.Rotation = Quaternion.Concatenate(
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, particle.Spin * age * 3f),
                particle.Node.Rotation);
        }
    }

    private static readonly Random Shared = new(4242);

    private static float Random(float magnitude) => ((Shared.NextSingle() * 2f) - 1f) * magnitude;
}
