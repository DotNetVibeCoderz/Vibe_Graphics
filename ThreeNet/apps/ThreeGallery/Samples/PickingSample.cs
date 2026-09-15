using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>Click a cube to select it: raycasting against the scene graph.</summary>
public sealed class PickingSample : GallerySample
{
    private readonly Dictionary<uint, Material> _materials = [];
    private Material _highlight = null!;
    private Node? _selected;
    private Geometry _cube = null!;

    public override string Title => "Raycast picking";

    public override string Category => "Interaction";

    public override string Summary => "Click a cube: the pointer becomes a world ray and the nearest triangle hit wins.";

    public override void Build(Scene scene)
    {
        _materials.Clear();
        _selected = null;

        _cube = scene.CreateBoxGeometry(0.9f, 0.9f, 0.9f);
        _highlight = scene.CreateMaterial(MaterialOptions.Pbr(Colors.Orange, 0.2f, 0.25f) with
        {
            Emissive = new Vector3(0.8f, 0.45f, 0.12f),
            EmissiveIntensity = 1.4f,
        });

        Random random = new(7);
        for (int x = -2; x <= 2; x++)
        {
            for (int z = -2; z <= 2; z++)
            {
                Vector4 color = MathHelpers.FromHex(0x3E63DD);
                color = Vector4.Lerp(color, MathHelpers.FromHex(0x30A46C), (float)random.NextDouble());
                Material material = scene.CreateMaterial(MaterialOptions.Pbr(color, 0.1f, 0.45f));

                Node node = scene.AddMesh(_cube, material, name: $"cube {x},{z}");
                node.Position = new Vector3(x * 1.5f, (float)random.NextDouble() * 0.8f, z * 1.5f);
                _materials[node.Id] = material;
            }
        }

        Material floorMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0x1B1F29), 0f, 0.9f));
        Node floor = scene.AddMesh(scene.CreatePlaneGeometry(40f, 40f), floorMaterial, name: "floor");
        floor.EulerAngles = new Vector3(-MathF.PI / 2f, 0f, 0f);
        floor.Position = new Vector3(0f, -0.8f, 0f);

        Node key = scene.AddLight(Light.Directional(Vector3.One, 3f));
        key.Position = new Vector3(4f, 7f, 5f);
        key.LookAt(Vector3.Zero);

        scene.Environment = scene.Environment with { AmbientIntensity = 0.12f };
    }

    public override void OnPick(Scene scene, Ray ray)
    {
        IReadOnlyList<RayHit> hits = scene.Raycast(ray);

        // Restore the previous selection before highlighting the new one.
        if (_selected is { } previous && _materials.TryGetValue(previous.Id, out Material? original))
        {
            previous.AttachMesh(_cube, original);
        }

        _selected = null;
        foreach (RayHit hit in hits)
        {
            if (!_materials.ContainsKey(hit.Node.Id))
            {
                continue;
            }

            hit.Node.AttachMesh(_cube, _highlight);
            _selected = hit.Node;
            break;
        }
    }

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = Vector3.Zero;
        orbit.Distance = 12f;
        orbit.Yaw = 0.7f;
        orbit.Pitch = 0.55f;
    }
}
