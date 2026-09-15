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
