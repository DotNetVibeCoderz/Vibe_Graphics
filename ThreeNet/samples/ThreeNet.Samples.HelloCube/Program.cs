// Three.Net - Hello Cube
// The smallest complete sample: a window, a lit scene and an orbiting camera.
// Made by Gravicode Studios - led by Kang Fadhil.

using System.Numerics;
using ThreeNet;

ThreeNetRuntime.Initialize(LogLevel.Warning);
Console.WriteLine($"Three.Net native core {ThreeNetRuntime.NativeVersion}");

using Scene scene = new()
{
    Environment = SceneEnvironment.Default with
    {
        Background = MathHelpers.FromHex(0x0B0D12),
        AmbientIntensity = 0.15f,
    },
};

// --- geometry and materials ------------------------------------------------
Geometry boxGeometry = scene.CreateBoxGeometry(1.2f, 1.2f, 1.2f);
Geometry floorGeometry = scene.CreatePlaneGeometry(20f, 20f);
Geometry sphereGeometry = scene.CreateSphereGeometry(0.6f);

Material boxMaterial = scene.CreateMaterial(MaterialOptions.Pbr(Colors.Orange, metallic: 0.1f, roughness: 0.35f));
Material floorMaterial = scene.CreateMaterial(MaterialOptions.Pbr(Colors.Gray, metallic: 0f, roughness: 0.9f));
Material sphereMaterial = scene.CreateMaterial(MaterialOptions.Pbr(Colors.Cyan, metallic: 1f, roughness: 0.2f));

Node cube = scene.AddMesh(boxGeometry, boxMaterial, name: "cube");
cube.Position = new Vector3(0f, 0.6f, 0f);

Node sphere = scene.AddMesh(sphereGeometry, sphereMaterial, name: "sphere");
sphere.Position = new Vector3(2f, 0.6f, -1f);

Node floor = scene.AddMesh(floorGeometry, floorMaterial, name: "floor");
floor.EulerAngles = new Vector3(-MathF.PI / 2f, 0f, 0f);

// --- lights ----------------------------------------------------------------
Node sun = scene.AddLight(Light.Directional(Vector3.One, 3f), name: "sun");
sun.Position = new Vector3(4f, 6f, 4f);
sun.LookAt(Vector3.Zero);

Node fill = scene.AddLight(Light.Point(new Vector3(0.4f, 0.6f, 1f), 12f, range: 12f), name: "fill");
fill.Position = new Vector3(-3f, 2.5f, 2f);

// --- camera ----------------------------------------------------------------
Node camera = scene.AddCamera(Camera.Perspective(55f.ToRadians()), new Vector3(0f, 2.5f, 6f));
camera.LookAt(new Vector3(0f, 0.6f, 0f));

// --- window ----------------------------------------------------------------
AppWindow window = new(WindowOptions.Default with
{
    Title = "Three.Net - Hello Cube",
    Width = 1280,
    Height = 720,
    Renderer = RendererOptions.Default with { MsaaSamples = 4, Bloom = true, BloomIntensity = 0.35f },
});

float elapsed = 0f;
float orbit = 0f;
bool orbiting = true;

window.Load += renderer =>
{
    Console.WriteLine($"GPU: {renderer.AdapterName}");
    Console.WriteLine("space = pause the orbit, escape = quit");
};

window.Render += (renderer, delta) =>
{
    elapsed += delta;
    if (orbiting)
    {
        orbit += delta * 0.4f;
    }

    cube.EulerAngles = new Vector3(elapsed * 0.7f, elapsed, 0f);
    sphere.Position = new Vector3(2f, 0.6f + (MathF.Sin(elapsed * 2f) * 0.35f), -1f);

    camera.Position = new Vector3(MathF.Sin(orbit) * 6f, 2.5f, MathF.Cos(orbit) * 6f);
    camera.LookAt(new Vector3(0f, 0.6f, 0f));

    renderer.Render(scene, camera);
};

window.Input += (renderer, input) =>
{
    switch (input.Kind)
    {
        case InputEventKind.KeyDown when input.Key == Key.Escape:
            System.Environment.Exit(0);
            break;
        case InputEventKind.KeyDown when input.Key == Key.Space && !input.IsRepeat:
            orbiting = !orbiting;
            break;
        case InputEventKind.MouseDown when input.Button == MouseButton.Left:
            PickAt(renderer, input.Position);
            break;
    }
};

window.Run();
return;

// Converts a click into a ray and reports what it hit.
void PickAt(Renderer renderer, Vector2 position)
{
    float ndcX = (position.X / renderer.Width * 2f) - 1f;
    float ndcY = 1f - (position.Y / renderer.Height * 2f);
    Ray ray = scene.CreateCameraRay(camera, ndcX, ndcY, renderer.AspectRatio);

    IReadOnlyList<RayHit> hits = scene.Raycast(ray);
    Console.WriteLine(hits.Count > 0
        ? $"picked '{hits[0].Node.Name}' at {hits[0].Distance:F2} units"
        : "nothing under the cursor");
}
