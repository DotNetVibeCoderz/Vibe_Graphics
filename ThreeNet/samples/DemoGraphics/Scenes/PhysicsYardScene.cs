using System.Numerics;
using DemoGraphics.Framework;
using ThreeNet;
using ThreeNet.Avalonia;

namespace DemoGraphics.Scenes;

/// <summary>
/// A test yard for the Rapier physics: a brick wall, a ramp, a stack of barrels
/// and a wrecking ball on a kinematic arm. Nothing here is faked - the renderer
/// draws whatever the solver says, and Reset puts every body back.
/// </summary>
public sealed class PhysicsYardScene : DemoScene
{
    private readonly List<(Node Node, Vector3 Start)> _bodies = [];
    private Node _ball = null!;
    private Node _arm = null!;
    private int _contacts;
    private float _swing;

    public PhysicsYardScene()
    {
        Declare(
            DemoParameter.Slider("columns", "Wall columns", 8f, 2f, 20f, "", 1f, requiresRebuild: true),
            DemoParameter.Slider("rows", "Wall rows", 6f, 1f, 14f, "", 1f, requiresRebuild: true),
            DemoParameter.Slider("gravity", "Gravity", 9.81f, 0f, 30f, "m/s2"),
            DemoParameter.Slider("bounce", "Restitution", 0.15f, 0f, 1f, "", 0f, requiresRebuild: true),
            DemoParameter.Slider("friction", "Friction", 0.7f, 0f, 2f, "", 0f, requiresRebuild: true),
            DemoParameter.Slider("ballMass", "Ball mass", 600f, 20f, 3000f, "kg", 10f, requiresRebuild: true),
            DemoParameter.Toggle("run", "Run the solver", true),
            DemoParameter.Toggle("wreck", "Swing the ball", true));

        AddPreset("Brick wall", ("columns", 8f), ("rows", 6f), ("bounce", 0.15f), ("wreck", 1f));
        AddPreset("Tall stack", ("columns", 3f), ("rows", 14f), ("bounce", 0.05f), ("friction", 1.4f));
        AddPreset("Bouncy", ("bounce", 0.85f), ("friction", 0.2f));
        AddPreset("Low gravity", ("gravity", 1.6f), ("bounce", 0.4f));
        AddPreset("Heavy ball", ("ballMass", 2400f), ("wreck", 1f));
        AddPreset("Frozen", ("run", 0f));

        AddAction("Reset bodies", ResetBodies);
        AddAction("Nudge the wall", () =>
        {
            foreach ((Node node, Vector3 _) in _bodies)
            {
                Scene.Physics.ApplyImpulse(node, new Vector3(0f, 0f, -220f));
            }
        });
    }

    public override string Id => "PHY";

    public override string Title => "Physics yard";

    public override string Category => "Simulation";

    public override string Summary =>
        "Rapier rigid bodies: a wall, a ramp, barrels and a wrecking ball, with contacts counted.";

