using System.Numerics;
using ThreeNet.Interop;

namespace ThreeNet;

/// <summary>
/// A light source. Lights are attached to nodes, so position and direction come
/// from the node transform (the emission direction is the node -Z axis).
/// </summary>
public struct Light
{
    public LightType Type;
    /// <summary>Linear RGB colour.</summary>
    public Vector3 Color;
    /// <summary>Multiplier applied to <see cref="Color"/>.</summary>
    public float Intensity;
    /// <summary>Reach of point and spot lights in world units; 0 means infinite.</summary>
    public float Range;
    /// <summary>Inner cone half angle in radians (spot lights).</summary>
    public float InnerConeAngle;
    /// <summary>Outer cone half angle in radians (spot lights).</summary>
    public float OuterConeAngle;
    /// <summary>Size of an area light.</summary>
    public Vector2 Size;
    /// <summary>
    /// Renders this light into a shadow map. Directional lights use cascaded
    /// shadow maps, spot lights a perspective map; point lights do not cast
    /// shadows yet. Requires <see cref="RendererOptions.Shadows"/>.
    /// </summary>
    public bool CastShadow;
    public bool Enabled;
    /// <summary>Constant depth offset (normalised shadow depth) that prevents shadow acne.</summary>
    public float ShadowBias;
    /// <summary>Offset along the surface normal in shadow map texels; raise it on acne at grazing angles.</summary>
    public float ShadowNormalBias;
    /// <summary>0 = shadows have no effect, 1 = shadowed areas get no direct light.</summary>
    public float ShadowStrength;

    /// <summary>A white directional light of unit intensity.</summary>
    public static Light Default => new();

    public Light()
    {
        Type = LightType.Directional;
        Color = Vector3.One;
        Intensity = 1f;
        Range = 0f;
        InnerConeAngle = MathF.PI / 8f;
        OuterConeAngle = MathF.PI / 4f;
        Size = Vector2.One;
        CastShadow = false;
        Enabled = true;
        ShadowBias = 0.0005f;
        ShadowNormalBias = 1.5f;
        ShadowStrength = 1f;
    }

    /// <summary>Sun style light; aim it by rotating the node it sits on.</summary>
    public static Light Directional(Vector3 color, float intensity = 1f) =>
        Default with { Type = LightType.Directional, Color = color, Intensity = intensity };

    /// <summary>Omnidirectional light with inverse square falloff.</summary>
    public static Light Point(Vector3 color, float intensity = 10f, float range = 0f) =>
        Default with { Type = LightType.Point, Color = color, Intensity = intensity, Range = range };

    /// <summary>Cone light with a smooth penumbra between the two angles.</summary>
    public static Light Spot(Vector3 color, float intensity = 20f, float range = 0f, float innerAngle = MathF.PI / 8f, float outerAngle = MathF.PI / 4f) =>
        Default with
        {
            Type = LightType.Spot,
            Color = color,
            Intensity = intensity,
            Range = range,
            InnerConeAngle = innerAngle,
            OuterConeAngle = outerAngle,
        };

    /// <summary>Constant term added to every lit fragment.</summary>
    public static Light Ambient(Vector3 color, float intensity = 0.1f) =>
        Default with { Type = LightType.Ambient, Color = color, Intensity = intensity };

    internal NativeLightDesc ToNative() => new()
    {
        Kind = (uint)Type,
        Color = Color,
        Intensity = Intensity,
        Range = Range,
        InnerConeAngle = InnerConeAngle,
        OuterConeAngle = OuterConeAngle,
        Width = Size.X,
        Height = Size.Y,
        CastShadow = CastShadow ? 1 : 0,
        Enabled = Enabled ? 1 : 0,
        ShadowBias = ShadowBias,
        ShadowNormalBias = ShadowNormalBias,
        ShadowStrength = ShadowStrength,
    };
}

/// <summary>A perspective or orthographic camera, attached to a scene node.</summary>
public struct Camera
{
    /// <summary>False for an orthographic camera.</summary>
    public bool IsPerspective;
    /// <summary>Vertical field of view in radians (perspective only).</summary>
    public float FieldOfView;
    /// <summary>Height of the view volume in world units (orthographic only).</summary>
    public float OrthographicHeight;
    /// <summary>Aspect ratio override; 0 follows the render target.</summary>
    public float AspectRatio;
    public float Near;
    public float Far;

    /// <summary>Perspective camera; <paramref name="fieldOfView"/> is in radians.</summary>
    public static Camera Perspective(float fieldOfView = MathF.PI / 4f, float near = 0.1f, float far = 1000f) => new()
    {
        IsPerspective = true,
        FieldOfView = fieldOfView,
        Near = near,
        Far = far,
    };

    /// <summary>Orthographic camera covering <paramref name="height"/> world units vertically.</summary>
    public static Camera Orthographic(float height = 10f, float near = 0.1f, float far = 1000f) => new()
    {
        IsPerspective = false,
        OrthographicHeight = height,
        Near = near,
        Far = far,
    };

    internal NativeCameraDesc ToNative() => new()
    {
        Projection = IsPerspective ? 0u : 1u,
        FovY = FieldOfView,
        OrthoHeight = OrthographicHeight,
        Aspect = AspectRatio,
        Near = Near,
        Far = Far,
    };

    internal static Camera FromNative(in NativeCameraDesc desc) => new()
    {
        IsPerspective = desc.Projection == 0,
        FieldOfView = desc.FovY,
        OrthographicHeight = desc.OrthoHeight,
        AspectRatio = desc.Aspect,
        Near = desc.Near,
        Far = desc.Far,
    };
}

/// <summary>Background, ambient light, fog and image based lighting settings.</summary>
public struct SceneEnvironment
{
    /// <summary>Clear colour in linear RGB; W is the clear alpha.</summary>
    public Vector4 Background;
    public Vector3 AmbientColor;
    public float AmbientIntensity;
    public Vector3 FogColor;
    /// <summary>Exponential squared fog density; 0 disables fog.</summary>
    public float FogDensity;
    public float FogStart;
    public float FogEnd;
    /// <summary>Equirectangular HDR map used for image based lighting.</summary>
    public Texture? EnvironmentMap;
    public float EnvironmentIntensity;

    /// <summary>Dark background, a touch of ambient light, no fog.</summary>
    public static SceneEnvironment Default => new();

    public SceneEnvironment()
    {
        Background = new Vector4(0.02f, 0.02f, 0.03f, 1f);
        AmbientColor = Vector3.One;
        AmbientIntensity = 0.03f;
        FogColor = new Vector3(0.5f, 0.55f, 0.6f);
        FogDensity = 0f;
        FogStart = 10f;
        FogEnd = 100f;
        EnvironmentMap = null;
        EnvironmentIntensity = 1f;
    }

    internal NativeEnvironmentDesc ToNative() => new()
    {
        Background = Background,
        AmbientColor = AmbientColor,
        AmbientIntensity = AmbientIntensity,
        FogColor = FogColor,
        FogDensity = FogDensity,
        FogStart = FogStart,
        FogEnd = FogEnd,
        EnvironmentMap = EnvironmentMap?.Id ?? 0,
        EnvironmentIntensity = EnvironmentIntensity,
    };
}
