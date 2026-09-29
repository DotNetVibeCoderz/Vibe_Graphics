using System.Numerics;
using DemoGraphics.Framework;
using ThreeNet;
using ThreeNet.Avalonia;
using ThreeNet.Scenes;

namespace DemoGraphics.Scenes;

/// <summary>What the sandbox can drop into the scene.</summary>
public enum SpawnKind
{
    Box,
    Sphere,
    Cylinder,
    Cone,
    Torus,
    PointLight,
    SpotLight,
}

/// <summary>
/// An empty stage to try things on. Objects are spawned, dragged on the ground
/// plane, recoloured and thrown around with the physics, and the whole thing
/// saves to a scene document so a setup can be loaded back or handed to the
/// editor.
/// </summary>
public sealed class SandboxScene : DemoScene
{
    private readonly List<Item> _items = [];
    private Node _sun = null!;
    private Material _ground = null!;
    private bool _physics;
    private int _counter;

    private sealed class Item
    {
        public required Node Node { get; init; }

        public required SpawnKind Kind { get; init; }

        public Material? Material { get; init; }

        public Vector3 SpawnPosition { get; set; }
    }

    public SandboxScene()
    {
        Declare(
            DemoParameter.Toggle("grid", "Ground grid", true),
            DemoParameter.Slider("gravity", "Gravity", 9.81f, 0f, 30f, "m/s2"),
            DemoParameter.Slider("bounce", "Restitution", 0.35f, 0f, 1f),
            DemoParameter.Slider("sunAngle", "Sun height", 48f, 4f, 88f, "deg"));
    }

    public override string Id => "SBX";

    public override string Title => "Sandbox";

    public override string Category => "Sandbox";

    public override string Summary => "An empty stage: spawn, drag, light, drop and save.";

    public override IReadOnlyList<string> Features =>
        ["Scene documents", "Rapier physics", "Node picking and dragging", "Shadows"];

    /// <summary>The node the user last clicked, if it is one of the spawned ones.</summary>
    public Node? Selected { get; private set; }

    public int ItemCount => _items.Count;

