using System.Numerics;
using MotoCross.Game.Audio;
using ThreeNet;

namespace MotoCross.Game;

public enum CameraMode
{
    Chase,
    Cockpit,
    Cinematic,
}

public enum TimeOfDay
{
    Morning,
    Noon,
    Sunset,
    Night,
}

/// <summary>
/// Owns the scene, the bike, the race and the presentation state. The Avalonia
/// window only forwards input and draws the HUD on top of what this produces.
/// </summary>
public sealed class GameWorld : IDisposable
{
    private readonly Scene _scene;
    private readonly Track _track;
    private readonly TerrainField _terrain;
    private readonly WorldBuilder _world;
    private readonly BikeRig _rig;
    private readonly BikePhysics _bike;
    private readonly DustSystem _dust;
    private readonly RaceState _race;
    private readonly GameAudio _audio;
    private readonly Node _camera;
    private readonly Node _sun;
    private readonly Node _fill;

    private Vector3 _cameraPosition;
    private Vector3 _cameraTarget;
    private float _shake;
    private float _dustTimer;
    private float _respawnDistance;

    public GameWorld()
    {
        _scene = new Scene();
        _track = new Track();
        _terrain = new TerrainField(_track);
        _world = new WorldBuilder(_scene, _terrain);
        _world.Build();

        _rig = new BikeRig(_scene);
        _bike = new BikePhysics(_terrain);
        _dust = new DustSystem(_scene);
        _race = new RaceState(_track);
        _audio = new GameAudio();

        _sun = _scene.AddLight(
            Light.Directional(new Vector3(1f, 0.96f, 0.88f), 3.4f) with
            {
                CastShadow = true,
                ShadowNormalBias = 2.2f,
            },
            name: "sun");
        _fill = _scene.AddLight(Light.Directional(new Vector3(0.45f, 0.55f, 0.75f), 0.7f), name: "fill");
        _fill.LookAt(new Vector3(1f, -0.4f, 1f));

        _camera = _scene.AddCamera(ThreeNet.Camera.Perspective(68f * (MathF.PI / 180f), 0.2f, 600f));

        _race.CheckpointPassed += index =>
        {
            _respawnDistance = _track.Length * index / _race.CheckpointCount;
            _audio.PlayCheckpoint();
        };
        _race.LapCompleted += _ => _audio.PlayLap();
        _race.RaceFinished += _ => _audio.PlayFinish();

        Restart();
        ApplyTimeOfDay(TimeOfDay.Noon);
    }

    public Scene Scene => _scene;

    public Node CameraNode => _camera;

    public BikePhysics Bike => _bike;

    public RaceState Race => _race;

    public Track Track => _track;

    public GameAudio Audio => _audio;

    public CameraMode CameraView { get; set; } = CameraMode.Chase;

    public TimeOfDay Time { get; private set; } = TimeOfDay.Noon;

    /// <summary>Speed in km/h, what the HUD shows.</summary>
    public float SpeedKph => MathF.Abs(_bike.Speed) * 3.6f;

    public bool Paused { get; set; }

    /// <summary>Puts bike, timers and camera back on the start line.</summary>
    public void Restart()
    {
        _bike.Reset(_track.StartPosition, _track.StartHeading);
        _race.Reset();
        _respawnDistance = 0f;
        _cameraPosition = _bike.Position - (_bike.Forward * 8f) + new Vector3(0f, 3f, 0f);
        _cameraTarget = _bike.Position;
    }

    /// <summary>Drops the bike back on the circuit at the last checkpoint taken.</summary>
    public void Respawn()
    {
        Vector3 position = _track.PositionAt(_respawnDistance);
        Vector3 tangent = _track.TangentAt(_respawnDistance);
        _bike.Reset(position, MathF.Atan2(tangent.X, tangent.Z));
    }

