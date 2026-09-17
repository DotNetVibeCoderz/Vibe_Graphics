using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>
/// Rapier physics and spatial audio: a tower of crates, a hinged gate, balls
/// fired from the camera on click, impact sounds positioned where bodies hit.
/// </summary>
public sealed class PhysicsSample : GallerySample
{
    private readonly List<Node> _crates = [];
    private readonly Dictionary<uint, double> _lastImpact = [];
    private Scene? _scene;
    private AudioEngine? _audio;
    private AudioClip? _thud;
    private Geometry? _ballGeometry;
    private Material? _ballMaterial;
    private OverlayElement? _counter;
    private int _balls;
    private double _time;

    public override string Title => "Physics & spatial audio";

    public override string Category => "Physics";

    public override string Summary =>
        "Rapier rigid bodies, colliders, a hinge joint and contact events, with 3D positioned impact sounds. Click to throw a ball.";

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with { Shadows = true, ShadowMapSize = 2048 };

    public override void Build(Scene scene)
    {
        _scene = scene;
        _crates.Clear();
        _lastImpact.Clear();
        _balls = 0;

        Material floorMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x4C566A), 0f, 0.9f));
        Node floor = scene.AddMesh(scene.CreateBoxGeometry(40f, 1f, 40f), floorMaterial, name: "floor");
        floor.Position = new Vector3(0f, -0.5f, 0f);
        scene.Physics.AddCollider(floor, ColliderOptions.Box(new Vector3(20f, 0.5f, 20f)));

        Geometry crate = scene.CreateBoxGeometry(1f, 1f, 1f);
        for (int layer = 0; layer < 6; layer++)
        {
            for (int i = 0; i < 4 - (layer / 2); i++)
            {
                Material material = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(layer % 2 == 0 ? 0xD08770u : 0xEBCB8Bu), 0f, 0.7f));
                Node node = scene.AddMesh(crate, material, name: $"crate {layer}-{i}");
                node.Position = new Vector3((i - 1.5f + (layer / 2 * 0.5f)) * 1.05f, 0.5f + layer * 1.01f, -3f);
                scene.Physics.Add(node, RigidBodyOptions.Dynamic, ColliderOptions.Box(new Vector3(0.5f)) with { Friction = 0.7f });
                _crates.Add(node);
            }
        }

        // A gate on a hinge: a fixed post and a swinging panel.
        Material metal = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x88C0D0), 0.8f, 0.3f));
        Node post = scene.AddMesh(scene.CreateBoxGeometry(0.2f, 2.4f, 0.2f), metal, name: "post");
        post.Position = new Vector3(4f, 1.2f, 1f);
        // Collision groups: the post is group 2 and the gate ignores it, so the hinge turns freely.
        scene.Physics.Add(post, RigidBodyOptions.Fixed, ColliderOptions.Box(new Vector3(0.1f, 1.2f, 0.1f)) with { Membership = 2 });
        Node gate = scene.AddMesh(scene.CreateBoxGeometry(2f, 1.8f, 0.08f), metal, name: "gate");
        gate.Position = new Vector3(5.1f, 1.2f, 1f);
        scene.Physics.Add(gate, RigidBodyOptions.Dynamic with { GravityScale = 0f, AngularDamping = 0.4f },
            ColliderOptions.Box(new Vector3(1f, 0.9f, 0.04f)) with { Filter = ~2u });
        scene.Physics.AddJoint(JointType.Hinge, post, gate, new Vector3(0.1f, 0f, 0f), new Vector3(-1f, 0f, 0f), Vector3.UnitY);

        _ballGeometry = scene.CreateSphereGeometry(0.25f, 24, 16);
        _ballMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0xBF616A), 0.2f, 0.3f));

        Node sun = scene.AddLight(Light.Directional(Vector3.One, 3f) with { CastShadow = true }, name: "sun");
        sun.Position = new Vector3(5f, 10f, 6f);
        sun.LookAt(Vector3.Zero);
        scene.Environment = scene.Environment with
        {
            Background = MathHelpers.FromHex(0x2E3440),
            AmbientIntensity = 0.3f,
        };

        _counter = scene.Overlay.AddText("Click to throw a ball", new Vector2(16f, 16f), 18f, Vector4.One);
        scene.Physics.Contact += OnContact;

        try
        {
            _audio?.Dispose();
            _audio = AudioEngine.Open();
            _thud = _audio.CreateClip(Thud(_audio.SampleRate), 1, _audio.SampleRate);
        }
        catch (ThreeNetException)
        {
            _audio = null; // no output device
        }
    }

    /// <summary>A short decaying low thump with a click of noise on top.</summary>
    private static float[] Thud(int sampleRate)
    {
        Random random = new(3);
        float[] samples = new float[sampleRate / 4];
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)sampleRate;
            float envelope = MathF.Exp(-t * 28f);
            samples[i] = envelope * ((MathF.Sin(t * MathF.Tau * (90f - (t * 120f))) * 0.8f) + ((random.NextSingle() - 0.5f) * MathF.Exp(-t * 120f)));
        }

        return samples;
    }

    private void OnContact(ContactEvent contact)
    {
        if (!contact.Started || _audio is null || _thud is null || _scene is null)
        {
            return;
        }

        // Rate limit per body so resting stacks do not buzz.
        uint key = Math.Min(contact.NodeA.Id, contact.NodeB.Id) * 7919u + Math.Max(contact.NodeA.Id, contact.NodeB.Id);
        if (_lastImpact.TryGetValue(key, out double last) && _time - last < 0.25)
        {
            return;
        }

        _lastImpact[key] = _time;
        Node moving = _scene.Physics.HasBody(contact.NodeA) ? contact.NodeA : contact.NodeB;
        float speed = _scene.Physics.HasBody(moving) ? _scene.Physics.GetVelocity(moving).Linear.Length() : 1f;
        if (speed < 0.6f)
        {
            return;
        }

        _audio.Play(_thud, SoundOptions.At(moving.WorldPosition, gain: MathF.Min(1f, speed / 6f)) with
        {
            Pitch = 0.8f + (Random.Shared.NextSingle() * 0.4f),
            MinDistance = 2f,
        });
    }

    public override void OnPick(Scene scene, Ray ray)
    {
        if (_ballGeometry is null || _ballMaterial is null)
        {
            return;
        }

        Node ball = scene.AddMesh(_ballGeometry, _ballMaterial, name: $"ball {++_balls}");
        ball.Position = ray.Origin + (ray.Direction * 1.5f);
        scene.Physics.Add(ball,
            RigidBodyOptions.Dynamic with { LinearVelocity = ray.Direction * 22f, ContinuousCollision = true },
            ColliderOptions.Sphere(0.25f) with { Density = 4f, Restitution = 0.3f, Membership = 1 });
        _counter?.Text = $"Balls thrown: {_balls}";
    }

    public override void Update(Scene scene, float deltaSeconds, double totalSeconds)
    {
        _time = totalSeconds;
        scene.Physics.Step(deltaSeconds);
        _audio?.Update(scene, scene.ActiveCamera, deltaSeconds);
    }

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 2f, -1f);
        orbit.Distance = 14f;
        orbit.Yaw = 0.35f;
        orbit.Pitch = 0.3f;
    }
}