    public bool PhysicsRunning => _physics;

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        Shadows = true,
        ShadowMapSize = 2048,
        ShadowCascades = 3,
        ShadowDistance = 60f,
        Ssao = true,
        Bloom = true,
        BloomIntensity = 0.3f,
    };

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 1f, 0f);
        orbit.Distance = 18f;
        orbit.Yaw = 0.8f;
        orbit.Pitch = 0.32f;
    }

    public override IEnumerable<(string Label, string Value)> Metrics()
    {
        yield return ("Objects", _items.Count.ToString());
        yield return ("Physics", _physics ? "running" : "stopped");
        yield return ("Selected", Selected?.Name ?? "nothing");
    }

    protected override void OnBuild()
    {
        Scene.Environment = SceneEnvironment.Default with
        {
            Background = Palette.Rgba(0x0B0E13),
            AmbientColor = new Vector3(0.55f, 0.62f, 0.78f),
            AmbientIntensity = 0.1f,
        };

        _ground = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x24282F), 0f, 0.75f));
        Node ground = Scene.AddMesh(Procedural.Ground(Scene, 60f, 1), _ground, name: "ground");
        ground.ReceiveShadow = true;
        Scene.Physics.Add(ground, RigidBodyOptions.Fixed, ColliderOptions.Box(new Vector3(30f, 0.05f, 30f)));

        Material gridMaterial = Scene.CreateMaterial(MaterialOptions.Basic(Palette.Rgba(0x39414D)));
        Node grid = Scene.AddMesh(Scene.CreateGridGeometry(60f, 60), gridMaterial, name: "grid");
        grid.Position = new Vector3(0f, 0.01f, 0f);
        grid.CastShadow = false;
        _items.Add(new Item { Node = grid, Kind = SpawnKind.Box });

        _sun = Scene.AddLight(Light.Directional(Palette.Kelvin(5800f), 3.2f) with { CastShadow = true }, name: "sun");
        Scene.AddLight(Light.Ambient(new Vector3(0.5f, 0.6f, 0.82f), 0.35f), name: "sky light");

        // Something to aim at on an empty stage.
        Spawn(SpawnKind.Box, new Vector3(-1.4f, 0.5f, 0f));
        Spawn(SpawnKind.Sphere, new Vector3(1.4f, 0.6f, 0.6f));
        Spawn(SpawnKind.PointLight, new Vector3(0f, 3.2f, 2.4f));
    }

    protected override void OnApplyParameters()
    {
        _sun.Position = new Vector3(14f, 20f * MathF.Sin(P("sunAngle").ToRadians()), 9f);
        _sun.LookAt(Vector3.Zero);
        Scene.Physics.Gravity = new Vector3(0f, -P("gravity"), 0f);

        if (_items.Count > 0)
        {
            _items[0].Node.Visible = B("grid");
        }
    }

    public override void Update(float deltaSeconds, double totalSeconds)
    {
        if (_physics)
        {
            Scene.Physics.Step(deltaSeconds);
        }
    }

    protected override void OnUnload()
    {
        _items.Clear();
        Selected = null;
        _physics = false;
    }

    // ----------------------------------------------------------- sandbox API

    /// <summary>A clear spot a few metres in front of the camera, on the ground.</summary>
    private Vector3 SpawnPointInFront()
    {
        Vector3 eye = Viewer.WorldPosition;
        Matrix4x4 world = Viewer.WorldMatrix;
        Vector3 forward = Vector3.Normalize(new Vector3(-world.M31, 0f, -world.M33));
        if (forward.LengthSquared() < 1e-4f)
        {
            forward = -Vector3.UnitZ;
        }

        Vector3 at = eye + (forward * 6f);
        return new Vector3(at.X, 0.8f, at.Z);
    }

    /// <summary>Drops a new object in front of the camera.</summary>
    public Node Spawn(SpawnKind kind, Vector3? position = null)
    {
        Vector3 at = position ?? SpawnPointInFront();
        _counter++;
        string name = $"{kind.ToString().ToLowerInvariant()}-{_counter}";

        if (kind is SpawnKind.PointLight or SpawnKind.SpotLight)
        {
            Node lightNode = Scene.AddLight(
                kind == SpawnKind.PointLight
                    ? Light.Point(Palette.Kelvin(3000f), 60f, 18f) with { CastShadow = true }
                    : Light.Spot(Palette.Kelvin(6000f), 220f, 30f, 12f.ToRadians(), 24f.ToRadians()) with { CastShadow = true },
                name: name);
            lightNode.Position = at;
            if (kind == SpawnKind.SpotLight)
            {
                lightNode.LookAt(new Vector3(at.X, 0f, at.Z));
            }

            Material glow = Scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.3f) with
            {
                Emissive = Palette.Kelvin(kind == SpawnKind.PointLight ? 3000f : 6000f),
                EmissiveIntensity = 8f,
            });
            Node marker = Scene.AddMesh(Scene.CreateSphereGeometry(0.16f, 16, 10), glow, parent: lightNode, name: name + " marker");
            marker.CastShadow = false;
            _items.Add(new Item { Node = lightNode, Kind = kind, Material = glow, SpawnPosition = at });
            Selected = lightNode;
            return lightNode;
        }

        Geometry geometry = kind switch
        {
            SpawnKind.Sphere => Scene.CreateSphereGeometry(0.55f, 32, 18),
            SpawnKind.Cylinder => Scene.CreateCylinderGeometry(0.45f, 0.45f, 1.1f, 28),
            SpawnKind.Cone => Scene.CreateConeGeometry(0.55f, 1.1f, 28),
            SpawnKind.Torus => Scene.CreateTorusGeometry(0.5f, 0.2f, 18, 48),
            _ => Scene.CreateBoxGeometry(1f, 1f, 1f, 1),
        };

        uint[] palette = [0xD96C55, 0x6BA368, 0x5B93D6, 0xE0B551, 0xB06BC4, 0xD9D4C8];
        Material material = Scene.CreateMaterial(MaterialOptions.Pbr(
            Palette.Rgba(palette[_counter % palette.Length]),
            0.1f,
            0.45f));

        Node node = Scene.AddMesh(geometry, material, name: name);
        node.Position = at;
        ColliderOptions collider = kind switch
        {
            SpawnKind.Sphere => ColliderOptions.Sphere(0.55f),
            SpawnKind.Cylinder => ColliderOptions.Cylinder(0.55f, 0.45f),
            SpawnKind.Cone => ColliderOptions.ConvexHull(geometry),
            SpawnKind.Torus => ColliderOptions.ConvexHull(geometry),
            _ => ColliderOptions.Box(new Vector3(0.5f)),
        };
        Scene.Physics.Add(node, RigidBodyOptions.Dynamic, collider with { Restitution = P("bounce") });

        _items.Add(new Item { Node = node, Kind = kind, Material = material, SpawnPosition = at });
        Selected = node;
        return node;
    }

    /// <summary>Makes every spawned object draggable on the ground plane.</summary>
    public void EnableDragging(InteractionManager interaction)
    {
        foreach (Item item in _items)
        {
            if (item.Kind is SpawnKind.PointLight or SpawnKind.SpotLight)
            {
                interaction.MakeDraggable(item.Node, DragMode.CameraPlane);
            }
            else
            {
                interaction.MakeDraggable(item.Node, DragMode.GroundPlane);
            }
        }
    }

    public void Select(Node? node)
    {
        if (node is null)
        {
            Selected = null;
            return;
        }

        // Clicking a light marker selects the light it belongs to.
        foreach (Item item in _items)
        {
            if (item.Node.Equals(node) || (node.Parent is { } parent && item.Node.Equals(parent)))
            {
                Selected = item.Node;
                return;
            }
        }

        Selected = null;
    }

    public void DeleteSelected()
    {
        if (Selected is not { } node)
        {
            return;
        }

        int index = _items.FindIndex(item => item.Node.Equals(node));
        if (index <= 0)
        {
            return;   // index 0 is the grid
        }

        Scene.Physics.Remove(node);
        Scene.Remove(node);
        _items.RemoveAt(index);
        Selected = null;
    }

    public void DuplicateSelected()
    {
        if (Selected is not { } node)
        {
            return;
        }

        Item? item = _items.Find(entry => entry.Node.Equals(node));
        if (item is null)
        {
            return;
        }

        Spawn(item.Kind, node.Position + new Vector3(1.2f, 0f, 0f));
    }

    /// <summary>Colour, metallic and roughness of the selected object.</summary>
    public (Vector4 Color, float Metallic, float Roughness)? SelectedMaterial()
    {
        Item? item = Selected is null ? null : _items.Find(entry => entry.Node.Equals(Selected));
        if (item?.Material is not { } material)
        {
            return null;
        }

        MaterialOptions options = material.Options;
        return (options.BaseColor, options.Metallic, options.Roughness);
    }

    public void UpdateSelectedMaterial(Func<MaterialOptions, MaterialOptions> change)
    {
        Item? item = Selected is null ? null : _items.Find(entry => entry.Node.Equals(Selected));
        item?.Material?.Update(change);
    }

    /// <summary>Starts or stops the physics. Stopping puts everything back.</summary>
    public void SetPhysics(bool running)
    {
        _physics = running;
        if (running)
        {
            return;
        }

        foreach (Item item in _items)
        {
            if (item.Kind is SpawnKind.PointLight or SpawnKind.SpotLight || item.SpawnPosition == Vector3.Zero)
            {
                continue;
            }

            Scene.Physics.Teleport(item.Node, item.SpawnPosition, Quaternion.Identity);
        }
    }

    public void Nudge()
    {
        foreach (Item item in _items)
        {
            if (item.Kind is SpawnKind.PointLight or SpawnKind.SpotLight)
            {
                continue;
            }

            Scene.Physics.ApplyImpulse(
                item.Node,
                new Vector3((Noise.Hash(item.Node.Name.Length, _counter) - 0.5f) * 6f, 4f, (Noise.Hash(_counter, 7) - 0.5f) * 6f));
        }
    }

    /// <summary>
    /// Writes the stage as a scene document, which the editor and
    /// <see cref="SceneDocument.Build"/> can both read back.
    /// </summary>
    public string Save(string path)
    {
        SceneDocument document = new() { Name = "DemoGraphics sandbox" };
        // A, B and C are the primitive's own dimensions; see GeometryDefinition.
        document.Geometries.Add(new GeometryDefinition { Id = "box", Kind = GeometryKind.Box, A = 1f, B = 1f, C = 1f });
        document.Geometries.Add(new GeometryDefinition { Id = "sphere", Kind = GeometryKind.Sphere, A = 0.55f });
        document.Geometries.Add(new GeometryDefinition { Id = "cylinder", Kind = GeometryKind.Cylinder, A = 0.45f, B = 0.45f, C = 1.1f });
        document.Geometries.Add(new GeometryDefinition { Id = "cone", Kind = GeometryKind.Cone, A = 0.55f, B = 1.1f });
        document.Geometries.Add(new GeometryDefinition { Id = "torus", Kind = GeometryKind.Torus, A = 0.5f, B = 0.2f });

        int index = 0;
        foreach (Item item in _items.Skip(1))
        {
            index++;
            Vector3 position = item.Node.Position;
            if (item.Kind is SpawnKind.PointLight or SpawnKind.SpotLight)
            {
                document.Nodes.Add(new NodeDefinition
                {
                    Id = item.Node.Name,
                    Name = item.Node.Name,
                    Position = position,
                    Light = new LightDefinition
                    {
                        Type = item.Kind == SpawnKind.PointLight ? LightType.Point : LightType.Spot,
                        Intensity = item.Node.Light?.Intensity ?? 60f,
                        Range = item.Node.Light?.Range ?? 18f,
                        CastShadow = true,
                    },
                });
                continue;
            }

            MaterialOptions options = item.Material?.Options ?? MaterialOptions.Default;
            string materialId = $"material-{index}";
            document.Materials.Add(new MaterialDefinition
            {
                Id = materialId,
                BaseColor = options.BaseColor,
                Metallic = options.Metallic,
                Roughness = options.Roughness,
            });
            document.Nodes.Add(new NodeDefinition
            {
                Id = item.Node.Name,
                Name = item.Node.Name,
                GeometryId = item.Kind.ToString().ToLowerInvariant(),
                MaterialId = materialId,
                Position = position,
                Rotation = item.Node.EulerAngles,
                Scale = item.Node.Scale,
                Physics = new PhysicsDefinition
                {
                    Body = RigidBodyType.Dynamic,
                    Shape = item.Kind == SpawnKind.Sphere ? ColliderShape.Sphere : ColliderShape.Box,
                    Radius = 0.55f,
                    Restitution = P("bounce"),
                },
            });
        }

        document.Save(path);
        return path;
    }
}
