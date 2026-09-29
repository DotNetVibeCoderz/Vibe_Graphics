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
        new ForestWindScene(),
        new NeonCityScene(),
        new MaterialMuseumScene(),
        new LightingLabScene(),
        new ShadowLabScene(),
        new CameraLabScene(),
        new ParticleLabScene(),
        new PhysicsYardScene(),
        new MotionScene(),
    ];

    /// <summary>
    /// A short run that still covers the different kinds of load: environment,
    /// geometry and draw calls, many lights, and simulation.
    /// </summary>
    public static IReadOnlyList<DemoScene> Flagship { get; } =
    [
        All[0],
        All[2],
        All[3],
        All[8],
    ];
}
