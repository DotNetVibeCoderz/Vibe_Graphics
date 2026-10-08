using DemoGraphics.Scenes;

namespace DemoGraphics.Framework;

/// <summary>
/// Every scene the app ships, in catalogue order. Scene codes are stable: they
/// are what a benchmark report and a capture sidecar refer to.
/// </summary>
public static class SceneCatalog
{
    public static IReadOnlyList<DemoScene> All { get; } =
    [
        new CoastScene(),
        new TimeOfDayScene(),
        new SkyScene(),
        new ForestWindScene(),
        new NeonCityScene(),
        new MaterialMuseumScene(),
        new LightingLabScene(),
        new ShadowLabScene(),
        new CameraLabScene(),
        new WaterPoolScene(),
        new FirePitScene(),
        new ParticleLabScene(),
        new PhysicsYardScene(),
        new MotionScene(),
        new FaceScene(),
    ];

    /// <summary>
    /// A short run that still covers the different kinds of load: environment,
    /// geometry and draw calls, many lights, and simulation. Picked by code, so
    /// adding a scene to the catalogue never silently changes the benchmark.
    /// </summary>
    public static IReadOnlyList<DemoScene> Flagship { get; } = [.. Pick("COA", "VEG", "NEO", "WAT", "FIR")];

    /// <summary>The scenes with these codes, in the order asked for.</summary>
    public static IEnumerable<DemoScene> Pick(params string[] codes) =>
        codes.Select(code => All.First(scene => scene.Id == code));
}
