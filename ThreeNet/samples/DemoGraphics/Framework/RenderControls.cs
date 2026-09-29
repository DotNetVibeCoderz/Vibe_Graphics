using ThreeNet;

namespace DemoGraphics.Framework;

public enum QualityPreset
{
    Low,
    Medium,
    High,
    Ultra,
    Cinematic,
    /// <summary>Whatever the user last changed by hand.</summary>
    Custom,
}

/// <summary>
/// Every renderer setting the app drives, in one place. Scenes hand over their
/// own defaults when they load; after that the control sheet owns these values
/// and pushes them into the live renderer without recreating it.
/// </summary>
public sealed class RenderControls
{
    public bool Shadows = true;
    public bool Ssao = true;
    public bool Bloom = true;
    public bool DepthOfField;
    public bool MotionBlur;
    public bool FrustumCulling = true;
    public bool VSync;

    public int MsaaSamples = 4;
    public RenderPath RenderPath = RenderPath.Forward;
    public ToneMapping ToneMapping = ToneMapping.Aces;
    public float Exposure = 1f;

    public float BloomIntensity = 0.6f;
    public float BloomThreshold = 1f;

    public int ShadowMapSize = 2048;
    public int ShadowCascades = 3;
    public int ShadowSoftness = 1;
    public float ShadowDistance = 60f;

    public int SsaoSamples = 16;
    public float SsaoRadius = 0.5f;
    public float SsaoIntensity = 1.5f;

    public float DofFocusDistance = 10f;
    public float DofFocusRange = 4f;
    public float DofMaxBlur = 14f;

    public float MotionBlurStrength = 0.6f;
    public int MotionBlurSamples = 12;

    /// <summary>Which surface channel the viewport shows instead of the image.</summary>
    public DebugView DebugView = DebugView.Off;

    /// <summary>Draws every material as lines; needs a GPU with line polygons.</summary>
    public bool Wireframe;

    /// <summary>Resolution multiplier of the viewport, 0.25-2.</summary>
    public double RenderScale = 1.0;

    public int FpsCap = 144;

    public QualityPreset Quality = QualityPreset.High;

    /// <summary>Adopts the settings a scene asked for when it loaded.</summary>
    public void CaptureFrom(in RendererOptions options)
    {
        Shadows = options.Shadows;
        Ssao = options.Ssao;
        Bloom = options.Bloom;
        DepthOfField = options.DepthOfField;
        MotionBlur = options.MotionBlur;
        FrustumCulling = options.FrustumCulling;
        MsaaSamples = options.MsaaSamples;
        RenderPath = options.RenderPath;
        ToneMapping = options.ToneMapping;
        Exposure = options.Exposure;
        BloomIntensity = options.BloomIntensity;
        BloomThreshold = options.BloomThreshold;
        ShadowMapSize = options.ShadowMapSize;
        ShadowCascades = options.ShadowCascades;
        ShadowSoftness = options.ShadowSoftness;
        ShadowDistance = options.ShadowDistance;
        SsaoSamples = options.SsaoSamples;
        SsaoRadius = options.SsaoRadius;
        SsaoIntensity = options.SsaoIntensity;
        DofFocusDistance = options.DofFocusDistance;
        DofFocusRange = options.DofFocusRange;
        DofMaxBlur = options.DofMaxBlur;
        MotionBlurStrength = options.MotionBlurStrength;
        MotionBlurSamples = options.MotionBlurSamples;
    }

    /// <summary>
    /// Writes these settings over the renderer's current options. The target size
    /// and the BGRA readback belong to the view, so they are carried across
    /// untouched.
    /// </summary>
    public RendererOptions Apply(RendererOptions live) => live with
    {
        VSync = VSync,
        MsaaSamples = MsaaSamples,
        RenderPath = RenderPath,
        ToneMapping = ToneMapping,
        Exposure = Exposure,
        Bloom = Bloom,
        BloomIntensity = BloomIntensity,
        BloomThreshold = BloomThreshold,
        FrustumCulling = FrustumCulling,
        Shadows = Shadows,
        ShadowMapSize = ShadowMapSize,
        ShadowCascades = ShadowCascades,
        ShadowSoftness = ShadowSoftness,
        ShadowDistance = ShadowDistance,
        Ssao = Ssao,
        SsaoSamples = SsaoSamples,
        SsaoRadius = SsaoRadius,
        SsaoIntensity = SsaoIntensity,
        DepthOfField = DepthOfField,
        DofFocusDistance = DofFocusDistance,
        DofFocusRange = DofFocusRange,
        DofMaxBlur = DofMaxBlur,
        MotionBlur = MotionBlur,
        MotionBlurStrength = MotionBlurStrength,
        MotionBlurSamples = MotionBlurSamples,
        DebugView = DebugView,
        Wireframe = Wireframe,
    };

