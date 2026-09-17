using System.Numerics;
using ThreeNet;

namespace MotoCross.Game;

/// <summary>
/// Detailed props generated with Rodin (Hyper3D): each GLB is imported once
/// into a hidden prototype, then placed as many times as needed with
/// <see cref="Node.Clone"/>, so every instance shares the same GPU buffers and
/// textures. When an asset is missing the caller falls back to primitives.
/// </summary>
public sealed class ModelLibrary
{
    private readonly Scene _scene;
    private readonly Node _prototypes;
    private readonly Dictionary<string, Prototype?> _cache = [];

    private sealed record Prototype(Node Root, Vector3 Size, Vector3 Centre, float Bottom);

    public ModelLibrary(Scene scene)
    {
        _scene = scene;
        _prototypes = scene.CreateNode(null, "model-prototypes");
        _prototypes.Visible = false;
    }

    public static string AssetPath(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name + ".glb");

    public bool Has(string name) => Load(name) is not null;

    private Prototype? Load(string name)
    {
        if (_cache.TryGetValue(name, out Prototype? cached))
        {
            return cached;
        }

        Prototype? prototype = null;
        string path = AssetPath(name);
        if (File.Exists(path))
        {
            try
            {
                Node holder = _scene.CreateNode(_prototypes, name);
                ImportResult import = _scene.LoadGltf(path, holder);
                BoundingBox bounds = _scene.GetBounds(import.Root);
                prototype = new Prototype(holder, bounds.Max - bounds.Min, (bounds.Min + bounds.Max) * 0.5f, bounds.Min.Y);
            }
            catch (ThreeNetException)
            {
                prototype = null;
            }
        }

        _cache[name] = prototype;
        return prototype;
    }

    /// <summary>
    /// Places a copy of <paramref name="name"/> so its longest horizontal side is
    /// <paramref name="length"/> metres, standing on <paramref name="position"/>
    /// and turned by <paramref name="yaw"/>. Returns null when the asset is missing.
    /// </summary>
    public Node? Place(string name, Node? parent, Vector3 position, float yaw, float length, float yawOffset = 0f, bool fitHeight = false, float tilt = 0f)
    {
        if (Load(name) is not { } prototype)
        {
            return null;
        }

        // Tall props (lamps) are sized by height, everything else by footprint.
        float reference = fitHeight ? prototype.Size.Y : MathF.Max(prototype.Size.X, prototype.Size.Z);
        float scale = length / MathF.Max(reference, 1e-3f);
        Node pivot = _scene.CreateNode(parent, name);
        pivot.Position = position;
        pivot.EulerAngles = new Vector3(tilt, yaw, 0f);

        Node copy = prototype.Root.Clone(pivot);
        copy.Visible = true;
        copy.Scale = new Vector3(scale);
        copy.EulerAngles = new Vector3(0f, yawOffset, 0f);
        // Centre the footprint on the pivot and stand the model on the floor.
        Vector3 offset = new(prototype.Centre.X * scale, prototype.Bottom * scale, prototype.Centre.Z * scale);
        copy.Position = -Vector3.Transform(offset, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawOffset));
        return pivot;
    }
}
