using System.Numerics;

namespace ThreeNet.Effects;

/// <summary>What a layer of cloud looks like.</summary>
public readonly record struct CloudStyle
{
    /// <summary>How much of the sky is cloud: 0 is clear, 1 is a solid deck.</summary>
    public float Coverage { get; init; }

    /// <summary>How quickly cloud swallows light, per metre. Higher is thicker, darker underneath.</summary>
    public float Density { get; init; }

    /// <summary>Depth of the layer from base to top, in metres.</summary>
    public float Thickness { get; init; }

    /// <summary>Size of the largest billows, in metres.</summary>
    public float FeatureSize { get; init; }

    /// <summary>Colour and strength of the sunlight on the cloud.</summary>
    public Vector3 SunColor { get; init; }

    /// <summary>Colour and strength of the light the sky gives it from all round.</summary>
    public Vector3 AmbientColor { get; init; }

    /// <summary>
    /// How many samples each ray takes (4..64). This is the cost of the effect: every
    /// pixel of sky pays for all of them. Twelve is enough on integrated graphics.
    /// </summary>
    public int Steps { get; init; }

    /// <summary>
    /// How much of the scene's distance fog reaches the cloud, 0..0.99. Fog set for
    /// streets would wipe a far-off cloud deck out; a quarter of it reads as haze.
    /// </summary>
    public float Haze { get; init; }

    /// <summary>Scattered heaps on a fine day.</summary>
    public static CloudStyle FairWeather => new()
    {
        Coverage = 0.42f,
        Density = 0.012f,
        Thickness = 700f,
        FeatureSize = 1_900f,
        SunColor = new Vector3(1.25f, 1.2f, 1.1f),
        AmbientColor = new Vector3(0.42f, 0.5f, 0.64f),
        Steps = 16,
        Haze = 0.25f,
    };

    /// <summary>A grey deck with a few thin places.</summary>
    public static CloudStyle Overcast => FairWeather with
    {
        Coverage = 0.82f,
        Density = 0.016f,
        Thickness = 900f,
        SunColor = new Vector3(0.8f, 0.8f, 0.8f),
        AmbientColor = new Vector3(0.4f, 0.43f, 0.47f),
    };

    /// <summary>Rain cloud: deep, dense and dark underneath.</summary>
    public static CloudStyle Storm => FairWeather with
    {
        Coverage = 0.95f,
        Density = 0.03f,
        Thickness = 1_300f,
        FeatureSize = 2_600f,
        SunColor = new Vector3(0.55f, 0.56f, 0.6f),
        AmbientColor = new Vector3(0.2f, 0.22f, 0.26f),
    };
}

/// <summary>
/// A layer of volumetric cloud: a sheet at the cloud base whose fragments ray march
/// the air above it.
/// </summary>
/// <remarks>
/// <para>
/// The sheet is real geometry at a real height, so mountains and towers hide the
/// cloud behind them and the scene's fog fades it into the distance like anything
/// else. It is meant to be seen from below: keep <see cref="Altitude"/> above the
/// camera. Call <see cref="Update"/> every frame; it keeps the sheet centred on the
/// camera and moves the cloud with the wind, so the sky never runs out.
/// </para>
/// <para>
/// The clouds are lit by <see cref="SunDirection"/> and the two colours of the
/// <see cref="Style"/>, not by the scene's lights: a volume has no surface for them
/// to fall on. Set them from the same sun the scene uses. The layer casts no shadow.
/// </para>
/// <para>
/// The shader is <see cref="EffectShaders.Clouds"/>; it costs
/// <see cref="CloudStyle.Steps"/> noise lookups for every pixel of sky and several
/// times that inside cloud.
/// </para>
/// </remarks>
public sealed class CloudLayer
{
    private CloudStyle _style = CloudStyle.FairWeather;
    private Vector3 _sun = Vector3.Normalize(new Vector3(0.3f, 0.8f, 0.2f));
    private Vector2 _drift;
    private float _altitude;

    /// <param name="scene">The scene the clouds belong to.</param>
    /// <param name="altitude">Height of the cloud base.</param>
    /// <param name="radius">
    /// How far the sheet reaches from the camera. Keep it inside the camera's far
    /// plane; the cloud thins to nothing over the outer third.
    /// </param>
    /// <param name="parent">Optional parent node.</param>
    /// <param name="shader">A compiled <see cref="EffectShaders.Clouds"/> to share between layers.</param>
    public CloudLayer(Scene scene, float altitude = 1_500f, float radius = 12_000f, Node? parent = null, Shader? shader = null)
    {
        _altitude = altitude;
        Radius = MathF.Max(radius, 1f);
        shader ??= scene.CreateShader(EffectShaders.Clouds, name: "threenet.clouds");
        Material = scene.CreateMaterial(MaterialOptions.Default with
        {
            Shading = ShadingModel.Basic,
            AlphaMode = AlphaMode.Blend,
            // Seen from underneath, whichever way the plane happens to face.
            CullMode = CullMode.None,
            DepthWrite = false,
            BaseColor = Vector4.One,
            Shader = shader,
        });

        // Tessellated: the forward pass interpolates view distance (for fog) per vertex, and
        // across one quad this size that would be wrong by kilometres.
        Node = scene.AddMesh(scene.CreatePlaneGeometry(Radius * 2f, Radius * 2f, 48, 48), Material, parent, "clouds");
        // The built-in plane stands upright; lay it flat.
        Node.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI * 0.5f);
        Node.Position = new Vector3(0f, altitude, 0f);
        Node.CastShadow = false;
        Node.ReceiveShadow = false;
        Apply();
    }

    /// <summary>The sheet at the cloud base.</summary>
    public Node Node { get; }

    /// <summary>The cloud material.</summary>
    public Material Material { get; }

    /// <summary>Reach of the sheet from the camera, in metres.</summary>
    public float Radius { get; }

    /// <summary>What the clouds look like.</summary>
    public CloudStyle Style
    {
        get => _style;
        set
        {
            _style = value;
            Apply();
        }
    }

    /// <summary>Direction from the ground toward the sun (or the moon).</summary>
    public Vector3 SunDirection
    {
        get => _sun;
        set
        {
            _sun = value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : Vector3.UnitY;
            Apply();
        }
    }

    /// <summary>How fast the clouds travel, in metres per second along x and z.</summary>
    public Vector2 Wind { get; set; } = new(9f, 3f);

    /// <summary>Height of the cloud base.</summary>
    public float Altitude
    {
        get => _altitude;
        set => _altitude = value;
    }

    /// <summary>Moves the clouds with the wind and keeps the sheet over the camera.</summary>
    public void Update(float deltaSeconds, Vector3 cameraPosition)
    {
        // The pattern is fixed to the world and slides with the wind; the sheet is
        // only the window it is seen through, so it can follow the camera freely.
        _drift -= Wind * deltaSeconds;
        Node.Position = new Vector3(cameraPosition.X, _altitude, cameraPosition.Z);
        Apply();
    }

    private void Apply()
    {
        Material.Options = Material.Options with
        {
            Custom0 = new Vector4(_sun, _style.Coverage),
            Custom1 = new Vector4(_style.SunColor, _style.Density),
            Custom2 = new Vector4(_style.AmbientColor, _style.Thickness),
            Custom3 = new Vector4(_drift.X, _drift.Y, _style.FeatureSize, Math.Clamp(_style.Steps, 4, 64) + Math.Clamp(_style.Haze, 0f, 0.99f)),
        };
    }
}
