using System.Numerics;
using System.Runtime.InteropServices;

namespace ThreeNet.Interop;

/// <summary>Status codes returned by the native entry points.</summary>
internal static class NativeStatus
{
    public const int Ok = 0;
    public const int Error = -1;
    public const int NullPointer = -2;
    public const int InvalidHandle = -3;
    public const int InvalidArgument = -4;
    public const int BufferTooSmall = -5;
}

/// <summary>Interleaved vertex, must match <c>threenet_core::geometry::Vertex</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 TexCoord;
    /// <summary>xyz = tangent, w = bitangent handedness (+1 / -1).</summary>
    public Vector4 Tangent;

    public Vertex(Vector3 position, Vector3 normal, Vector2 texCoord)
    {
        Position = position;
        Normal = normal;
        TexCoord = texCoord;
        Tangent = new Vector4(1f, 0f, 0f, 1f);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTransform
{
    public Vector3 Translation;
    public Vector4 Rotation;
    public Vector3 Scale;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMaterialDesc
{
    public uint Shading;
    public uint AlphaMode;
    public uint CullMode;
    public Vector4 BaseColor;
    public Vector3 Emissive;
    public float EmissiveIntensity;
    public float Metallic;
    public float Roughness;
    public Vector3 Specular;
    public float Shininess;
    public float Reflectance;
    public float NormalScale;
    public float OcclusionStrength;
    public float AlphaCutoff;
    public Vector2 UvScale;
    public Vector2 UvOffset;
    public int DepthWrite;
    public int DepthTest;
    public int Wireframe;
    public int RenderOrder;
    public uint BaseColorTexture;
    public uint NormalTexture;
    public uint MetallicRoughnessTexture;
    public uint EmissiveTexture;
    public uint OcclusionTexture;
    public uint Shader;
    public Vector4 Custom0;
    public Vector4 Custom1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeLightDesc
{
    public uint Kind;
    public Vector3 Color;
    public float Intensity;
    public float Range;
    public float InnerConeAngle;
    public float OuterConeAngle;
    public float Width;
    public float Height;
    public int CastShadow;
    public int Enabled;
    public float ShadowBias;
    public float ShadowNormalBias;
    public float ShadowStrength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCameraDesc
{
    public uint Projection;
    public float FovY;
    public float OrthoHeight;
    public float Aspect;
    public float Near;
    public float Far;
    public float AngleLeft;
    public float AngleRight;
    public float AngleUp;
    public float AngleDown;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeBodyDesc
{
    public uint Kind;
    public float AdditionalMass;
    public float LinearDamping;
    public float AngularDamping;
    public float GravityScale;
    public int Ccd;
    public int LockRotations;
    public int CanSleep;
    public Vector3 LinearVelocity;
    public Vector3 AngularVelocity;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeColliderDesc
{
    public uint Shape;
    public Vector3 HalfExtents;
    public float Radius;
    public float HalfHeight;
    public uint Geometry;
    public Vector3 Offset;
    public Vector4 Rotation;
    public float Friction;
    public float Restitution;
    public float Density;
    public int Sensor;
    public uint Membership;
    public uint Filter;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePhysicsHit
{
    public uint Node;
    public float Distance;
    public Vector3 Point;
    public Vector3 Normal;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeContactEvent
{
    public uint NodeA;
    public uint NodeB;
    public int Started;
    public int Sensor;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSoundDesc
{
    public uint Clip;
    public float Gain;
    public float Pitch;
    public int Looping;
    public int Spatial;
    public Vector3 Position;
    public Vector3 Velocity;
    public float MinDistance;
    public float MaxDistance;
    public float Rolloff;
    public uint Node;
    public int Paused;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeXrInfo
{
    public int LoaderFound;
    public int RuntimeFound;
    public int HeadsetFound;
    public uint VendorId;
    public uint RecommendedWidth;
    public uint RecommendedHeight;
    public uint ViewCount;
    public int OrientationTracking;
    public int PositionTracking;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeEnvironmentDesc
{
    public Vector4 Background;
    public Vector3 AmbientColor;
    public float AmbientIntensity;
    public Vector3 FogColor;
    public float FogDensity;
    public float FogStart;
    public float FogEnd;
    public uint EnvironmentMap;
    public float EnvironmentIntensity;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRendererDesc
{
    public uint Width;
    public uint Height;
    public int VSync;
    public uint MsaaSamples;
    public float Exposure;
    public uint ToneMapping;
    public int Bloom;
    public float BloomIntensity;
    public float BloomThreshold;
    public int FrustumCulling;
    public uint PowerPreference;
    public int BgraOutput;
    public int Shadows;
    public uint ShadowMapSize;
    public float ShadowDistance;
    public uint ShadowCascades;
    public uint ShadowSoftness;
    public int Ssao;
    public float SsaoRadius;
    public float SsaoIntensity;
    public float SsaoBias;
    public uint SsaoSamples;
    public float SsaoDirectStrength;
    public uint RenderPath;
    public int DepthOfField;
    public float DofFocusDistance;
    public float DofFocusRange;
    public float DofMaxBlur;
    public int MotionBlur;
    public float MotionBlurStrength;
    public uint MotionBlurSamples;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFrameStats
{
    public uint DrawCalls;
    public uint Triangles;
    public uint VisibleNodes;
    public uint CulledNodes;
    public uint Lights;
    public float CpuTimeMs;
    public uint ShadowLayers;
    public uint ShadowDrawCalls;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRayHit
{
    public uint Node;
    public float Distance;
    public Vector3 Point;
    public Vector3 Normal;
    public uint Triangle;
    public Vector2 Barycentric;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeOverlayElement
{
    public uint Kind;
    public uint Parent;
    public uint Anchor;
    public int Layer;
    public Vector2 Offset;
    public Vector2 Size;
    public Vector4 Color;
    public Vector4 BorderColor;
    public float BorderWidth;
    public float CornerRadius;
    public int Visible;
    public int Interactive;
    public uint Texture;
    public Vector4 Uv;
    public uint Font;
    public float FontSize;
    public uint Align;
    public uint VerticalAlign;
    public int Wrap;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeGamepadState
{
    public int Connected;
    public int IsVirtual;
    public uint Buttons;
    public uint PreviousButtons;
    public float LeftX;
    public float LeftY;
    public float RightX;
    public float RightY;
    public float LeftTrigger;
    public float RightTrigger;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAssetStats
{
    public uint PendingTextures;
    public uint CachedTextures;
    public uint CachedModels;
    public uint Padding;
    public ulong CacheHits;
    public ulong CacheMisses;
    public ulong StreamedTextures;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeImportResult
{
    public uint Root;
    public uint NodeCount;
    public uint GeometryCount;
    public uint MaterialCount;
    public uint TextureCount;
    public uint AnimationCount;
    public uint SkinCount;
}

[StructLayout(LayoutKind.Sequential)]
internal record struct NativePlayerDesc
{
    public float Time;
    public float Speed;
    public float Weight;
    public int Looping;
    public int Playing;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSamplerDesc
{
    public uint WrapU;
    public uint WrapV;
    public int LinearFilter;
    public int Mipmaps;
    public uint Anisotropy;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRaycastOptions
{
    public float MaxDistance;
    public uint Layers;
    public int VisibleOnly;
    public int IncludeBackFaces;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeWindowDesc
{
    public byte* Title;
    public uint Width;
    public uint Height;
    public int Resizable;
    public int Decorations;
    public NativeRendererDesc Renderer;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAppCallbacks
{
    public nint UserData;
    public delegate* unmanaged[Cdecl]<nint, nint, void> OnInit;
    public delegate* unmanaged[Cdecl]<nint, nint, float, void> OnFrame;
    public delegate* unmanaged[Cdecl]<nint, nint, NativeInputEvent*, void> OnEvent;
    public delegate* unmanaged[Cdecl]<nint, int> OnClose;
}

/// <summary>Raw input event as delivered by the native window loop.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeInputEvent
{
    public uint Kind;
    public uint Code;
    public uint Modifiers;
    public float X;
    public float Y;
    public float DeltaX;
    public float DeltaY;
    public uint Repeat;
}