    /// <summary>Loads a quality preset over the current settings.</summary>
    public void LoadQuality(QualityPreset preset)
    {
        Quality = preset;
        switch (preset)
        {
            case QualityPreset.Low:
                (MsaaSamples, Shadows, ShadowMapSize, ShadowCascades, ShadowSoftness) = (1, false, 1024, 1, 0);
                (Ssao, SsaoSamples, Bloom, DepthOfField, MotionBlur) = (false, 8, false, false, false);
                (RenderScale, ToneMapping, ShadowDistance) = (0.75, ToneMapping.Reinhard, 40f);
                break;
            case QualityPreset.Medium:
                (MsaaSamples, Shadows, ShadowMapSize, ShadowCascades, ShadowSoftness) = (2, true, 1024, 2, 1);
                (Ssao, SsaoSamples, Bloom, DepthOfField, MotionBlur) = (true, 8, true, false, false);
                (RenderScale, ToneMapping, ShadowDistance) = (1.0, ToneMapping.Aces, 50f);
                break;
            case QualityPreset.High:
                (MsaaSamples, Shadows, ShadowMapSize, ShadowCascades, ShadowSoftness) = (4, true, 2048, 3, 1);
                (Ssao, SsaoSamples, Bloom, DepthOfField, MotionBlur) = (true, 16, true, false, false);
                (RenderScale, ToneMapping, ShadowDistance) = (1.0, ToneMapping.Aces, 70f);
                break;
            case QualityPreset.Ultra:
                (MsaaSamples, Shadows, ShadowMapSize, ShadowCascades, ShadowSoftness) = (8, true, 4096, 4, 2);
                (Ssao, SsaoSamples, Bloom, DepthOfField, MotionBlur) = (true, 32, true, false, false);
                (RenderScale, ToneMapping, ShadowDistance) = (1.0, ToneMapping.Aces, 110f);
                break;
            case QualityPreset.Cinematic:
                (MsaaSamples, Shadows, ShadowMapSize, ShadowCascades, ShadowSoftness) = (8, true, 4096, 4, 3);
                (Ssao, SsaoSamples, Bloom, DepthOfField, MotionBlur) = (true, 32, true, true, true);
                (RenderScale, ToneMapping, ShadowDistance) = (1.0, ToneMapping.Filmic, 140f);
                (MotionBlurStrength, MotionBlurSamples, DofMaxBlur) = (0.85f, 16, 18f);
                break;
        }
    }

    /// <summary>Clamps a preset to what the adapter can actually do.</summary>
    public void ClampTo(GpuCapabilities capabilities)
    {
        MsaaSamples = Math.Min(MsaaSamples, Math.Max(1, capabilities.MaxMsaaSamples));
        ShadowMapSize = Math.Min(ShadowMapSize, Math.Max(256, capabilities.MaxTextureSize));
        if (!capabilities.WireframeRendering)
        {
            Wireframe = false;
        }
    }

    /// <summary>The settings as strings, for capture sidecars and reports.</summary>
    public Dictionary<string, string> Describe() => new()
    {
        ["quality"] = Quality.ToString(),
        ["renderPath"] = RenderPath.ToString(),
        ["msaa"] = MsaaSamples + "x",
        ["renderScale"] = RenderScale.ToString("F2"),
        ["toneMapping"] = ToneMapping.ToString(),
        ["exposure"] = Exposure.ToString("F2"),
        ["shadows"] = Shadows ? $"{ShadowMapSize} px, {ShadowCascades} cascades, PCF {ShadowSoftness}" : "off",
        ["ssao"] = Ssao ? $"{SsaoSamples} samples, radius {SsaoRadius:F2}" : "off",
        ["bloom"] = Bloom ? $"intensity {BloomIntensity:F2}, threshold {BloomThreshold:F2}" : "off",
        ["depthOfField"] = DepthOfField ? $"focus {DofFocusDistance:F1} m, blur {DofMaxBlur:F0} px" : "off",
        ["motionBlur"] = MotionBlur ? $"strength {MotionBlurStrength:F2}, {MotionBlurSamples} samples" : "off",
        ["debugView"] = DebugView.ToString(),
        ["wireframe"] = Wireframe ? "on" : "off",
        ["vsync"] = VSync ? "on" : "off",
    };
}
