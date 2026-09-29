using System.Numerics;
using DemoGraphics.Framework;
using ThreeNet;
using ThreeNet.Avalonia;

namespace DemoGraphics.Scenes;

/// <summary>
/// Animation from both ends: a skinned glTF model deformed on the CPU from its
/// imported clips, and a mechanism animated by clips built in code. The count of
/// skinned copies is a dial, because skinning is the part that costs.
/// </summary>
public sealed class MotionScene : DemoScene
{
    private readonly List<AnimationPlayer> _players = [];
    private readonly List<Node> _copies = [];
    private Node _crank = null!;
    private Node _piston = null!;
    private AnimationPlayer? _mechanism;
    private string _modelStatus = "not loaded";
    private int _clipCount;

    public MotionScene()
    {
        Declare(
            DemoParameter.Slider("copies", "Skinned copies", 6f, 1f, 64f, "", 1f,
                "Each copy is deformed on the CPU every frame.", requiresRebuild: true),
            DemoParameter.Slider("speed", "Playback speed", 1f, 0f, 3f, "x"),
            DemoParameter.Slider("stagger", "Stagger", 0.35f, 0f, 1f, "",
                0f, "Offsets each copy in time, so they do not move as one."),
            DemoParameter.Slider("machineSpeed", "Mechanism speed", 1f, 0f, 4f, "x"),
            DemoParameter.Toggle("machine", "Run the mechanism", true));

        AddPreset("Single figure", ("copies", 1f), ("speed", 1f));
        AddPreset("Crowd of 24", ("copies", 24f), ("stagger", 0.8f));
        AddPreset("Stress: 64", ("copies", 64f), ("stagger", 1f));
        AddPreset("Slow motion", ("speed", 0.25f), ("machineSpeed", 0.3f));
        AddPreset("Paused", ("speed", 0f), ("machine", 0f));

        AddAction("Restart clips", () =>
        {
            foreach (AnimationPlayer player in _players)
            {
                player.Time = 0f;
            }
        });
    }

    public override string Id => "ANM";

    public override string Title => "Motion";

    public override string Category => "Simulation";

    public override string Summary =>
        "Imported skinned animation beside clips built in code, with the skinning cost on a dial.";

