using Stopwatch = System.Diagnostics.Stopwatch;
using System.Numerics;
using System.Runtime.InteropServices;
using Android.Runtime;
using Android.Util;
using Android.Views;
using ThreeNet;

namespace ThreeNet.Samples.AndroidApp;

/// <summary>
/// Live rendering straight into an Android surface: no offscreen readback, no
/// bitmap, the GPU draws into the view. The surface arrives as a Java
/// <c>Surface</c>, which <c>ANativeWindow_fromSurface</c> turns into the native
/// window handle <see cref="Renderer.CreateForAndroid"/> wants.
/// </summary>
/// <remarks>
/// The render loop runs on its own thread: the surface callbacks hand it the
/// window and take it back, and the thread owns the renderer for as long as it
/// holds that window. One finger orbits, two fingers pinch to zoom.
/// </remarks>
// Exported so the device test can launch it straight from adb; the app
// itself opens it from the button on the first screen.
[Activity(Label = "Three.Net live", Name = "com.gravicode.threenet.samples.LiveActivity", Exported = true)]
public class LiveActivity : Activity, ISurfaceHolderCallback
{
    private const string Tag = "ThreeNet";

    [DllImport("android")]
    private static extern nint ANativeWindow_fromSurface(nint env, nint surface);

    [DllImport("android")]
    private static extern void ANativeWindow_release(nint window);

    private readonly ManualResetEventSlim _surfaceReady = new(false);
    private readonly object _gate = new();

    private SurfaceView _view = null!;
    private TextView _status = null!;
    private Thread? _renderThread;
    private volatile bool _running;
    private nint _window;
    private int _width;
    private int _height;

    // Touch state, read by the render thread.
    private volatile float _yaw = 0.9f;
    private volatile float _pitch = 0.32f;
    private volatile float _distance = 14f;
    private float _lastX;
    private float _lastY;
    private float _lastSpan;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        LinearLayout layout = new(this) { Orientation = Orientation.Vertical };
        _status = new TextView(this) { Text = "Starting the renderer..." };
        _status.SetPadding(24, 16, 24, 16);
        _view = new SurfaceView(this);
        _view.Holder!.AddCallback(this);

        layout.AddView(_status, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        layout.AddView(_view, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        SetContentView(layout);
    }

    // ------------------------------------------------------------- surface

    public void SurfaceCreated(ISurfaceHolder holder)
    {
        nint surface = holder.Surface?.Handle ?? nint.Zero;
        if (surface == nint.Zero)
        {
            Log.Error(Tag, "THREENET_FAILED the surface has no handle");
            return;
        }

        lock (_gate)
        {
            _window = ANativeWindow_fromSurface(JNIEnv.Handle, surface);
        }

        if (_window == nint.Zero)
        {
            Log.Error(Tag, "THREENET_FAILED ANativeWindow_fromSurface returned null");
            return;
        }

        _running = true;
        _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "threenet.render" };
        _renderThread.Start();
    }

    public void SurfaceChanged(ISurfaceHolder holder, [GeneratedEnum] global::Android.Graphics.Format format, int width, int height)
    {
        _width = width;
        _height = height;
        _surfaceReady.Set();
    }

    public void SurfaceDestroyed(ISurfaceHolder holder)
    {
        // The renderer holds the surface, so it has to go first.
        _running = false;
        _renderThread?.Join(2000);
        _renderThread = null;
        _surfaceReady.Reset();

        lock (_gate)
        {
            if (_window != nint.Zero)
            {
                ANativeWindow_release(_window);
                _window = nint.Zero;
            }
        }
    }

    protected override void OnDestroy()
    {
        SurfaceDestroyed(_view.Holder!);
        base.OnDestroy();
    }