    public void ApplyTimeOfDay(TimeOfDay time)
    {
        Time = time;
        (Vector3 direction, Vector3 color, float intensity, Vector3 ambient, float ambientIntensity, Vector4 background, float fog) = time switch
        {
            TimeOfDay.Morning => (
                new Vector3(-0.55f, -0.35f, 0.75f), new Vector3(1f, 0.82f, 0.62f), 2.8f,
                new Vector3(0.55f, 0.62f, 0.78f), 0.30f, new Vector4(0.52f, 0.60f, 0.72f, 1f), 0.0055f),
            TimeOfDay.Sunset => (
                new Vector3(0.75f, -0.28f, -0.6f), new Vector3(1f, 0.55f, 0.28f), 3.0f,
                new Vector3(0.45f, 0.38f, 0.52f), 0.26f, new Vector4(0.38f, 0.24f, 0.22f, 1f), 0.008f),
            TimeOfDay.Night => (
                new Vector3(-0.3f, -0.85f, 0.4f), new Vector3(0.36f, 0.44f, 0.72f), 0.35f,
                new Vector3(0.16f, 0.21f, 0.35f), 0.16f, new Vector4(0.015f, 0.02f, 0.045f, 1f), 0.012f),
            _ => (
                new Vector3(-0.45f, -0.85f, -0.35f), new Vector3(1f, 0.97f, 0.92f), 3.6f,
                new Vector3(0.52f, 0.60f, 0.76f), 0.24f, new Vector4(0.45f, 0.58f, 0.78f, 1f), 0.0035f),
        };

        _sun.Light = _sun.Light is { } light
            ? light with { Color = color, Intensity = intensity, CastShadow = time != TimeOfDay.Night }
            : null;
        _sun.Position = -direction * 60f;
        _sun.LookAt(Vector3.Zero);
        _fill.Light = _fill.Light is { } fill
            ? fill with { Intensity = time == TimeOfDay.Night ? 0.25f : 0.7f }
            : null;

        _scene.Environment = _scene.Environment with
        {
            Background = background,
            AmbientColor = ambient,
            AmbientIntensity = ambientIntensity,
            FogColor = new Vector3(background.X, background.Y, background.Z) * 1.1f,
            FogDensity = fog,
        };

        if (_world.SkyMaterial is { } sky)
        {
            sky.Update(options => options with
            {
                BaseColor = time == TimeOfDay.Night
                    ? new Vector4(0.05f, 0.07f, 0.16f, 1f)
                    : new Vector4(background.X * 1.5f, background.Y * 1.5f, background.Z * 1.5f, 1f),
            });
        }

        bool lightsOn = time is TimeOfDay.Night or TimeOfDay.Sunset;
        foreach (Node lamp in _world.NightLights)
        {
            lamp.Light = lamp.Light is { } spot ? spot with { Enabled = lightsOn } : null;
        }

        _rig.SetHeadlight(lightsOn);
    }

    /// <summary>Advances physics, animation, effects, audio and the camera.</summary>
    public void Update(float dt, in RiderInput input, double totalSeconds)
    {
        if (Paused)
        {
            UpdateCamera(dt, totalSeconds, idle: true);
            return;
        }

        _bike.Update(dt, input);
        _rig.Update(_bike, dt, totalSeconds);

        (float lateral, float lapDistance, _) = _terrain.NearestTrackPoint(_bike.Position.X, _bike.Position.Z);
        _race.Update(dt, lapDistance, MathF.Abs(lateral));

        EmitDust(dt);
        _dust.Update(dt, _cameraPosition);
        _audio.Update(_bike);

        if (_bike.LandingImpact > 0.1f)
        {
            _shake = MathF.Max(_shake, _bike.LandingImpact);
            _dust.Spawn(_bike.Position - new Vector3(0f, 0.35f, 0f), Vector3.Zero, 10, 1.6f, 1.3f);
        }

        UpdateCamera(dt, totalSeconds, idle: false);
    }

