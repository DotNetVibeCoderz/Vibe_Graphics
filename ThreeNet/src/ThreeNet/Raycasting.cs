using System.Numerics;
using ThreeNet.Interop;

namespace ThreeNet;

/// <summary>A ray in world space. The direction is normalised on construction.</summary>
public readonly struct Ray(Vector3 origin, Vector3 direction)
{
    public Vector3 Origin { get; } = origin;

    public Vector3 Direction { get; } = direction == Vector3.Zero ? -Vector3.UnitZ : Vector3.Normalize(direction);

    /// <summary>Point at <paramref name="distance"/> along the ray.</summary>
    public Vector3 At(float distance) => Origin + (Direction * distance);

    public override string ToString() => $"Ray(origin: {Origin}, direction: {Direction})";
}

/// <summary>One intersection reported by <see cref="Scene.Raycast"/>.</summary>
public readonly record struct RayHit(
    Node Node,
    float Distance,
    Vector3 Point,
    Vector3 Normal,
    int TriangleIndex,
    Vector2 Barycentric);

/// <summary>Filters narrowing which nodes take part in a cast.</summary>
public struct RaycastOptions
{
    /// <summary>Maximum distance along the ray; 0 or negative means unlimited.</summary>
    public float MaxDistance;
    /// <summary>Only nodes sharing a bit with this mask are tested.</summary>
    public uint Layers;
    /// <summary>Skip nodes hidden through <see cref="Node.Visible"/>.</summary>
    public bool VisibleOnly;
    /// <summary>Also report hits on back faces.</summary>
    public bool IncludeBackFaces;

    /// <summary>Unlimited distance, every layer, visible nodes only.</summary>
    public static RaycastOptions Default => new();

    public RaycastOptions()
    {
        MaxDistance = 0f;
        Layers = uint.MaxValue;
        VisibleOnly = true;
        IncludeBackFaces = false;
    }

    internal NativeRaycastOptions ToNative() => new()
    {
        MaxDistance = MaxDistance,
        Layers = Layers,
        VisibleOnly = VisibleOnly ? 1 : 0,
        IncludeBackFaces = IncludeBackFaces ? 1 : 0,
    };
}

/// <summary>An axis aligned bounding box in world space.</summary>
public readonly record struct BoundingBox(Vector3 Min, Vector3 Max)
{
    /// <summary>True when the box holds no geometry.</summary>
    public bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;

    public Vector3 Center => IsEmpty ? Vector3.Zero : (Min + Max) * 0.5f;

    public Vector3 Size => IsEmpty ? Vector3.Zero : Max - Min;

    /// <summary>Radius of the sphere enclosing the box.</summary>
    public float Radius => IsEmpty ? 0f : Size.Length() * 0.5f;
}