    public override IReadOnlyList<string> Features =>
        ["Rapier rigid bodies", "Contact events", "Kinematic bodies", "Shadows", "SSAO"];

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        Shadows = true,
        ShadowMapSize = 2048,
        ShadowCascades = 2,
        ShadowDistance = 45f,
        ShadowSoftness = 1,
        Ssao = true,
        SsaoRadius = 0.4f,
        Bloom = false,
    };

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 2f, 0f);
        orbit.Distance = 20f;
        orbit.Yaw = 0.9f;
        orbit.Pitch = 0.22f;
    }

    public override IEnumerable<(string Label, string Value)> Metrics()
    {
        yield return ("Bodies", _bodies.Count.ToString());
        yield return ("Contacts this second", _contacts.ToString());
        yield return ("Solver", B("run") ? $"{Scene.Physics.FixedTimestep * 1000f:F1} ms steps" : "stopped");
    }

    protected override void OnBuild()
    {
        Scene.Environment = SceneEnvironment.Default with
        {
            Background = Palette.Rgba(0x0C0F14),
            AmbientColor = new Vector3(0.55f, 0.62f, 0.78f),
            AmbientIntensity = 0.11f,
        };

        Material floorMaterial = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x4A4E55), 0f, 0.7f));
        Node floor = Scene.AddMesh(Procedural.Ground(Scene, 80f, 1), floorMaterial, name: "yard");
        Scene.Physics.Add(floor, RigidBodyOptions.Fixed, ColliderOptions.Box(new Vector3(40f, 0.05f, 40f)) with
        {
            Friction = P("friction"),
        });

        // ---------------------------------------------------------- brick wall
        int columns = (int)P("columns");
        int rows = (int)P("rows");
        Geometry brick = Scene.CreateBoxGeometry(0.9f, 0.45f, 0.45f);
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                // Offset every other row, like a real bond.
                float offset = row % 2 == 0 ? 0f : 0.45f;
                Material material = Scene.CreateMaterial(MaterialOptions.Pbr(
                    Palette.Rgba(row % 2 == 0 ? 0x9C4A38u : 0xB05A44u),
                    0f,
                    0.78f));
                Node node = Scene.AddMesh(brick, material, name: $"brick-{row}-{column}");
                Vector3 position = new(
                    (column - ((columns - 1) * 0.5f)) * 0.95f + offset,
                    0.24f + (row * 0.46f),
                    -2f);
                node.Position = position;
                Scene.Physics.Add(node, RigidBodyOptions.Dynamic, ColliderOptions.Box(new Vector3(0.45f, 0.225f, 0.225f)) with
                {
                    Restitution = P("bounce"),
                    Friction = P("friction"),
                    Density = 900f,
                });
                _bodies.Add((node, position));
            }
        }

        // --------------------------------------------------------------- ramp
        Material rampMaterial = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x3A4048), 0f, 0.6f));
        Node ramp = Scene.AddMesh(Scene.CreateBoxGeometry(6f, 0.3f, 4f), rampMaterial, name: "ramp");
        ramp.Position = new Vector3(7.5f, 1.2f, 3f);
        ramp.EulerAngles = new Vector3(0f, 0f, -0.35f);
        Scene.Physics.Add(ramp, RigidBodyOptions.Fixed, ColliderOptions.Box(new Vector3(3f, 0.15f, 2f)) with
        {
            Friction = P("friction"),
        });

        // Barrels to roll down it.
        for (int i = 0; i < 6; i++)
        {
            Material barrelMaterial = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x3F6B4F), 0.2f, 0.5f));
            Node barrel = Scene.AddMesh(Scene.CreateCylinderGeometry(0.4f, 0.4f, 0.9f, 24), barrelMaterial, name: $"barrel-{i}");
            Vector3 position = new(9.2f, 2.6f + (i * 1.05f), 2f + ((i % 2) * 1.2f));
            barrel.Position = position;
            barrel.EulerAngles = new Vector3(0f, 0f, MathF.PI / 2f);
            Scene.Physics.Add(barrel, RigidBodyOptions.Dynamic, ColliderOptions.Cylinder(0.45f, 0.4f) with
            {
                Restitution = P("bounce"),
                Friction = P("friction"),
                Density = 500f,
            });
            _bodies.Add((barrel, position));
        }

        // ------------------------------------------------------- wrecking ball
        _arm = Scene.CreateNode(name: "crane");
        _arm.Position = new Vector3(-1f, 7.5f, 4.5f);
        Material steel = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x8A8F98), 0.9f, 0.3f));
        Node mast = Scene.AddMesh(Scene.CreateCylinderGeometry(0.16f, 0.2f, 7.5f, 14), steel, name: "mast");
        mast.Position = new Vector3(-1f, 3.75f, 4.5f);

        Node chain = Scene.AddMesh(Scene.CreateCylinderGeometry(0.05f, 0.05f, 5f, 8), steel, parent: _arm, name: "chain");
        chain.Position = new Vector3(0f, -2.5f, 0f);

        Material ballMaterial = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x2A2E34), 0.8f, 0.35f));
        _ball = Scene.AddMesh(Scene.CreateSphereGeometry(0.85f, 32, 20), ballMaterial, parent: _arm, name: "wrecking ball");
        _ball.Position = new Vector3(0f, -5f, 0f);
        // Kinematic: the arm drives it, but the bricks still feel it.
        Scene.Physics.Add(_ball, RigidBodyOptions.Kinematic, ColliderOptions.Sphere(0.85f) with
        {
            Restitution = 0.2f,
            Friction = P("friction"),
            Density = P("ballMass") / 3f,
        });

        Node sun = Scene.AddLight(
            Light.Directional(Palette.Kelvin(5600f), 3.4f) with { CastShadow = true, ShadowNormalBias = 1.6f },
            name: "sun");
        sun.Position = new Vector3(10f, 16f, 12f);
        sun.LookAt(Vector3.Zero);
        Scene.AddLight(Light.Ambient(new Vector3(0.5f, 0.6f, 0.82f), 0.4f), name: "sky light");
    }

    protected override void OnApplyParameters() => Scene.Physics.Gravity = new Vector3(0f, -P("gravity"), 0f);

    public override void Update(float deltaSeconds, double totalSeconds)
    {
        if (B("wreck"))
        {
            // A pendulum, driven rather than simulated, so the timing is the same
            // on every run of the benchmark.
            _swing += deltaSeconds;
            _arm.EulerAngles = new Vector3(0f, 0f, MathF.Sin(_swing * 1.15f) * 0.85f);
        }

        if (!B("run"))
        {
            return;
        }

        Scene.Physics.Step(deltaSeconds);
        // Draining the queue every frame is what keeps it from growing; the count
        // is what the read-out shows.
        _contacts = Scene.Physics.TakeContactEvents().Count(contact => contact.Started);
    }

    /// <summary>Puts every body back where it started.</summary>
    public void ResetBodies()
    {
        foreach ((Node node, Vector3 start) in _bodies)
        {
            Scene.Physics.Teleport(node, start, Quaternion.Identity);
            Scene.Physics.SetVelocity(node, Vector3.Zero, Vector3.Zero);
        }
    }

    protected override void OnUnload() => _bodies.Clear();
}
