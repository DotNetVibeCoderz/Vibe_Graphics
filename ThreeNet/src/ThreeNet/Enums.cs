namespace ThreeNet;

/// <summary>Shading model used by a material.</summary>
public enum ShadingModel : uint
{
    /// <summary>Unlit, base colour only.</summary>
    Basic = 0,
    /// <summary>Lambert diffuse.</summary>
    Lambert = 1,
    /// <summary>Blinn-Phong specular.</summary>
    Phong = 2,
    /// <summary>Metallic-roughness physically based shading.</summary>
    Pbr = 3,
}

/// <summary>How fragment alpha is resolved.</summary>
public enum AlphaMode : uint
{
    Opaque = 0,
    /// <summary>Alpha tested against the material cutoff.</summary>
    Mask = 1,
    /// <summary>Sorted back to front and alpha blended.</summary>
    Blend = 2,
    /// <summary>
    /// Sorted back to front and added to what is already there. Fire, sparks and
    /// glows read as light rather than as paint: they never darken what is
    /// behind them, and they do not need to be sorted among themselves.
    /// </summary>
    Additive = 3,
}

/// <summary>Face culling mode.</summary>
public enum CullMode : uint
{
    None = 0,
    Back = 1,
    Front = 2,
}

/// <summary>Light source type.</summary>
public enum LightType : uint
{
    /// <summary>Infinitely distant light, direction taken from the node -Z axis.</summary>
    Directional = 0,
    Point = 1,
    Spot = 2,
    Area = 3,
    /// <summary>Constant term added to every fragment.</summary>
    Ambient = 4,
}

/// <summary>Tone mapping operator applied to the HDR buffer.</summary>
public enum ToneMapping : uint
{
    None = 0,
    Reinhard = 1,
    /// <summary>Narkowicz ACES approximation (default).</summary>
    Aces = 2,
    /// <summary>Uncharted 2 filmic curve.</summary>
    Filmic = 3,
}

/// <summary>Adapter selection hint.</summary>
public enum PowerPreference : uint
{
    HighPerformance = 0,
    LowPower = 1,
}

/// <summary>How opaque geometry is lit.</summary>
public enum RenderPath : uint
{
    /// <summary>Every fragment is lit while rasterised; supports MSAA.</summary>
    Forward = 0,
    /// <summary>G-buffer then one lighting pass per pixel; MSAA is not applied.</summary>
    Deferred = 1,
}

/// <summary>
/// Replaces the shaded image with one channel of the surface, for inspecting
/// what the renderer fed the lighting. While a view other than
/// <see cref="Off"/> is active the renderer skips exposure, tone mapping, bloom
/// and the camera effects, so the values reach the screen unchanged (encoded to
/// sRGB by the output surface like any other colour).
/// </summary>
public enum DebugView : uint
{
    /// <summary>Normal shading.</summary>
    Off = 0,
    /// <summary>Albedo / diffuse colour with the lighting removed.</summary>
    BaseColor = 1,
    /// <summary>World space shading normal, remapped to 0..1.</summary>
    WorldNormal = 2,
    Roughness = 3,
    Metallic = 4,
    /// <summary>Material occlusion, multiplied by SSAO when it is on.</summary>
    Occlusion = 5,
    Emissive = 6,
    /// <summary>Linear view depth over the camera range, square rooted for contrast.</summary>
    Depth = 7,
    /// <summary>Lighting with the albedo taken out (white surfaces).</summary>
    Lighting = 8,
    /// <summary>Shadow visibility of every shadow casting light.</summary>
    Shadow = 9,
    /// <summary>
    /// Texture coordinates. The deferred path draws magenta instead, because the
    /// G-buffer does not carry UVs.
    /// </summary>
    Uv = 10,
}

/// <summary>What fills the pixels no geometry covers.</summary>
public enum SkyMode : uint
{
    /// <summary>The background colour, and nothing else.</summary>
    Color = 0,
    /// <summary>
    /// The equirectangular <see cref="SceneEnvironment.EnvironmentMap"/>, which
    /// also lights the scene through image based lighting.
    /// </summary>
    Texture = 1,
    /// <summary>
    /// A sky built from the sun direction: height gradient, horizon haze, a sun
    /// disc bright enough to bloom, stars at night and an optional cloud sheet.
    /// It needs no texture, and it lights nothing by itself.
    /// </summary>
    Procedural = 2,
}

/// <summary>Graphics API a renderer ended up on.</summary>
public enum GpuBackend : uint
{
    Unknown = 0,
    Vulkan = 1,
    Metal = 2,
    Direct3D12 = 3,
    OpenGl = 4,
    WebGpu = 5,
}

/// <summary>What kind of device the adapter is.</summary>
public enum GpuDeviceType : uint
{
    Other = 0,
    IntegratedGpu = 1,
    DiscreteGpu = 2,
    VirtualGpu = 3,
    /// <summary>A software rasteriser such as WARP, SwiftShader or lavapipe.</summary>
    Cpu = 4,
}

/// <summary>Pixel format of a texture upload.</summary>
public enum TextureFormat : uint
{
    /// <summary>8 bit RGBA interpreted as sRGB (colour data).</summary>
    Rgba8UnormSrgb = 0,
    /// <summary>8 bit RGBA, linear (normal maps, masks, metallic-roughness).</summary>
    Rgba8Unorm = 1,
    /// <summary>32 bit float RGBA (HDR environment maps).</summary>
    Rgba32Float = 2,
}

/// <summary>Texture addressing mode.</summary>
public enum WrapMode : uint
{
    Repeat = 0,
    ClampToEdge = 1,
    MirrorRepeat = 2,
}

/// <summary>How geometry indices are interpreted.</summary>
public enum PrimitiveTopology : uint
{
    TriangleList = 0,
    LineList = 1,
    PointList = 2,
}

/// <summary>Verbosity of the native logger.</summary>
public enum LogLevel : uint
{
    Off = 0,
    Error = 1,
    Warning = 2,
    Info = 3,
    Debug = 4,
    Trace = 5,
}