    public override IReadOnlyList<string> Features =>
        ["glTF skinning", "Animation clips in code", "CPU skinning", "Shadows"];

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        Shadows = true,
        ShadowMapSize = 2048,
        ShadowCascades = 2,
        ShadowDistance = 40f,
        Ssao = true,
        SsaoRadius = 0.3f,
        Bloom = true,
        BloomIntensity = 0.3f,
    };

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 1.2f, 0f);
        orbit.Distance = 11f;
        orbit.Yaw = 0.6f;
        orbit.Pitch = 0.18f;
    }

    public override IEnumerable<(string Label, string Value)> Metrics()
    {
        yield return ("Model", _modelStatus);
        yield return ("Clips playing", _players.Count.ToString());
        yield return ("Skinned copies", _copies.Count.ToString());
        yield return ("Imported clips", _clipCount.ToString());
    }

    protected override void OnBuild()
    {
        Scene.Environment = SceneEnvironment.Default with
        {
            Background = Palette.Rgba(0x0B0E13),
            AmbientColor = new Vector3(0.55f, 0.62f, 0.78f),
            AmbientIntensity = 0.12f,
        };

        Material floor = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x20242B), 0f, 0.55f));
        Scene.AddMesh(Procedural.Ground(Scene, 60f, 1), floor, name: "floor");

        Material grid = Scene.CreateMaterial(MaterialOptions.Basic(Palette.Rgba(0x333B46)));
        Node lines = Scene.AddMesh(Scene.CreateGridGeometry(40f, 40), grid, name: "grid");
        lines.Position = new Vector3(0f, 0.01f, 0f);
        lines.CastShadow = false;

        LoadFigures();
        BuildMechanism();

        Node key = Scene.AddLight(
            Light.Spot(Palette.Kelvin(5400f), 320f, 40f, 24f.ToRadians(), 44f.ToRadians()) with { CastShadow = true },
            name: "key");
        key.Position = new Vector3(5f, 8f, 6f);
        key.LookAt(new Vector3(0f, 1f, 0f));
        Scene.AddLight(Light.Ambient(new Vector3(0.5f, 0.6f, 0.8f), 0.32f), name: "fill");
    }

    /// <summary>
    /// Loads the rigged model the repository ships. If it is missing the scene
    /// still runs: the copies become a procedural arm waving from a code built
    /// clip, and the read-out says so.
    /// </summary>
    private void LoadFigures()
    {
        int copies = (int)P("copies");
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "RiggedSimple.glb");
        bool haveModel = File.Exists(path);
        _modelStatus = haveModel ? "RiggedSimple.glb" : "missing, using a code built clip";

        for (int i = 0; i < copies; i++)
        {
            int column = i % 8;
            int row = i / 8;
            Vector3 at = new((column - 3.5f) * 1.8f, 0f, (row * -2.2f) - 1f);

            if (haveModel)
            {
                // Skinned models are imported per copy: clips address the nodes of
                // one import, so a clone would share a pose.
                ImportResult result = Scene.LoadGltf(path);
                result.Root.Position = at;
                result.Root.Scale = new Vector3(0.6f);
                result.Root.SetShadowsRecursive(cast: true, receive: true);
                _copies.Add(result.Root);
                _clipCount = result.AnimationCount;
            }
            else
            {
                Node arm = BuildArm(at, i);
                _copies.Add(arm);
            }
        }

        foreach (AnimationClip clip in Scene.Animations)
        {
            AnimationPlayer player = clip.Play(loop: true);
            player.Speed = P("speed");
            _players.Add(player);
        }

        // Stagger the copies in time so a crowd does not move in lockstep.
        for (int i = 0; i < _players.Count; i++)
        {
            _players[i].Time = i * 0.21f * P("stagger");
        }
    }

    /// <summary>A jointed arm plus a clip that waves it, for when the model is absent.</summary>
    private Node BuildArm(Vector3 at, int index)
    {
        Material material = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0xB8BCC4), 0.6f, 0.35f));
        Node shoulder = Scene.CreateNode(name: $"arm-{index}");
        shoulder.Position = at + new Vector3(0f, 1.2f, 0f);

        Node upper = Scene.AddMesh(Scene.CreateBoxGeometry(0.22f, 0.9f, 0.22f), material, parent: shoulder, name: "upper");
        upper.Position = new Vector3(0f, -0.45f, 0f);

        Node elbow = Scene.CreateNode(shoulder, "elbow");
        elbow.Position = new Vector3(0f, -0.9f, 0f);
        Node lower = Scene.AddMesh(Scene.CreateBoxGeometry(0.18f, 0.8f, 0.18f), material, parent: elbow, name: "lower");
        lower.Position = new Vector3(0f, -0.4f, 0f);

        Scene.CreateAnimation($"wave-{index}")
            .AddRotation(
                shoulder,
                [0f, 1.2f, 2.4f],
                [
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.5f),
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f),
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.5f),
                ])
            .AddRotation(
                elbow,
                [0f, 0.6f, 1.2f, 2.4f],
                [
                    Quaternion.Identity,
                    Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.9f),
                    Quaternion.Identity,
                    Quaternion.Identity,
                ]);

        return shoulder;
    }

    /// <summary>A crank and piston driven by a clip built in code.</summary>
    private void BuildMechanism()
    {
        Material steel = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x9AA0A9), 0.85f, 0.28f));
        Material brass = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0xC8A058), 1f, 0.22f));

        Node frame = Scene.CreateNode(name: "mechanism");
        frame.Position = new Vector3(6.5f, 0f, 1.5f);

        Node bed = Scene.AddMesh(Scene.CreateBoxGeometry(3.4f, 0.3f, 1.6f), steel, parent: frame, name: "bed");
        bed.Position = new Vector3(0f, 0.15f, 0f);

        _crank = Scene.CreateNode(frame, "crank");
        _crank.Position = new Vector3(-1f, 1.2f, 0f);
        Node disc = Scene.AddMesh(Scene.CreateCylinderGeometry(0.62f, 0.62f, 0.18f, 32), brass, parent: _crank, name: "disc");
        disc.EulerAngles = new Vector3(MathF.PI / 2f, 0f, 0f);
        Node pin = Scene.AddMesh(Scene.CreateCylinderGeometry(0.1f, 0.1f, 0.5f, 16), steel, parent: _crank, name: "pin");
        pin.Position = new Vector3(0.45f, 0f, 0.3f);
        pin.EulerAngles = new Vector3(MathF.PI / 2f, 0f, 0f);

        _piston = Scene.AddMesh(Scene.CreateCylinderGeometry(0.34f, 0.34f, 1.1f, 24), brass, parent: frame, name: "piston");
        _piston.Position = new Vector3(1.2f, 1.2f, 0f);
        _piston.EulerAngles = new Vector3(0f, 0f, MathF.PI / 2f);

        Node guide = Scene.AddMesh(Scene.CreateBoxGeometry(1.3f, 0.9f, 0.9f), steel, parent: frame, name: "guide");
        guide.Position = new Vector3(1.9f, 1.2f, 0f);

        // Two channels on two nodes: the crank turns, the piston slides in step.
        _mechanism = Scene.CreateAnimation("mechanism")
            .AddRotation(
                _crank,
                [0f, 1f, 2f],
                [
                    Quaternion.Identity,
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI),
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Tau),
                ])
            .AddTranslation(
                _piston,
                [0f, 0.5f, 1f, 1.5f, 2f],
                [
                    new Vector3(1.2f, 1.2f, 0f),
                    new Vector3(1.6f, 1.2f, 0f),
                    new Vector3(1.2f, 1.2f, 0f),
                    new Vector3(0.8f, 1.2f, 0f),
                    new Vector3(1.2f, 1.2f, 0f),
                ])
            .Play(loop: true);
        _players.Add(_mechanism);
    }

    protected override void OnApplyParameters()
    {
        float speed = P("speed");
        foreach (AnimationPlayer player in _players)
        {
            player.Speed = ReferenceEquals(player, _mechanism) ? P("machineSpeed") : speed;
        }

        if (_mechanism is not null)
        {
            _mechanism.Speed = B("machine") ? P("machineSpeed") : 0f;
        }
    }

    public override void Update(float deltaSeconds, double totalSeconds) => Scene.UpdateAnimations(deltaSeconds);

    protected override void OnUnload()
    {
        _players.Clear();
        _copies.Clear();
        _mechanism = null;
    }
}