    private void EmitDust(float dt)
    {
        if (!_bike.Grounded || MathF.Abs(_bike.Speed) < 3f)
        {
            return;
        }

        _dustTimer -= dt;
        if (_dustTimer > 0f)
        {
            return;
        }

        // Roost comes off the rear wheel, thicker on dirt and while sliding.
        float rate = 0.02f + (0.05f * (1f - Math.Clamp(MathF.Abs(_bike.Speed) / 20f, 0f, 1f)));
        _dustTimer = rate;
        Vector3 rear = _bike.Position - (_bike.Forward * 0.9f) - new Vector3(0f, 0.3f, 0f);
        Vector3 kick = (-_bike.Forward * MathF.Abs(_bike.Speed) * 0.25f) + new Vector3(0f, 1.2f, 0f);
        int count = 1 + (int)(_bike.Slide * 3f) + (_bike.DirtWeight > 0.5f ? 1 : 0);
        _dust.Spawn(rear, kick, count, 0.9f + (_bike.Slide * 0.8f), 0.9f);
    }

    private void UpdateCamera(float dt, double totalSeconds, bool idle)
    {
        Vector3 bikePosition = _bike.Position;
        Vector3 forward = _bike.Forward;
        _shake = MathF.Max(0f, _shake - (dt * 2.2f));

        Vector3 desired;
        Vector3 target;
        switch (CameraView)
        {
            case CameraMode.Cockpit:
                desired = bikePosition + (forward * 0.35f) + new Vector3(0f, 1.05f, 0f);
                target = desired + (forward * 6f) - new Vector3(0f, 0.6f, 0f);
                _cameraPosition = desired;
                break;

            case CameraMode.Cinematic:
                // Slow orbit around the rider, useful for screenshots.
                float angle = (float)totalSeconds * 0.35f;
                desired = bikePosition + new Vector3(MathF.Cos(angle) * 9f, 4.5f, MathF.Sin(angle) * 9f);
                target = bikePosition + new Vector3(0f, 0.9f, 0f);
                _cameraPosition = Vector3.Lerp(_cameraPosition, desired, 1f - MathF.Exp(-4f * dt));
                break;

            default:
                // Chase camera: pulled back with speed, damped, and never below the ground.
                float distance = 6.4f + (MathF.Abs(_bike.Speed) * 0.12f);
                float height = 2.6f + (idle ? 0.6f : 0f);
                desired = bikePosition - (forward * distance) + new Vector3(0f, height, 0f);
                float ground = _terrain.HeightAt(desired.X, desired.Z) + 1.2f;
                desired.Y = MathF.Max(desired.Y, ground);
                _cameraPosition = Vector3.Lerp(_cameraPosition, desired, 1f - MathF.Exp(-7f * dt));
                target = bikePosition + (forward * 4f) + new Vector3(0f, 1.1f, 0f);
                break;
        }

        _cameraTarget = Vector3.Lerp(_cameraTarget, target, 1f - MathF.Exp(-10f * dt));

        if (_shake > 0.01f)
        {
            float amount = _shake * 0.25f;
            _cameraPosition += new Vector3(
                MathF.Sin((float)totalSeconds * 47f) * amount,
                MathF.Sin((float)totalSeconds * 61f) * amount,
                MathF.Cos((float)totalSeconds * 53f) * amount);
        }

        _camera.Position = _cameraPosition;
        _camera.LookAt(_cameraTarget);

        // A touch of extra field of view at speed sells the pace.
        float fov = (CameraView == CameraMode.Cockpit ? 78f : 68f) + (Math.Clamp(MathF.Abs(_bike.Speed) / 32f, 0f, 1f) * 10f);
        _camera.Camera = ThreeNet.Camera.Perspective(fov * (MathF.PI / 180f), 0.2f, 600f);
    }

    /// <summary>Points of the centreline in world space, for the mini map.</summary>
    public IReadOnlyList<Vector3> TrackOutline => _track.Samples;

    public void Dispose()
    {
        _audio.Dispose();
        _scene.Dispose();
    }
}
