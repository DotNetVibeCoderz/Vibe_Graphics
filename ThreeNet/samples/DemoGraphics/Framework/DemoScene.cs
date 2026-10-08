using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace DemoGraphics.Framework;

/// <summary>One stop on the benchmark camera path.</summary>
public readonly record struct CameraKey(Vector3 Target, float Distance, float Yaw, float Pitch, float Seconds);

/// <summary>A named configuration: parameter values plus the weather they want.</summary>
public sealed record DemoPreset(string Name, IReadOnlyDictionary<string, float> Values)
{
    /// <summary>Weather preset applied with this one, if any.</summary>
    public string? Weather { get; init; }

    /// <summary>Time of day applied with this one, if any.</summary>
    public float? Hour { get; init; }
}

/// <summary>
/// A demo scene. Every scene follows the same contract - build, apply, update,
/// measure, reset - so the gallery, the laboratory, the benchmark and the
/// capture sidecar can drive all of them the same way.
/// </summary>
public abstract class DemoScene
{
    private readonly List<DemoParameter> _parameters = [];
    private readonly List<DemoPreset> _presets = [];
    private readonly Dictionary<string, DemoParameter> _byId = [];
    private readonly List<(string Label, Action Run)> _actions = [];

    /// <summary>Short code that identifies this scene in reports and captures.</summary>
    public abstract string Id { get; }

    public abstract string Title { get; }

    /// <summary>Catalogue grouping, for example "Environment" or "Lighting".</summary>
    public abstract string Category { get; }

    /// <summary>One line on what the scene puts on screen.</summary>
    public abstract string Summary { get; }

    /// <summary>Renderer features this scene is built to show off.</summary>
    public virtual IReadOnlyList<string> Features => [];

    /// <summary>The scene currently built, or null while nothing is loaded.</summary>
    protected Scene Scene { get; private set; } = null!;

    /// <summary>The shared clock and weather.</summary>
    protected EnvironmentState World { get; private set; } = new();

    /// <summary>The camera node the host orbits, for anything that must follow it.</summary>
    protected Node Viewer { get; private set; } = null!;

    public IReadOnlyList<DemoParameter> Parameters => _parameters;

    public IReadOnlyList<DemoPreset> Presets => _presets;

    /// <summary>One-shot things this scene can do, shown as buttons.</summary>
    public IReadOnlyList<(string Label, Action Run)> Actions => _actions;

    /// <summary>Preset the user picked last, so the sheet can show it as active.</summary>
    public string? ActivePreset { get; private set; }

    /// <summary>
    /// Set by the host: lets a scene change renderer settings while it runs, for
    /// the few cases where the scene is about them (a camera scene owns its depth
    /// of field, for instance).
    /// </summary>
    public Action<Action<RenderControls>>? RenderRequest { get; set; }

    /// <summary>Asks the host to apply a renderer change now.</summary>
    protected void RequestRender(Action<RenderControls> change) => RenderRequest?.Invoke(change);

    // ------------------------------------------------------------- lifecycle

    /// <summary>Creates the contents of a fresh scene. The camera is the host's.</summary>
    protected abstract void OnBuild();

    /// <summary>Pushes the current parameter values into the live scene.</summary>
    protected virtual void OnApplyParameters()
    {
    }

    /// <summary>Pushes the shared weather and clock into the live scene.</summary>
    protected virtual void OnApplyEnvironment()
    {
    }

    /// <summary>Called once per frame before rendering.</summary>
    public virtual void Update(float deltaSeconds, double totalSeconds)
    {
    }

    /// <summary>Renderer settings this scene wants when it loads.</summary>
    public virtual RendererOptions ConfigureRenderer(RendererOptions options) => options;

