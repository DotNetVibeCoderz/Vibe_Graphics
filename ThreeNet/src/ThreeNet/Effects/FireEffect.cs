using System.Numerics;

namespace ThreeNet.Effects;

/// <summary>How a flame burns.</summary>
public readonly record struct FireStyle
{
    /// <summary>Tint over the flame's own colour ramp; white leaves it alone.</summary>
    public Vector3 Tint { get; init; }

    /// <summary>How far the turbulence bends the flame: low is a candle, high is a bonfire.</summary>
    public float Magnitude { get; init; }

    /// <summary>How fast the flame climbs.</summary>
    public float Speed { get; init; }

    /// <summary>Size of the turbulence; larger numbers make a finer, busier flame.</summary>
    public Vector3 NoiseScale { get; init; }

    /// <summary>How much finer each turbulence octave is than the last.</summary>
    public float Lacunarity { get; init; }

    /// <summary>How much weaker each turbulence octave is than the last.</summary>
    public float Gain { get; init; }

    /// <summary>Overall brightness. Over 1 the flame blooms.</summary>
    public float Brightness { get; init; }

    /// <summary>A steady campfire.</summary>
    public static FireStyle Campfire => new()
    {
        Tint = Vector3.One,
        Magnitude = 1.3f,
        Speed = 0.32f,
        NoiseScale = new Vector3(1f, 2f, 1f),
        Lacunarity = 2f,
        Gain = 0.5f,
        Brightness = 2.4f,
    };

    /// <summary>A torch: narrow, quick, and leaning.</summary>
    public static FireStyle Torch => Campfire with
    {
        Magnitude = 1.6f,
        Speed = 0.45f,
        NoiseScale = new Vector3(1.6f, 2.6f, 1.6f),
        Brightness = 2.8f,
    };

    /// <summary>A candle: small, calm, almost still.</summary>
    public static FireStyle Candle => Campfire with
    {
        Magnitude = 0.75f,
        Speed = 0.18f,
        NoiseScale = new Vector3(2.4f, 3.2f, 2.4f),
        Brightness = 2f,
    };

    /// <summary>Something burning that should not be: blue green and hungry.</summary>
    public static FireStyle Magic => Campfire with
    {
        Tint = new Vector3(0.35f, 0.95f, 1.5f),
        Magnitude = 1.5f,
        Speed = 0.4f,
        Brightness = 2.6f,
    };
}

/// <summary>
/// A volumetric flame: a box whose fragments ray march a procedural fire.
/// </summary>
/// <remarks>
/// <para>
/// This follows
/// <see href="https://github.com/typeWolffo/THREE.Fire">THREE.Fire</see> and the
/// "real-time procedural volumetric fire" technique behind it. Each fragment of
/// the box walks the volume, and at every step asks a flame function for colour
/// and density: an envelope that tapers with height, turbulent simplex noise
/// that tears it apart, and a temperature ramp from white through yellow and
/// orange to the red that is almost smoke. The samples are added up, which is
/// why the material is additive - fire is light, not paint.
/// </para>
/// <para>
/// A flame is not a light. Put a flickering <see cref="Light.Point"/> inside one
/// if the surroundings should notice it, and
/// <see cref="ParticleEffect"/> embers above it if it should throw sparks;
/// <see cref="Flicker"/> is there to drive the light.
/// </para>
/// </remarks>
public sealed class FireEffect
{
    private readonly Random _random;
    private FireStyle _style;
    private Vector3 _size;
    private float _flicker = 1f;
    private float _target = 1f;
    private float _clock;

    /// <param name="scene">The scene the flame belongs to.</param>
    /// <param name="position">Where the base of the flame sits.</param>
    /// <param name="size">Width, height and depth of the volume, in metres.</param>
    /// <param name="parent">Optional parent node.</param>
    /// <param name="shader">
    /// A shader to share. A yard full of torches should compile
    /// <see cref="EffectShaders.Fire"/> once and pass it to every flame, which
    /// saves the compilation and keeps them on one pipeline.
    /// </param>
    public FireEffect(Scene scene, Vector3 position, Vector3 size, Node? parent = null, Shader? shader = null, int seed = 97)
    {
        _random = new Random(seed);
        _size = Vector3.Max(size, new Vector3(0.01f));
        _style = FireStyle.Campfire;

        shader ??= scene.CreateShader(EffectShaders.Fire, name: "threenet.fire");
        Material = scene.CreateMaterial(MaterialOptions.Default with
        {
            Shading = ShadingModel.Basic,
            AlphaMode = AlphaMode.Additive,
            // Front faces only: the march starts where the view ray enters the
            // box, and drawing the far side as well would count it twice.
            CullMode = CullMode.Back,
            DepthWrite = false,
            BaseColor = new Vector4(1f, 1f, 1f, 1f),
            Shader = shader,
        });

        Node = scene.AddMesh(scene.CreateBoxGeometry(_size.X, _size.Y, _size.Z), Material, parent, "fire");
        // The box is the volume, and the flame grows from its floor.
        Node.Position = position + new Vector3(0f, _size.Y * 0.5f, 0f);
        Node.CastShadow = false;
        Node.ReceiveShadow = false;

        Apply();
    }

    /// <summary>The box the flame is marched through.</summary>
    public Node Node { get; }

    /// <summary>The flame material.</summary>
    public Material Material { get; }

    /// <summary>How the flame burns.</summary>
    public FireStyle Style
    {
        get => _style;
        set
        {
            _style = value;
            Apply();
        }
    }

    /// <summary>Size of the volume, in metres.</summary>
    public Vector3 Size
    {
        get => _size;
        set
        {
            _size = Vector3.Max(value, new Vector3(0.01f));
            Apply();
        }
    }

    /// <summary>
    /// A number near 1 that wanders as the flame does. Multiply a nearby light's
    /// intensity by it and the room flickers with the fire.
    /// </summary>
    public float Flicker => _flicker;

    /// <summary>Keeps the material in step with the node and advances the flicker.</summary>
    public void Update(float deltaSeconds)
    {
        _clock += deltaSeconds;
        if (_clock >= 0.07f)
        {
            _clock = 0f;
            // A new target every so often, eased towards: a flame gutters, it
            // does not strobe.
            _target = 0.76f + ((float)_random.NextDouble() * 0.45f);
        }

        _flicker += (_target - _flicker) * MathF.Min(1f, deltaSeconds * 9f);

        // The shader marches in world space, so a flame that is carried about
        // has to tell its material where it went.
        Vector3 center = Node.WorldPosition;
        MaterialOptions options = Material.Options;
        if (options.Custom0.X != center.X || options.Custom0.Y != center.Y || options.Custom0.Z != center.Z)
        {
            Material.Options = options with { Custom0 = new Vector4(center, options.Custom0.W) };
        }
    }

    private void Apply()
    {
        Material.Options = Material.Options with
        {
            Custom0 = new Vector4(Node.WorldPosition, _style.Brightness),
            Custom1 = new Vector4(_size, _style.Speed),
            Custom2 = new Vector4(_style.Tint, _style.Magnitude),
            Custom3 = new Vector4(_style.NoiseScale.X, _style.NoiseScale.Y, _style.Lacunarity, _style.Gain),
        };
    }
}
