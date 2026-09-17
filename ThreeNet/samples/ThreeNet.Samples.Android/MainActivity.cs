using Stopwatch = System.Diagnostics.Stopwatch;
using System.Numerics;
using Android.Graphics;
using Android.Util;
using Android.Widget;
using ThreeNet;

namespace ThreeNet.Samples.AndroidApp;

/// <summary>
/// Renders a lit, textured scene with the Three.Net core on the device GPU,
/// steps physics and draws the HUD, then shows the frame and logs the results
/// (logcat tag "ThreeNet").
/// </summary>
[Activity(Label = "Three.Net", MainLauncher = true, Name = "com.gravicode.threenet.samples.MainActivity")]
public class MainActivity : Activity
{
    private const string Tag = "ThreeNet";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        LinearLayout layout = new(this) { Orientation = Orientation.Vertical };
        TextView status = new(this) { Text = "Rendering..." };
        ImageView image = new(this);
        layout.AddView(status);
        layout.AddView(image);
        SetContentView(layout);

        try
        {
            (Bitmap bitmap, string report) = Render(512, 512);
            image.SetImageBitmap(bitmap);
            status.Text = report;
            Log.Info(Tag, "THREENET_OK " + report.Replace('\n', ' '));
        }
        catch (Exception exception)
        {
            status.Text = exception.ToString();
            Log.Error(Tag, "THREENET_FAILED " + exception);
        }
    }

    private static (Bitmap Bitmap, string Report) Render(int width, int height)
    {
        Stopwatch watch = Stopwatch.StartNew();
        using Scene scene = new();
        scene.Environment = scene.Environment with { Background = new Vector4(0.05f, 0.06f, 0.09f, 1f), AmbientIntensity = 0.3f };

        Node floor = scene.AddMesh(scene.CreateBoxGeometry(10f, 0.2f, 10f), scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.4f, 0.45f, 0.5f, 1f), 0f, 0.8f)));
        floor.Position = new Vector3(0f, -0.1f, 0f);
        scene.Physics.AddCollider(floor, ColliderOptions.Box(new Vector3(5f, 0.1f, 5f)));

        Node ball = scene.AddMesh(scene.CreateSphereGeometry(0.6f, 48, 24), scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.9f, 0.35f, 0.2f, 1f), 0.2f, 0.3f)));
        ball.Position = new Vector3(0f, 3f, 0f);
        scene.Physics.Add(ball, RigidBodyOptions.Dynamic, ColliderOptions.Sphere(0.6f));
        for (int i = 0; i < 120; i++)
        {
            scene.Physics.Step(1f / 60f);
        }

        Node torus = scene.AddMesh(scene.CreateTorusGeometry(0.5f, 0.18f, 24, 64), scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.3f, 0.8f, 0.9f, 1f), 0.9f, 0.25f)));
        torus.Position = new Vector3(1.6f, 0.9f, 0.5f);
        torus.EulerAngles = new Vector3(0.6f, 0.4f, 0f);

        Node sun = scene.AddLight(Light.Directional(Vector3.One, 3f));
        sun.Position = new Vector3(3f, 6f, 4f);
        sun.LookAt(Vector3.Zero);
        Node camera = scene.AddCamera(Camera.Perspective(0.9f), new Vector3(0f, 2.5f, 6f));
        camera.LookAt(new Vector3(0.4f, 0.6f, 0f));

        scene.Overlay.AddPanel(new Vector2(12f, 12f), new Vector2(300f, 44f), new Vector4(0f, 0f, 0f, 0.6f), cornerRadius: 10f);
        scene.Overlay.AddText("Three.Net on Android", new Vector2(24f, 22f), 22f, Vector4.One);

        using Renderer renderer = Renderer.CreateOffscreen(RendererOptions.Default with { Width = width, Height = height, MsaaSamples = 4 });
        renderer.Render(scene, camera);
        byte[] rgba = renderer.ReadPixels();

        int[] argb = new int[width * height];
        for (int i = 0; i < argb.Length; i++)
        {
            argb[i] = (rgba[(i * 4) + 3] << 24) | (rgba[i * 4] << 16) | (rgba[(i * 4) + 1] << 8) | rgba[(i * 4) + 2];
        }

        Bitmap bitmap = Bitmap.CreateBitmap(argb, width, height, Bitmap.Config.Argb8888!)!;
        int centre = ((height / 2 * width) + (width / 2)) * 4;
        string report = $"adapter: {renderer.AdapterName}\nABI {ThreeNetRuntime.NativeAbiVersion}, core {ThreeNetRuntime.NativeVersion}\n" +
            $"ball rests at y={ball.Position.Y:0.00}, draws {renderer.Stats.DrawCalls}, triangles {renderer.Stats.Triangles}\n" +
            $"centre pixel {rgba[centre]},{rgba[centre + 1]},{rgba[centre + 2]} in {watch.ElapsedMilliseconds} ms";
        return (bitmap, report);
    }
}
