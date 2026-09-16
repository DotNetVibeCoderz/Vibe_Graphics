using System.Reflection;
using System.Text;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>
/// One entry of the gallery. A sample builds its own scene, optionally animates
/// it, and exposes the source file that produced it.
/// </summary>
public abstract class GallerySample
{
    /// <summary>Name shown in the sidebar.</summary>
    public abstract string Title { get; }

    /// <summary>Sidebar grouping, for example "Materials" or "Lighting".</summary>
    public abstract string Category { get; }

    /// <summary>One line explaining what the sample demonstrates.</summary>
    public abstract string Summary { get; }

    /// <summary>Builds the scene contents. The camera node is created by the gallery.</summary>
    public abstract void Build(Scene scene);

    /// <summary>Called once per frame; the default does nothing.</summary>
    public virtual void Update(Scene scene, float deltaSeconds, double totalSeconds)
    {
    }

    /// <summary>Lets a sample set the starting camera framing.</summary>
    public virtual void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = System.Numerics.Vector3.Zero;
        orbit.Distance = 7f;
        orbit.Yaw = 0.7f;
        orbit.Pitch = 0.35f;
    }

    /// <summary>Lets a sample turn renderer features (bloom, MSAA, ...) on or off.</summary>
    public virtual RendererOptions ConfigureRenderer(RendererOptions options) => options;

    /// <summary>Called when the user clicks in the viewport, with the picking ray.</summary>
    public virtual void OnPick(Scene scene, Ray ray)
    {
    }

    /// <summary>Source code of this sample, loaded from the embedded resource.</summary>
    public string SourceCode => SampleSource.Load(GetType());
}

/// <summary>
/// Reads the sample source files embedded at build time, so the code panel
/// always shows exactly what the viewport is running.
/// </summary>
public static class SampleSource
{
    private static readonly Dictionary<Type, string> Cache = [];

    public static string Load(Type sampleType)
    {
        if (Cache.TryGetValue(sampleType, out string? cached))
        {
            return cached;
        }

        Assembly assembly = sampleType.Assembly;
        string suffix = $".{sampleType.Name}.cs";
        string? resource = Array.Find(assembly.GetManifestResourceNames(), name => name.EndsWith(suffix, StringComparison.Ordinal));

        string source;
        if (resource is null)
        {
            source = $"// source for {sampleType.Name} was not embedded in the build";
        }
        else
        {
            using Stream? stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                source = $"// could not open the embedded source of {sampleType.Name}";
            }
            else
            {
                using StreamReader reader = new(stream, Encoding.UTF8);
                source = reader.ReadToEnd();
            }
        }

        Cache[sampleType] = source;
        return source;
    }
}

/// <summary>Every sample shipped with the gallery, in display order.</summary>
public static class SampleCatalog
{
    public static IReadOnlyList<GallerySample> All { get; } =
    [
        new PrimitivesSample(),
        new ShadingModelsSample(),
        new MaterialsSample(),
        new TexturesSample(),
        new StreamingSample(),
        new CustomShaderSample(),
        new TransparencySample(),
        new LightsSample(),
        new ShadowsSample(),
        new DeferredLightsSample(),
        new BloomSample(),
        new CameraEffectsSample(),
        new FogSample(),
        new HierarchySample(),
        new AnimatedGeometrySample(),
        new AnimationSample(),
        new PickingSample(),
        new StressTestSample(),
    ];
}
