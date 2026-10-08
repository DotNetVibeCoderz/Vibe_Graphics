using System.Numerics;
using DemoGraphics.Framework;
using ThreeNet;
using ThreeNet.Avalonia;

namespace DemoGraphics.Scenes;

/// <summary>
/// Facial expressions driven by morph targets: one slider per blend shape, read
/// from the model rather than hard coded, plus presets that mix them.
/// </summary>
/// <remarks>
/// The character ships without blend shapes, so they are authored in Blender by
/// <c>tools/blender/face-expressions.py</c> and exported as glTF morph targets.
/// When that file is missing the scene falls back to a head built here with
/// targets added through <see cref="Geometry.AddMorphTarget"/>, so the same
/// sliders work either way.
/// </remarks>
public sealed class FaceScene : DemoScene
{
    private const string ModelFile = "grandma-expressions.glb";

    private readonly List<string> _targets = [];
    private Node _face = null!;
    private Node _subject = null!;
    private string _source = "none";
    private float _blinkClock;
    private float _nextBlink = 2.5f;
    private float _blink;

    public override string Id => "FAC";

    public override string Title => "Facial expressions";

    public override string Category => "Simulation";

    public override string Summary =>
        "Blend shapes on a character: a slider per morph target, mixed into expressions.";

    public override IReadOnlyList<string> Features =>
        ["Morph targets", "glTF blend shapes", "CPU deformation", "Shadows", "SSAO"];

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        Shadows = true,
        ShadowMapSize = 2048,
        ShadowCascades = 2,
        ShadowDistance = 8f,
        ShadowSoftness = 2,
        Ssao = true,
        SsaoRadius = 0.08f,
        SsaoIntensity = 1.4f,
        Bloom = true,
        BloomIntensity = 0.25f,
        Exposure = 1.1f,
    };

    public override void ConfigureCamera(OrbitController orbit)
    {
        // Framed on the face, which sits near the top of a 1.75 m figure.
        orbit.Target = new Vector3(0f, 0.52f, 0f);
        orbit.Distance = 0.62f;
        orbit.Yaw = 0f;
        orbit.Pitch = 0.04f;
        orbit.MinDistance = 0.2f;
        orbit.MaxDistance = 4f;
    }

    public override IReadOnlyList<CameraKey> CameraPath =>
    [
        new(new Vector3(0f, 0.52f, 0f), 0.62f, 0.0f, 0.04f, 0f),
        new(new Vector3(0f, 0.52f, 0f), 0.42f, 0.5f, 0.02f, 4f),
        new(new Vector3(0f, 0.45f, 0f), 0.95f, -0.6f, 0.15f, 8f),
        new(new Vector3(0f, 0.52f, 0f), 0.62f, 0.0f, 0.04f, 12f),
    ];

    public override IEnumerable<(string Label, string Value)> Metrics()
    {
        yield return ("Model", _source);
        yield return ("Blend shapes", _targets.Count.ToString());
        yield return ("Deformed", _face is null ? "-" : $"{VertexCount():N0} vertices on the CPU");
        yield return ("Strongest", Strongest());
    }

    protected override void OnBuild()
    {
        ResetDeclarations();
        _targets.Clear();

        Scene.Environment = SceneEnvironment.Default with
        {
            Background = Palette.Rgba(0x12151B),
            AmbientColor = new Vector3(0.55f, 0.6f, 0.72f),
            AmbientIntensity = 0.25f,
        };

        BuildStudio();

        string path = Path.Combine(AppContext.BaseDirectory, "Assets", ModelFile);
        if (File.Exists(path))
        {
            ImportResult imported = Scene.LoadGltf(path);
            _subject = imported.Root;
            _face = FindMorphed(imported.Root) ?? imported.Root;
            _source = ModelFile;
        }
        else
        {
            _face = BuildFallbackHead();
            _subject = _face;
            _source = $"{ModelFile} not found, using a built-in head";
        }

        if (_face.Geometry is { } geometry)
        {
            _targets.AddRange(geometry.MorphTargetNames);
        }

        DeclareShapes();
    }

    /// <summary>One slider per blend shape, named by the model.</summary>
    private void DeclareShapes()
    {
        List<DemoParameter> parameters = [];
        foreach (string name in _targets)
        {
            parameters.Add(DemoParameter.Slider(Key(name), Humanise(name), 0f, 0f, 1f));
        }

        parameters.Add(DemoParameter.Toggle("idle", "Idle life", true, "Blinks and breathes between poses."));
        parameters.Add(DemoParameter.Slider("overdrive", "Overdrive", 1f, 0.5f, 1.8f, "x", 0f,
            "Scales every weight; past 1 the shapes push beyond what was authored."));
        Declare([.. parameters]);

        // Presets name the shapes they need, so a model with other blend shapes
        // simply gets fewer of them.
        Expression("Neutral");
        Expression("Happy", ("smile", 0.95f), ("cheekPuff", 0.2f), ("browRaise", 0.35f), ("squint", 0.25f));
        Expression("Sad", ("frown", 0.85f), ("browFurrow", 0.45f));
        Expression("Surprised", ("jawOpen", 0.9f), ("browRaise", 1f));
        Expression("Suspicious", ("squint", 0.85f), ("browFurrow", 0.65f), ("pucker", 0.3f));
        Expression("Kiss", ("pucker", 1f), ("cheekPuff", 0.35f));
        Expression("Grumpy", ("frown", 0.6f), ("browFurrow", 0.9f), ("squint", 0.5f));

        AddAction("Reset the face", () =>
        {
            foreach (string name in _targets)
            {
                SetParameter(Key(name), 0f);
            }
        });
    }

    private void Expression(string name, params (string Shape, float Weight)[] shapes)
    {
        List<(string, float)> values = [];
        foreach (string target in _targets)
        {
            float weight = 0f;
            foreach ((string shape, float amount) in shapes)
            {
                if (string.Equals(shape, target, StringComparison.OrdinalIgnoreCase))
                {
                    weight = amount;
                }
            }

            values.Add((Key(target), weight));
        }

        AddPreset(name, [.. values]);
    }

    protected override void OnApplyParameters() => PushWeights();

    public override void Update(float deltaSeconds, double totalSeconds)
    {
        if (B("idle"))
        {
            // A blink every few seconds, and a slow breath under everything.
            _blinkClock += deltaSeconds;
            if (_blinkClock >= _nextBlink)
            {
                _blinkClock = 0f;
                _nextBlink = 2.2f + (Noise.Hash((int)totalSeconds, 7) * 3.5f);
            }

            float since = _blinkClock;
            _blink = since < 0.16f ? MathF.Sin(since / 0.16f * MathF.PI) : 0f;
            _subject.Position = new Vector3(0f, MathF.Sin((float)totalSeconds * 1.1f) * 0.004f, 0f);
            PushWeights();
        }
        else if (_blink > 0f)
        {
            _blink = 0f;
            PushWeights();
        }

        Scene.UpdateAnimations(deltaSeconds);
    }

    /// <summary>Copies the sliders onto the node, where the deformation reads them.</summary>
    private void PushWeights()
    {
        if (_face is null || _targets.Count == 0)
        {
            return;
        }

        float gain = P("overdrive");
        Span<float> weights = stackalloc float[_targets.Count];
        for (int i = 0; i < _targets.Count; i++)
        {
            float value = P(Key(_targets[i])) * gain;
            // The idle blink rides on top of whatever the sliders say.
            if (_blink > 0f && _targets[i].Contains("squint", StringComparison.OrdinalIgnoreCase))
            {
                value = MathF.Max(value, _blink);
            }

            weights[i] = value;
        }

        _face.SetMorphWeights(weights);
    }

    private int VertexCount() => _face.Geometry?.Counts.Vertices ?? 0;

    private string Strongest()
    {
        string name = "rest";
        float best = 0.02f;
        foreach (string target in _targets)
        {
            float value = P(Key(target));
            if (value > best)
            {
                best = value;
                name = $"{Humanise(target).ToLowerInvariant()} {value:P0}";
            }
        }

        return name;
    }

    private static string Key(string target) => "shape:" + target;

    /// <summary>"browFurrow" reads as "Brow furrow" on a control sheet.</summary>
    private static string Humanise(string name)
    {
        if (name.Length == 0)
        {
            return name;
        }

        System.Text.StringBuilder text = new();
        text.Append(char.ToUpperInvariant(name[0]));
        foreach (char c in name.AsSpan(1))
        {
            if (char.IsUpper(c))
            {
                text.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    private static Node? FindMorphed(Node node)
    {
        if (node.MorphWeightCount > 0)
        {
            return node;
        }

        foreach (Node child in node.Children)
        {
            if (FindMorphed(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void BuildStudio()
    {
        Material floor = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x1A1E25), 0f, 0.65f));
        Scene.AddMesh(Procedural.Ground(Scene, 12f, 1), floor, name: "floor");

        Material backdrop = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0x232833), 0f, 0.9f));
        Node wall = Scene.AddMesh(Scene.CreateBoxGeometry(6f, 4f, 0.1f), backdrop, name: "backdrop");
        wall.Position = new Vector3(0f, 2f, -1.4f);

        // Portrait lighting: a soft key in front, a cool rim behind.
        Node key = Scene.AddLight(
            Light.Spot(Palette.Kelvin(5200f), 26f, 6f, 22f.ToRadians(), 40f.ToRadians()) with { CastShadow = true },
            name: "key");
        key.Position = new Vector3(0.75f, 1.25f, 1.1f);
        key.LookAt(new Vector3(0f, 0.52f, 0f));

        Node rim = Scene.AddLight(Light.Spot(Palette.Kelvin(8000f), 14f, 5f, 18f.ToRadians(), 34f.ToRadians()), name: "rim");
        rim.Position = new Vector3(-0.9f, 1.1f, -0.7f);
        rim.LookAt(new Vector3(0f, 0.55f, 0f));

        Node fill = Scene.AddLight(Light.Point(Palette.Kelvin(4000f), 1.6f, 3f), name: "fill");
        fill.Position = new Vector3(-0.6f, 0.6f, 0.9f);

        Scene.AddLight(Light.Ambient(new Vector3(0.5f, 0.56f, 0.7f), 0.22f), name: "ambient");
    }

    /// <summary>
    /// A head built from primitives with blend shapes added in code, so the scene
    /// still demonstrates the feature when the authored model is not there.
    /// </summary>
    private Node BuildFallbackHead()
    {
        Geometry head = Scene.CreateSphereGeometry(0.12f, 48, 32);
        Material skin = Scene.CreateMaterial(MaterialOptions.Pbr(Palette.Rgba(0xD9A98C), 0f, 0.55f));
        Node node = Scene.AddMesh(head, skin, name: "head");
        node.Position = new Vector3(0f, 0.52f, 0f);

        (int vertices, _) = head.Counts;
        Vector3[] stretch = new Vector3[vertices];
        Vector3[] widen = new Vector3[vertices];
        Vector3[] nod = new Vector3[vertices];
        // The sphere is centred on the node, so its own coordinates are enough
        // to tell top from bottom and front from back.
        for (int i = 0; i < vertices; i++)
        {
            float t = i / (float)Math.Max(1, vertices - 1);
            float angle = t * MathF.Tau;
            stretch[i] = new Vector3(0f, 0.03f * MathF.Sin(angle), 0f);
            widen[i] = new Vector3(0.03f * MathF.Cos(angle), 0f, 0f);
            nod[i] = new Vector3(0f, 0f, 0.02f * MathF.Sin(angle * 2f));
        }

        head.AddMorphTarget("stretch", stretch);
        head.AddMorphTarget("widen", widen);
        head.AddMorphTarget("nod", nod);
        return node;
    }
}