    /// <summary>Starting camera framing.</summary>
    public virtual void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = Vector3.Zero;
        orbit.Distance = 12f;
        orbit.Yaw = 0.7f;
        orbit.Pitch = 0.3f;
    }

    /// <summary>
    /// Deterministic camera path the benchmark replays, so two runs of the same
    /// scene measure the same work. The default is a slow full turn.
    /// </summary>
    public virtual IReadOnlyList<CameraKey> CameraPath =>
    [
        new(Vector3.Zero, 14f, 0.0f, 0.28f, 0f),
        new(Vector3.Zero, 10f, 1.6f, 0.18f, 3f),
        new(Vector3.Zero, 16f, 3.2f, 0.45f, 6f),
        new(Vector3.Zero, 14f, 4.8f, 0.24f, 9f),
    ];

    /// <summary>Scene specific numbers for the read-out, beyond the frame stats.</summary>
    public virtual IEnumerable<(string Label, string Value)> Metrics() => [];

    /// <summary>Why this scene cannot run here, or null when it can.</summary>
    public virtual string? UnsupportedReason(GpuCapabilities capabilities) => null;

    /// <summary>Called when the user clicks something in the viewport.</summary>
    public virtual void OnPick(RayHit? hit)
    {
    }

    /// <summary>Drops cached nodes; the scene itself is disposed by the host.</summary>
    protected virtual void OnUnload()
    {
    }

    // ---------------------------------------------------------- host driving

    /// <summary>Builds this scene into <paramref name="scene"/>.</summary>
    public void Load(Scene scene, EnvironmentState world, Node camera)
    {
        Scene = scene;
        World = world;
        Viewer = camera;
        OnBuild();
        OnApplyEnvironment();
        OnApplyParameters();
    }

    public void Unload()
    {
        OnUnload();
        Scene = null!;
        Viewer = null!;
    }

    /// <summary>Re-applies the weather; cheap enough to call every time it changes.</summary>
    public void ApplyEnvironment()
    {
        if (Scene is not null)
        {
            OnApplyEnvironment();
        }
    }

    public void ApplyParameters()
    {
        if (Scene is not null)
        {
            OnApplyParameters();
        }
    }

    /// <summary>Sets one parameter and applies it. Returns true if a rebuild is needed.</summary>
    public bool SetParameter(string id, float value)
    {
        if (!_byId.TryGetValue(id, out DemoParameter? parameter))
        {
            return false;
        }

        parameter.Value = value;
        ActivePreset = null;
        ApplyParameters();
        return parameter.RequiresRebuild;
    }

    /// <summary>Applies a preset. Returns true if a rebuild is needed.</summary>
    public bool ApplyPreset(DemoPreset preset)
    {
        bool rebuild = false;
        foreach ((string id, float value) in preset.Values)
        {
            if (_byId.TryGetValue(id, out DemoParameter? parameter))
            {
                parameter.Value = value;
                rebuild |= parameter.RequiresRebuild;
            }
        }

        if (preset.Weather is { } weather)
        {
            World.ApplyWeather(weather);
        }

        if (preset.Hour is { } hour)
        {
            World.Hour = hour;
        }

        ActivePreset = preset.Name;
        ApplyEnvironment();
        ApplyParameters();
        return rebuild;
    }

    /// <summary>Puts every parameter back to the value the scene shipped with.</summary>
    public void Reset()
    {
        foreach (DemoParameter parameter in _parameters)
        {
            parameter.Reset();
        }

        ActivePreset = null;
        ApplyParameters();
    }

    /// <summary>Camera pose for the benchmark at <paramref name="seconds"/> into the path.</summary>
    public virtual void ApplyBenchmarkCamera(OrbitController orbit, float seconds)
    {
        IReadOnlyList<CameraKey> path = CameraPath;
        if (path.Count == 0)
        {
            return;
        }

        float total = path[^1].Seconds;
        float time = total <= 0f ? 0f : seconds % total;
        for (int i = 0; i < path.Count - 1; i++)
        {
            CameraKey from = path[i];
            CameraKey to = path[i + 1];
            if (time > to.Seconds)
            {
                continue;
            }

            float span = MathF.Max(1e-3f, to.Seconds - from.Seconds);
            // Smoothstep between keys so the path has no visible corners.
            float t = Math.Clamp((time - from.Seconds) / span, 0f, 1f);
            t = t * t * (3f - (2f * t));
            orbit.Target = Vector3.Lerp(from.Target, to.Target, t);
            orbit.Distance = float.Lerp(from.Distance, to.Distance, t);
            orbit.Yaw = float.Lerp(from.Yaw, to.Yaw, t);
            orbit.Pitch = float.Lerp(from.Pitch, to.Pitch, t);
            orbit.Apply();
            return;
        }
    }

    /// <summary>Seconds one lap of the benchmark camera path takes.</summary>
    public float CameraPathDuration => CameraPath.Count == 0 ? 6f : MathF.Max(1f, CameraPath[^1].Seconds);

    // ------------------------------------------------------ scene authoring

    /// <summary>
    /// Drops everything declared so far, for a scene whose knobs depend on what
    /// it just loaded - blend shape names, for instance, are only known once the
    /// model is open. Call it at the top of <c>OnBuild</c> before redeclaring.
    /// </summary>
    protected void ResetDeclarations()
    {
        _parameters.Clear();
        _byId.Clear();
        _presets.Clear();
        _actions.Clear();
        ActivePreset = null;
    }

    /// <summary>Registers the parameters this scene exposes, in sheet order.</summary>
    protected void Declare(params DemoParameter[] parameters)
    {
        foreach (DemoParameter parameter in parameters)
        {
            _parameters.Add(parameter);
            _byId[parameter.Id] = parameter;
        }
    }

    /// <summary>Registers a button the control sheet shows for this scene.</summary>
    protected void AddAction(string label, Action run) => _actions.Add((label, run));

    protected void AddPreset(string name, params (string Id, float Value)[] values) =>
        _presets.Add(new DemoPreset(name, values.ToDictionary(pair => pair.Id, pair => pair.Value)));

    protected void AddPreset(string name, string? weather, float? hour, params (string Id, float Value)[] values) =>
        _presets.Add(new DemoPreset(name, values.ToDictionary(pair => pair.Id, pair => pair.Value))
        {
            Weather = weather,
            Hour = hour,
        });

    /// <summary>Value of a slider parameter.</summary>
    protected float P(string id) => _byId.TryGetValue(id, out DemoParameter? p) ? p.Value : 0f;

    /// <summary>Value of a toggle parameter.</summary>
    protected bool B(string id) => _byId.TryGetValue(id, out DemoParameter? p) && p.BoolValue;

    /// <summary>Selected index of a choice parameter.</summary>
    protected int I(string id) => _byId.TryGetValue(id, out DemoParameter? p) ? p.IntValue : 0;
}
