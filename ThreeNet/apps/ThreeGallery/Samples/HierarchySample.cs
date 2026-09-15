using System.Numerics;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Samples;

/// <summary>Parent / child transforms, demonstrated with a small solar system.</summary>
public sealed class HierarchySample : GallerySample
{
    private readonly List<(Node Pivot, float Speed)> _orbits = [];
    private Node _sun = null!;

    public override string Title => "Scene graph";

    public override string Category => "Scene graph";

    public override string Summary => "Rotating a parent node carries its children with it: orbits for free, no matrix maths.";

    public override void Build(Scene scene)
    {
        _orbits.Clear();

        Material sunMaterial = scene.CreateMaterial(MaterialOptions.Basic(MathHelpers.FromHex(0xFFC53D)) with
        {
            Emissive = new Vector3(1f, 0.72f, 0.2f),
            EmissiveIntensity = 3.2f,
        });
        _sun = scene.AddMesh(scene.CreateSphereGeometry(1.1f, 48, 32), sunMaterial, name: "sun");
        scene.AddLight(Light.Point(new Vector3(1f, 0.9f, 0.75f), 80f, range: 60f), _sun, "sun light");

        Geometry planetGeometry = scene.CreateSphereGeometry(1f, 32, 24);
        (float Distance, float Radius, float Speed, uint Color)[] planets =
        [
            (2.4f, 0.22f, 1.4f, 0xB08D6A),
            (3.6f, 0.34f, 0.95f, 0xD98E4A),
            (5.0f, 0.38f, 0.7f, 0x4F8FE5),
            (6.6f, 0.30f, 0.5f, 0xC1572F),
            (8.8f, 0.72f, 0.32f, 0xD9B27C),
        ];

        foreach ((float distance, float radius, float speed, uint color) in planets)
        {
            // The pivot sits at the centre and rotates; the planet is offset on it.
            Node pivot = scene.CreateNode(name: $"pivot {distance}");
            Material material = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(color), 0.1f, 0.6f));
            Node planet = scene.AddMesh(planetGeometry, material, pivot, $"planet {distance}");
            planet.Position = new Vector3(distance, 0f, 0f);
            planet.Scale = new Vector3(radius);
            _orbits.Add((pivot, speed));

            // A moon on the largest planet shows a second level of nesting.
            if (radius > 0.6f)
            {
                Node moonPivot = scene.CreateNode(planet, "moon pivot");
                Material moonMaterial = scene.CreateMaterial(MaterialOptions.Pbr(MathHelpers.FromHex(0xCFD4DC), 0f, 0.8f));
                Node moon = scene.AddMesh(planetGeometry, moonMaterial, moonPivot, "moon");
                moon.Position = new Vector3(2.2f, 0f, 0f);
                moon.Scale = new Vector3(0.3f);
                _orbits.Add((moonPivot, 2.4f));
            }
        }

        scene.Environment = scene.Environment with
        {
            Background = MathHelpers.FromHex(0x05060B),
            AmbientIntensity = 0.02f,
        };
    }

    public override void Update(Scene scene, float deltaSeconds, double totalSeconds)
    {
        foreach ((Node pivot, float speed) in _orbits)
        {
            pivot.EulerAngles = new Vector3(0f, (float)totalSeconds * speed * 0.4f, 0f);
        }

        _sun.EulerAngles = new Vector3(0f, (float)totalSeconds * 0.1f, 0f);
    }

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = Vector3.Zero;
        orbit.Distance = 22f;
        orbit.Yaw = 0.4f;
        orbit.Pitch = 0.55f;
    }
}
