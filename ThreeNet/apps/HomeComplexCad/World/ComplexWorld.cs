using System.Numerics;
using HomeComplexCad.Navigation;
using ThreeNet;

namespace HomeComplexCad.World;

/// <summary>
/// The estate scene plus everything that changes over time: the sun's path
/// from morning to night, sky and ambient colour, street and room lights,
/// glowing windows and signs, and which building the viewer is looking at.
/// </summary>
public sealed class ComplexWorld : IDisposable
{
    private readonly Scene _scene;
    private readonly EstateBuilder _estate;
    private readonly Node _sun;
    private readonly Node _sky;
    private readonly Node _camera;
    private float _appliedHour = -1f;

    public ComplexWorld()
    {
        _scene = new Scene();
        _estate = new EstateBuilder(_scene);
        _estate.Build();

        _sun = _scene.AddLight(Light.Directional(Vector3.One, 3.2f) with { CastShadow = true, ShadowNormalBias = 1.8f }, name: "sun");
        _sky = _scene.AddLight(Light.Directional(new Vector3(0.5f, 0.6f, 0.8f), 0.5f), name: "sky-fill");

        _camera = _scene.AddCamera(Camera.Perspective(60f * (MathF.PI / 180f), 0.08f, 900f));
        Rig = new CameraRig(_scene, _camera);
        Location overview = _estate.Locations.First(l => l.Name == "Pemandangan Udara");
        Rig.Teleport(overview.Eye, overview.Target);

        SetHour(9.5f);
    }

    public Scene Scene => _scene;

    public Node CameraNode => _camera;

    public CameraRig Rig { get; }

    public IReadOnlyList<Location> Locations => _estate.Locations;

    public IReadOnlyList<PropertyInfo> Properties => _estate.Properties;

    /// <summary>Hour of the day, 0-24.</summary>
    public float Hour { get; private set; }

    public bool IsNight => Hour < 6.2f || Hour > 18.2f;

    /// <summary>Sets the time of day and relights the estate accordingly.</summary>
    public void SetHour(float hour)
    {
        Hour = ((hour % 24f) + 24f) % 24f;
        if (MathF.Abs(Hour - _appliedHour) < 0.01f)
        {
            return;
        }

        _appliedHour = Hour;

        // Sun path: rises in the east (+X) at 6, overhead at 12, sets in the west at 18.
        float dayAngle = (Hour - 6f) / 12f * MathF.PI;
        float elevation = MathF.Sin(dayAngle);
        Vector3 toSun = Vector3.Normalize(new Vector3(MathF.Cos(dayAngle), MathF.Max(elevation, -0.3f), 0.35f));
        float daylight = Math.Clamp((elevation + 0.08f) * 2.2f, 0f, 1f);
        float golden = Math.Clamp(1f - (MathF.Abs(elevation) * 3.2f), 0f, 1f) * daylight;

        Vector3 noonColour = new(1f, 0.97f, 0.92f);
        Vector3 goldenColour = new(1f, 0.58f, 0.3f);
        Vector3 sunColour = Vector3.Lerp(noonColour, goldenColour, golden);

        if (daylight > 0.01f)
        {
            _sun.Light = Light.Directional(sunColour, 3.4f * daylight) with { CastShadow = true, ShadowNormalBias = 1.8f };
            _sun.Position = toSun * 120f;
            _sun.LookAt(Vector3.Zero);
        }
        else
        {
            // Moonlight: cool, dim and from high above.
            _sun.Light = Light.Directional(new Vector3(0.45f, 0.55f, 0.85f), 0.35f) with { CastShadow = true, ShadowStrength = 0.6f };
            _sun.Position = new Vector3(-40f, 100f, 30f);
            _sun.LookAt(Vector3.Zero);
        }

        Vector3 dayAmbient = new(0.55f, 0.64f, 0.8f);
        Vector3 duskAmbient = new(0.55f, 0.42f, 0.45f);
        Vector3 nightAmbient = new(0.12f, 0.16f, 0.3f);
        Vector3 ambient = daylight > 0f
            ? Vector3.Lerp(Vector3.Lerp(nightAmbient, dayAmbient, daylight), duskAmbient, golden * 0.6f)
            : nightAmbient;

        Vector3 daySky = new(0.42f, 0.6f, 0.86f);
        Vector3 duskSky = new(0.62f, 0.38f, 0.28f);
        Vector3 nightSky = new(0.01f, 0.015f, 0.04f);
        Vector3 sky = Vector3.Lerp(Vector3.Lerp(nightSky, daySky, daylight), duskSky, golden * 0.7f);

        _scene.Environment = _scene.Environment with
        {
            Background = new Vector4(sky, 1f),
            AmbientColor = ambient,
            AmbientIntensity = 0.12f + (daylight * 0.22f),
            FogColor = sky * 1.05f,
            FogDensity = 0.0022f + ((1f - daylight) * 0.002f),
        };

        _sky.Light = Light.Directional(Vector3.Lerp(new Vector3(0.3f, 0.35f, 0.6f), new Vector3(0.55f, 0.65f, 0.85f), daylight), 0.25f + (daylight * 0.35f));
        _sky.LookAt(new Vector3(0.3f, -1f, -0.4f));

        // After dusk: street lamps, porch and room lights, glowing windows and signs.
        bool lightsOn = Hour < 6.3f || Hour > 17.8f;
        float glow = lightsOn ? 1f : 0f;
        foreach (Node lamp in _estate.StreetLights.Concat(_estate.OutdoorLights).Concat(_estate.InteriorLights))
        {
            if (lamp.Light is { } light && light.Enabled != lightsOn)
            {
                lamp.Light = light with { Enabled = lightsOn };
            }
        }

        _estate.Palette.WindowGlow.Update(o => o with { EmissiveIntensity = glow * 1.4f });
        _estate.Palette.LampGlow.Update(o => o with { EmissiveIntensity = lightsOn ? 6f : 0.4f });
        foreach (Material sign in _estate.NightEmissives)
        {
            sign.Update(o => o with { EmissiveIntensity = lightsOn ? 1.1f : 0.15f });
        }
    }

    /// <summary>The building the camera is looking at, if any (within 90 m).</summary>
    public PropertyInfo? PropertyInView()
    {
        IReadOnlyList<RayHit> hits = _scene.Raycast(new Ray(Rig.EyePosition, Rig.ViewDirection), new RaycastOptions { MaxDistance = 90f });
        return hits.Count == 0 ? null : PropertyFor(hits[0].Node);
    }

    /// <summary>Walks up the hierarchy until it finds a building root.</summary>
    public PropertyInfo? PropertyFor(Node node)
    {
        Node? current = node;
        for (int depth = 0; depth < 16 && current is not null && !current.IsNull; depth++)
        {
            if (_estate.BuildingRoots.TryGetValue(current.Id, out PropertyInfo? info))
            {
                return info;
            }

            current = current.Parent;
        }

        return null;
    }

    public PropertyInfo? Pick(float ndcX, float ndcY, float aspect)
    {
        Ray ray = _scene.CreateCameraRay(_camera, ndcX, ndcY, aspect);
        IReadOnlyList<RayHit> hits = _scene.Raycast(ray, new RaycastOptions { MaxDistance = 400f });
        return hits.Count == 0 ? null : PropertyFor(hits[0].Node);
    }

    public void Dispose() => _scene.Dispose();
}
