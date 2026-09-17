using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>
/// Node events and the native HUD: hover highlights, click to recolour, drag
/// crates across the floor, HUD buttons, and a live gamepad readout.
/// </summary>
public sealed class InteractionHudSample : GallerySample
{
    private static readonly Vector4[] Palette =
    [
        MathHelpers.FromHex(0xE76F51), MathHelpers.FromHex(0x2A9D8F), MathHelpers.FromHex(0xE9C46A),
        MathHelpers.FromHex(0x8AB17D), MathHelpers.FromHex(0x9B5DE5),
    ];

    private readonly List<(Node Node, Material Material)> _crates = [];
    private readonly Gamepads _pads = new();
    private Node? _spinner;
    private OverlayElement? _status;
    private OverlayElement? _padText;
    private OverlayElement? _spinButton;
    private OverlayElement? _spinLabel;
    private OverlayElement? _shuffleButton;
    private bool _spinning = true;
    private int _clicks;

    public override string Title => "Interaction & HUD";

    public override string Category => "Interaction";

    public override string Summary =>
        "InteractionManager node events (hover, click, drag on the ground plane), native HUD panels, text and buttons, and gamepad input.";

    public override void Build(Scene scene)
    {
        _crates.Clear();
        _clicks = 0;

        Material floorMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x3B4252), 0f, 0.9f));
        Node floor = scene.AddMesh(scene.CreatePlaneGeometry(30f, 30f), floorMaterial, name: "floor");
        floor.EulerAngles = new Vector3(-MathF.PI / 2f, 0f, 0f);

        Geometry crate = scene.CreateBoxGeometry(1f, 1f, 1f);
        for (int i = 0; i < 5; i++)
        {
            Material material = scene.CreateMaterial(MaterialOptions.Pbr(Palette[i], 0.1f, 0.5f));
            // A group per crate: handlers on the group also see hits on its children.
            Node group = scene.CreateNode(name: $"crate {i + 1}");
            group.Position = new Vector3((i - 2) * 1.8f, 0.5f, 0f);
            scene.AddMesh(crate, material, group, "body");
            Node lid = scene.AddMesh(scene.CreateBoxGeometry(1.1f, 0.12f, 1.1f), material, group, "lid");
            lid.Position = new Vector3(0f, 0.56f, 0f);
            _crates.Add((group, material));
        }

        _spinner = scene.AddMesh(scene.CreateTorusGeometry(0.6f, 0.2f, 24, 64),
            scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0xD8DEE9), 0.9f, 0.25f)), name: "spinner");
        _spinner.Position = new Vector3(0f, 2.6f, -2f);

        Node sun = scene.AddLight(Light.Directional(Vector3.One, 3f) with { CastShadow = true }, name: "sun");
        sun.Position = new Vector3(4f, 8f, 5f);
        sun.LookAt(Vector3.Zero);
        scene.Environment = scene.Environment with
        {
            Background = MathHelpers.FromHex(0x1E222A),
            AmbientIntensity = 0.3f,
        };

        BuildHud(scene);
    }

    private void BuildHud(Scene scene)
    {
        Overlay hud = scene.Overlay;
        OverlayElement panel = hud.Add(new OverlayElementOptions
        {
            Anchor = OverlayAnchor.TopLeft,
            Offset = new Vector2(16f, 16f),
            Size = new Vector2(300f, 124f),
            Color = new Vector4(0.08f, 0.09f, 0.12f, 0.82f),
            BorderColor = new Vector4(1f, 1f, 1f, 0.15f),
            BorderWidth = 1f,
            CornerRadius = 12f,
        });
        hud.AddText("Three.Net HUD", new Vector2(16f, 12f), 20f, Vector4.One, parent: panel);
        _status = hud.AddText("Hover or drag a crate", new Vector2(16f, 44f), 15f, new Vector4(0.75f, 0.8f, 0.9f, 1f), parent: panel);
        _padText = hud.AddText("No gamepad", new Vector2(16f, 70f), 13f, new Vector4(0.6f, 0.65f, 0.75f, 1f), parent: panel);

        // A button is an interactive panel; its label is a child text element.
        _spinButton = hud.AddPanel(new Vector2(-16f, 16f), new Vector2(130f, 36f), new Vector4(0.16f, 0.18f, 0.24f, 0.92f),
            OverlayAnchor.TopRight, cornerRadius: 8f, interactive: true);
        _spinLabel = hud.AddText("Pause spin", Vector2.Zero, 16f, Vector4.One, OverlayAnchor.Center, _spinButton,
            new Vector2(130f, 36f), OverlayTextAlign.Center);
        _shuffleButton = hud.AddButton("Shuffle", new Vector2(-16f, 60f), new Vector2(130f, 36f), OverlayAnchor.TopRight);
    }

    public override void ConfigureInteraction(InteractionManager interaction)
    {
        foreach ((Node node, Material material) in _crates)
        {
            interaction
                .OnPointerEnter(node, e =>
                {
                    material.Update(o => o with { Emissive = new Vector3(0.25f), EmissiveIntensity = 1f });
                    SetStatus($"Hovering {e.Target.Name}");
                })
                .OnPointerLeave(node, _ => material.Update(o => o with { Emissive = Vector3.Zero }))
                .OnClick(node, e =>
                {
                    _clicks++;
                    material.BaseColor = Palette[_clicks % Palette.Length];
                    SetStatus($"Clicked {e.Target.Name} ({_clicks} clicks)");
                })
                .MakeDraggable(node, DragMode.GroundPlane, e => SetStatus($"Dragging {e.Target.Name} to {e.DragPoint.X:0.0}, {e.DragPoint.Z:0.0}"));
        }

        if (_spinButton is { } spin)
        {
            // Hover feedback on the HUD button.
            interaction
                .OnPointerEnter(spin, button => button.Update(o => o with { Color = new Vector4(0.26f, 0.3f, 0.4f, 0.95f) }))
                .OnPointerLeave(spin, button => button.Update(o => o with { Color = new Vector4(0.16f, 0.18f, 0.24f, 0.92f) }))
                .OnClick(spin, _ =>
                {
                    _spinning = !_spinning;
                    _spinLabel?.Text = _spinning ? "Pause spin" : "Resume spin";
                    SetStatus(_spinning ? "Spinning" : "Spin paused");
                });
        }

        if (_shuffleButton is { } shuffle)
        {
            interaction.OnClick(shuffle, _ =>
            {
                Random random = new();
                foreach ((Node node, Material _) in _crates)
                {
                    node.Position = new Vector3((random.NextSingle() - 0.5f) * 10f, 0.5f, (random.NextSingle() - 0.5f) * 6f);
                }

                SetStatus("Shuffled");
            });
        }
    }

    public override void Update(Scene scene, float deltaSeconds, double totalSeconds)
    {
        if (_spinning && _spinner is not null)
        {
            _spinner.EulerAngles = new Vector3((float)totalSeconds * 0.8f, (float)totalSeconds * 1.3f, 0f);
        }

        _pads.Update();
        if (_padText is not null)
        {
            string text = _pads.Connected is [var pad, ..]
                ? $"{pad.Name}: L {pad.LeftStick.X:0.00},{pad.LeftStick.Y:0.00}  RT {pad.RightTrigger:0.00}  {pad.Buttons}"
                : _pads.PlatformError ?? "No gamepad connected";
            if (_padText.Text != text)
            {
                _padText.Text = text;
            }

            // The left stick nudges the first crate.
            if (_pads.Connected is [var first, ..] && _crates.Count > 0)
            {
                Node crate = _crates[0].Node;
                crate.Position += new Vector3(first.LeftStick.X, 0f, -first.LeftStick.Y) * deltaSeconds * 4f;
            }
        }
    }

    private void SetStatus(string text)
    {
        if (_status is not null && _status.Text != text)
        {
            _status.Text = text;
        }
    }

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 0.8f, 0f);
        orbit.Distance = 11f;
        orbit.Yaw = 0.2f;
        orbit.Pitch = 0.5f;
    }
}