    // --------------------------------------------------------------- touch

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null)
        {
            return false;
        }

        if (e.PointerCount >= 2)
        {
            float dx = e.GetX(0) - e.GetX(1);
            float dy = e.GetY(0) - e.GetY(1);
            float span = MathF.Sqrt((dx * dx) + (dy * dy));
            if (e.ActionMasked is MotionEventActions.PointerDown or MotionEventActions.Down || _lastSpan <= 0f)
            {
                _lastSpan = span;
            }
            else if (e.ActionMasked == MotionEventActions.Move && span > 0f)
            {
                // Pinching apart pulls the camera in.
                _distance = Math.Clamp(_distance * (_lastSpan / span), 3f, 60f);
                _lastSpan = span;
            }

            return true;
        }

        _lastSpan = 0f;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _lastX = e.GetX();
                _lastY = e.GetY();
                return true;

            case MotionEventActions.Move:
                float moveX = e.GetX() - _lastX;
                float moveY = e.GetY() - _lastY;
                _lastX = e.GetX();
                _lastY = e.GetY();
                _yaw -= moveX * 0.006f;
                _pitch = Math.Clamp(_pitch + (moveY * 0.006f), -1.3f, 1.3f);
                return true;
        }

        return base.OnTouchEvent(e);
    }

    // --------------------------------------------------------- render loop

    private void RenderLoop()
    {
        nint window;
        lock (_gate)
        {
            window = _window;
        }

        if (!_surfaceReady.Wait(5000) || window == nint.Zero)
        {
            Log.Error(Tag, "THREENET_FAILED the surface never reported a size");
            return;
        }

        Scene? scene = null;
        Renderer? renderer = null;
        try
        {
            renderer = Renderer.CreateForAndroid(window, RendererOptions.Default with
            {
                Width = _width,
                Height = _height,
                VSync = true,
                MsaaSamples = 4,
                Shadows = true,
                ShadowMapSize = 1024,
                ShadowCascades = 2,
                ShadowDistance = 40f,
                Bloom = true,
                BloomIntensity = 0.4f,
            });

            scene = BuildScene(out Node camera, out Node turntable, out OverlayElement readout);
            Log.Info(Tag, $"THREENET_LIVE_OK adapter: {renderer.AdapterName}, ABI {ThreeNetRuntime.NativeAbiVersion}, {_width}x{_height}");
            RunOnUiThread(() => _status.Text = $"{renderer.AdapterName}\ndrag to orbit, pinch to zoom");

            Stopwatch clock = Stopwatch.StartNew();
            double previous = 0;
            int frames = 0;
            double fpsWindow = 0;
            float fps = 0f;
            int resizeWidth = _width;
            int resizeHeight = _height;

            while (_running)
            {
                double now = clock.Elapsed.TotalSeconds;
                float delta = (float)Math.Clamp(now - previous, 1.0 / 1000, 0.25);
                previous = now;

                if (_width != resizeWidth || _height != resizeHeight)
                {
                    resizeWidth = _width;
                    resizeHeight = _height;
                    renderer.Resize(resizeWidth, resizeHeight);
                }

                turntable.EulerAngles = new Vector3(0f, (float)now * 0.4f, 0f);
                PlaceCamera(camera);
                scene.UpdateAnimations(delta);
                scene.Physics.Step(delta);
                renderer.Render(scene, camera);

                frames++;
                fpsWindow += delta;
                if (fpsWindow >= 0.5)
                {
                    fps = (float)(frames / fpsWindow);
                    frames = 0;
                    fpsWindow = 0;
                    FrameStats stats = renderer.Stats;
                    readout.Text =
                        $"{fps,5:F0} fps   {stats.DrawCalls} draws   {stats.Triangles:N0} tris   {stats.CpuTimeMs:F1} ms cpu";
                }
            }
        }
        catch (Exception exception)
        {
            Log.Error(Tag, "THREENET_FAILED " + exception);
            RunOnUiThread(() => _status.Text = exception.Message);
        }
        finally
        {
            renderer?.Dispose();
            scene?.Dispose();
        }
    }

    private void PlaceCamera(Node camera)
    {
        float pitch = _pitch;
        float cos = MathF.Cos(pitch);
        Vector3 target = new(0f, 1.2f, 0f);
        camera.Position = target + (new Vector3(MathF.Sin(_yaw) * cos, MathF.Sin(pitch), MathF.Cos(_yaw) * cos) * _distance);
        camera.LookAt(target);
    }

    /// <summary>A small set that exercises shadows, PBR, physics and the HUD.</summary>
    private Scene BuildScene(out Node camera, out Node turntable, out OverlayElement readout)
    {
        Scene scene = new()
        {
            Environment = SceneEnvironment.Default with
            {
                Background = new Vector4(0.03f, 0.04f, 0.06f, 1f),
                AmbientColor = new Vector3(0.5f, 0.6f, 0.8f),
                AmbientIntensity = 0.25f,
            },
        };

        Material floorMaterial = scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.16f, 0.17f, 0.2f, 1f), 0f, 0.6f));
        Node floor = scene.AddMesh(scene.CreateBoxGeometry(40f, 0.4f, 40f), floorMaterial, name: "floor");
        floor.Position = new Vector3(0f, -0.2f, 0f);
        scene.Physics.AddCollider(floor, ColliderOptions.Box(new Vector3(20f, 0.2f, 20f)));

        // A turning rig of metals, so the lighting has something to move across.
        turntable = scene.CreateNode(name: "turntable");
        uint[] colours = [0xE0564E, 0xE8A93C, 0x8FCB6B, 0x45C4CF, 0x5B93D6, 0x9A86F0];
        for (int i = 0; i < colours.Length; i++)
        {
            float angle = MathF.Tau * i / colours.Length;
            Material material = scene.CreateMaterial(MaterialOptions.Pbr(
                MathHelpers.FromHex(colours[i]),
                i % 2 == 0 ? 1f : 0.1f,
                0.15f + (i * 0.12f)));
            Node node = scene.AddMesh(
                i % 3 == 0 ? scene.CreateSphereGeometry(0.6f, 40, 22)
                    : i % 3 == 1 ? scene.CreateTorusGeometry(0.5f, 0.18f, 20, 56)
                    : scene.CreateBoxGeometry(0.9f, 0.9f, 0.9f, 2),
                material,
                parent: turntable,
                name: $"prop-{i}");
            node.Position = new Vector3(MathF.Sin(angle) * 3f, 0.8f, MathF.Cos(angle) * 3f);
        }

        // Falling boxes: the solver runs on the device too.
        Geometry crate = scene.CreateBoxGeometry(0.5f, 0.5f, 0.5f);
        Material crateMaterial = scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.55f, 0.42f, 0.28f, 1f), 0f, 0.7f));
        for (int i = 0; i < 12; i++)
        {
            Node box = scene.AddMesh(crate, crateMaterial, name: $"crate-{i}");
            box.Position = new Vector3(((i % 4) - 1.5f) * 0.6f, 3f + (i * 0.7f), ((i / 4) - 1f) * 0.6f);
            scene.Physics.Add(box, RigidBodyOptions.Dynamic, ColliderOptions.Box(new Vector3(0.25f)) with
            {
                Restitution = 0.25f,
            });
        }

        Node sun = scene.AddLight(Light.Directional(new Vector3(1f, 0.96f, 0.9f), 3.4f) with { CastShadow = true }, name: "sun");
        sun.Position = new Vector3(6f, 10f, 6f);
        sun.LookAt(Vector3.Zero);
        scene.AddLight(Light.Ambient(new Vector3(0.45f, 0.55f, 0.8f), 0.3f), name: "sky");

        camera = scene.AddCamera(Camera.Perspective(55f.ToRadians(), 0.05f, 200f));
        PlaceCamera(camera);

        scene.Overlay.AddPanel(new Vector2(16f, 16f), new Vector2(560f, 46f), new Vector4(0f, 0f, 0f, 0.55f), cornerRadius: 8f);
        readout = scene.Overlay.AddText("starting", new Vector2(32f, 30f), 22f, Vector4.One);
        return scene;
    }
}
