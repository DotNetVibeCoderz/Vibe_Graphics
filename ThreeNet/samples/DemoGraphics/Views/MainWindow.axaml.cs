using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DemoGraphics.Diagnostics;
using DemoGraphics.Framework;
using DemoGraphics.Scenes;
using ThreeNet;
using ThreeNet.Avalonia;

namespace DemoGraphics.Views;

/// <summary>Which of the five benches is on screen.</summary>
public enum AppMode
{
    Gallery,
    Laboratory,
    Benchmark,
    Inspect,
    Sandbox,
}

/// <summary>How much of the performance read-out is shown.</summary>
public enum HudMode
{
    Detailed,
    Compact,
    Hidden,
}

public partial class MainWindow : Window
{
    private readonly EnvironmentState _world = new();
    private readonly RenderControls _controls = new();
    private readonly RenderControls _compareControls = new();
    private readonly FrameLog _log = new();
    private readonly BenchmarkRunner _runner = new();
    private readonly SandboxScene _sandbox = new();
    private readonly DispatcherTimer _uiTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly List<(string Label, TextBlock Value)> _hudRows = [];

    private DemoScene? _active;
    private Scene? _scene;
    private Node? _camera;
    private OrbitController? _orbit;
    private GpuCapabilities _capabilities;
    private AppMode _mode = AppMode.Gallery;
    private HudMode _hudMode = HudMode.Detailed;
    private bool _compare;
    private double _wipe = 0.5;
    private Point _pressed;
    private bool _pressValid;
    private bool _loading;
    private RayHit? _selection;
    private string _benchmarkProfile = "Native, scene settings";

    public MainWindow()
    {
        InitializeComponent();

        BuildCatalogue();
        BuildHudRows();

        Ribbon.Log = _log;

        GalleryTab.Click += (_, _) => SetMode(AppMode.Gallery);
        LabTab.Click += (_, _) => SetMode(AppMode.Laboratory);
        BenchTab.Click += (_, _) => SetMode(AppMode.Benchmark);
        InspectTab.Click += (_, _) => SetMode(AppMode.Inspect);
        SandboxTab.Click += (_, _) => SetMode(AppMode.Sandbox);

        FilterBox.TextChanged += (_, _) => BuildCatalogue();
        SceneList.SelectionChanged += OnSceneSelected;

        ResetViewButton.Click += (_, _) => ResetView();
        SpinToggle.IsCheckedChanged += (_, _) =>
        {
            if (_orbit is not null)
            {
                _orbit.AutoRotate = SpinToggle.IsChecked == true;
            }
        };
        PauseToggle.IsCheckedChanged += (_, _) =>
        {
            bool paused = PauseToggle.IsChecked == true;
            Viewport.IsRendering = !paused;
            ViewportB.IsRendering = !paused && _compare;
            Status(paused ? "Rendering paused." : "Rendering.");
        };
        HudButton.Click += (_, _) => CycleHud();
        CaptureButton.Click += async (_, _) => await CaptureAsync();

        Viewport.Frame += OnFrame;
        Viewport.RendererCreated += OnRendererCreated;
        Viewport.RenderFailed += (_, message) => Status($"Renderer stopped: {message}");
        Viewport.PointerPressed += (_, e) =>
        {
            _pressed = e.GetPosition(Viewport);
            _pressValid = e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed;
        };
        Viewport.PointerReleased += OnViewportPointerReleased;
        ViewportHost.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                UpdateCompareClip();
            }
        };

        _world.Changed += () =>
        {
            _active?.ApplyEnvironment();
            UpdateWeatherText();
        };

        _uiTimer.Tick += (_, _) => UpdateReadouts();
        _uiTimer.Start();

        SetMode(AppMode.Gallery);
        if (SceneList.Items.Count > 0)
        {
            SceneList.SelectedIndex = FirstSelectableIndex();
        }
    }

    // ------------------------------------------------------------- catalogue

    private void BuildCatalogue()
    {
        string filter = FilterBox.Text?.Trim() ?? string.Empty;
        object? previous = (SceneList.SelectedItem as ListBoxItem)?.Tag;
        SceneList.Items.Clear();

        IEnumerable<DemoScene> matches = SceneCatalog.All.Where(scene =>
            filter.Length == 0
            || scene.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || scene.Category.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || scene.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || scene.Summary.Contains(filter, StringComparison.OrdinalIgnoreCase));

        foreach (IGrouping<string, DemoScene> group in matches.GroupBy(scene => scene.Category))
        {
            TextBlock header = new() { Text = group.Key.ToUpperInvariant(), Margin = new Thickness(0, 6, 0, 0) };
            header.Classes.Add("eyebrow");
            SceneList.Items.Add(new ListBoxItem
            {
                Content = header,
                IsHitTestVisible = false,
                Focusable = false,
                Padding = new Thickness(12, 8, 12, 2),
            });

            foreach (DemoScene scene in group)
            {
                StackPanel content = new() { Spacing = 1 };
                StackPanel line = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
                TextBlock code = new() { Text = scene.Id, VerticalAlignment = VerticalAlignment.Center };
                code.Classes.Add("code");
                TextBlock title = new() { Text = scene.Title };
                title.Classes.Add("heading");
                line.Children.Add(code);
                line.Children.Add(title);
                content.Children.Add(line);

                TextBlock summary = new() { Text = scene.Summary, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
                summary.Classes.Add("body");
                content.Children.Add(summary);

                SceneList.Items.Add(new ListBoxItem { Content = content, Tag = scene });
            }
        }

        if (previous is DemoScene selected)
        {
            foreach (object? item in SceneList.Items)
            {
                if (item is ListBoxItem row && ReferenceEquals(row.Tag, selected))
                {
                    SceneList.SelectedItem = row;
                    return;
                }
            }
        }
    }

    private int FirstSelectableIndex()
    {
        for (int i = 0; i < SceneList.Items.Count; i++)
        {
            if (SceneList.Items[i] is ListBoxItem { Tag: DemoScene })
            {
                return i;
            }
        }

        return -1;
    }

    private void OnSceneSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || SceneList.SelectedItem is not ListBoxItem { Tag: DemoScene scene })
        {
            return;
        }

        if (_mode == AppMode.Sandbox)
        {
            // Leaving the sandbox already loads whatever the catalogue has selected.
            SetMode(AppMode.Gallery);
        }

        if (!ReferenceEquals(_active, scene))
        {
            LoadScene(scene);
        }
    }

    // ---------------------------------------------------------- scene loading

    private void LoadScene(DemoScene scene, bool keepCamera = false)
    {
        _loading = true;
        try
        {
            if (_capabilities.MaxTextureSize > 0 && scene.UnsupportedReason(_capabilities) is { } reason)
            {
                Status($"{scene.Title} cannot run on this GPU: {reason}");
                return;
            }

            float yaw = _orbit?.Yaw ?? 0f;
            float pitch = _orbit?.Pitch ?? 0f;
            float distance = _orbit?.Distance ?? 0f;
            Vector3 target = _orbit?.Target ?? Vector3.Zero;

            _orbit?.Dispose();
            _orbit = null;
            _selection = null;
            Viewport.Scene = null;
            ViewportB.Scene = null;
            _active?.Unload();
            _scene?.Dispose();

            _scene = new Scene();
            _camera = _scene.AddCamera(Camera.Perspective(52f.ToRadians(), 0.08f, 900f), new Vector3(0f, 2f, 12f));
            _active = scene;
            scene.RenderRequest = change =>
            {
                change(_controls);
                _controls.Quality = QualityPreset.Custom;
                ApplyRenderOptions();
            };
            scene.Load(_scene, _world, _camera);

            RendererOptions requested = scene.ConfigureRenderer(RendererOptions.Default with
            {
                BgraOutput = true,
                VSync = false,
                MsaaSamples = 4,
            });
            _controls.CaptureFrom(requested);
            // A scene brings its own settings, so the quality preset goes back to
            // "scene" until the user picks one again.
            _controls.Quality = QualityPreset.Custom;
            _controls.ClampTo(_capabilities);
            _compareControls.CaptureFrom(requested);

            Viewport.RendererOptions = _controls.Apply(requested);
            Viewport.RenderScale = _controls.RenderScale;
            Viewport.MaxFramesPerSecond = _controls.FpsCap;
            Viewport.Scene = _scene;
            Viewport.Camera = _camera;

            _orbit = new OrbitController(Viewport, _camera) { AutoRotateSpeed = 0.18f };
            scene.ConfigureCamera(_orbit);
            if (keepCamera && distance > 0f)
            {
                _orbit.Yaw = yaw;
                _orbit.Pitch = pitch;
                _orbit.Distance = distance;
                _orbit.Target = target;
            }

            _orbit.AutoRotate = SpinToggle.IsChecked == true;
            _orbit.Apply();

            SceneCodeText.Text = scene.Id;
            SceneTitleText.Text = scene.Title;
            SceneSummaryText.Text = scene.Summary;
            _log.Clear();

            ApplyCompare();
            BuildPanel();
            UpdateWeatherText();
            Status($"{scene.Title} loaded: {_scene.NodeCount} nodes.");
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Rebuilds the current scene from scratch, keeping the camera.</summary>
    private void RebuildScene()
    {
        if (_active is { } scene)
        {
            LoadScene(scene, keepCamera: true);
        }
    }

    private void OnRendererCreated(object? sender, Renderer renderer)
    {
        _capabilities = renderer.Capabilities;
        AdapterText.Text = renderer.AdapterName;
        TimingText.Text = _capabilities.TimestampQueries ? "GPU + CPU" : "CPU only";
        TimingText.Foreground = Sheet.Brush(_capabilities.TimestampQueries ? "Cyan" : "Muted");
        _controls.ClampTo(_capabilities);
        if (_mode is AppMode.Benchmark or AppMode.Inspect)
        {
            BuildPanel();
        }
    }

    // ------------------------------------------------------------ frame loop

    private void OnFrame(object? sender, FrameEventArgs e)
    {
        float delta = Math.Clamp(e.DeltaSeconds, 1f / 1000f, 0.25f);
        _log.Add(delta * 1000f);

        if (_scene is null || _active is null)
        {
            return;
        }

        _active.Update(delta, e.TotalSeconds);

        if (_runner.IsRunning)
        {
            DriveBenchmark(delta, e.Renderer.Stats);
        }
    }

    private void DriveBenchmark(float delta, FrameStats stats)
    {
        if (_orbit is not null && _active is not null)
        {
            _active.ApplyBenchmarkCamera(_orbit, _runner.SceneElapsed);
        }

        bool finished = _runner.Frame(delta, stats);
        if (_runner.WantsSceneLoad && _runner.Current is { } next)
        {
            // Loading tears down the scene this frame is being drawn from, so it
            // waits until the frame is over.
            Dispatcher.UIThread.Post(() =>
            {
                if (_runner.WantsSceneLoad)
                {
                    LoadScene(next);
                    _runner.SceneLoaded();
                }
            });
            return;
        }

        if (finished)
        {
            FinishBenchmark();
        }
    }

    // -------------------------------------------------------------- read-out

    private void BuildHudRows()
    {
        foreach (string label in new[]
                 {
                     "frame", "cpu", "gpu", "draws", "triangles", "visible", "culled", "lights", "shadow", "internal",
                 })
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("62,*") };
            TextBlock caption = new() { Text = label };
            caption.Classes.Add("eyebrow");
            TextBlock value = new() { Text = "-" };
            value.Classes.Add("readout");
            Grid.SetColumn(value, 1);
            row.Children.Add(caption);
            row.Children.Add(value);
            HudRows.Children.Add(row);
            _hudRows.Add((label, value));
        }
    }

    private void UpdateReadouts()
    {
        FrameStats stats = Viewport.Stats;
        float fps = _log.Fps;
        FpsText.Text = fps <= 0f ? "--" : fps.ToString("F0");
        FpsText.Foreground = Sheet.Brush(fps >= 58f ? "Cyan" : fps >= 28f ? "Amber" : "Crimson");

        Renderer? renderer = Viewport.Renderer;
        Set("frame", $"{_log.LatestMs,6:F2} ms");
        Set("cpu", $"{stats.CpuTimeMs,6:F2} ms");
        Set("gpu", _capabilities.TimestampQueries ? $"{stats.GpuTimeMs,6:F2} ms" : "   n/a");
        Set("draws", $"{stats.DrawCalls,6:N0}");
        Set("triangles", $"{stats.Triangles,6:N0}");
        Set("visible", $"{stats.VisibleNodes,6:N0}");
        Set("culled", $"{stats.CulledNodes,6:N0}");
        Set("lights", $"{stats.Lights,6:N0}");
        Set("shadow", $"{stats.ShadowLayers,3} x {stats.ShadowDrawCalls,-6:N0}");
        Set("internal", renderer is null ? "-" : $"{renderer.Width} x {renderer.Height}");

        RibbonLowText.Text = _log.OnePercentLowFps <= 0f ? "1% low --" : $"1% low {_log.OnePercentLowFps,5:F0}";
        RibbonStutterText.Text = $"STUTTERS {_log.Stutters()}";
        RibbonStutterText.Foreground = Sheet.Brush(_log.Stutters() > 0 ? "Amber" : "Dim");
        Ribbon.Refresh();

        if (_runner.IsRunning)
        {
            UpdateBenchmarkProgress();
        }

        UpdateSceneMetrics();

        void Set(string label, string value)
        {
            foreach ((string name, TextBlock block) in _hudRows)
            {
                if (name == label)
                {
                    block.Text = value;
                    return;
                }
            }
        }
    }

    private void CycleHud()
    {
        _hudMode = _hudMode switch
        {
            HudMode.Detailed => HudMode.Compact,
            HudMode.Compact => HudMode.Hidden,
            _ => HudMode.Detailed,
        };

        HudPanel.IsVisible = _hudMode != HudMode.Hidden;
        HudRows.IsVisible = _hudMode == HudMode.Detailed;
        Status($"Read-out: {_hudMode.ToString().ToLowerInvariant()}.");
    }

    private void UpdateWeatherText() =>
        WeatherText.Text =
            $"{_world.Clock}  wind {_world.WindSpeed:F0} m/s\ncloud {_world.CloudCover:P0}  fog {_world.FogDensity:F3}";

    // --------------------------------------------------------------- compare

    private void ApplyCompare()
    {
        ViewportB.IsVisible = _compare;
        SeamGrid.IsVisible = _compare;
        if (!_compare)
        {
            ViewportB.IsRendering = false;
            ViewportB.Scene = null;
            return;
        }

        ViewportB.EnableInteraction = false;
        ViewportB.RendererOptions = _compareControls.Apply(RendererOptions.Default with
        {
            BgraOutput = true,
            VSync = false,
        });
        ViewportB.RenderScale = _controls.RenderScale;
        ViewportB.MaxFramesPerSecond = _controls.FpsCap;
        ViewportB.Scene = _scene;
        ViewportB.Camera = _camera;
        ViewportB.IsRendering = PauseToggle.IsChecked != true;
        UpdateCompareClip();
    }

    /// <summary>
    /// B is drawn over A and clipped to the right of the wipe, so both sides
    /// share one camera, one scene and one simulation step - the only difference
    /// is the settings.
    /// </summary>
    private void UpdateCompareClip()
    {
        Rect bounds = new(ViewportHost.Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        double x = bounds.Width * _wipe;
        ViewportB.Clip = new RectangleGeometry(new Rect(x, 0, Math.Max(0, bounds.Width - x), bounds.Height));
        if (SeamGrid.ColumnDefinitions.Count == 2)
        {
            SeamGrid.ColumnDefinitions[0].Width = new GridLength(Math.Max(0.0001, _wipe), GridUnitType.Star);
            SeamGrid.ColumnDefinitions[1].Width = new GridLength(Math.Max(0.0001, 1 - _wipe), GridUnitType.Star);
        }
    }

    // --------------------------------------------------------------- picking

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressValid || _scene is null || _camera is null || Viewport.Renderer is null)
        {
            return;
        }

        _pressValid = false;
        Point point = e.GetPosition(Viewport);
        if (Math.Abs(point.X - _pressed.X) > 4 || Math.Abs(point.Y - _pressed.Y) > 4)
        {
            return;   // that was a camera drag
        }

        float ndcX = (float)((point.X / Math.Max(1.0, Viewport.Bounds.Width) * 2.0) - 1.0);
        float ndcY = (float)(1.0 - (point.Y / Math.Max(1.0, Viewport.Bounds.Height) * 2.0));
        Ray ray = _scene.CreateCameraRay(_camera, ndcX, ndcY, Viewport.Renderer.AspectRatio);
        IReadOnlyList<RayHit> hits = _scene.Raycast(ray);
        _selection = hits.Count > 0 ? hits[0] : null;
        _active?.OnPick(_selection);

        if (_mode == AppMode.Sandbox && _active == _sandbox)
        {
            _sandbox.Select(_selection?.Node);
            BuildPanel();
            return;
        }

        if (_mode == AppMode.Inspect)
        {
            BuildPanel();
        }

        Status(_selection is { } hit
            ? $"Picked {hit.Node.Name} at {hit.Distance:F2} m."
            : "Nothing under the pointer.");
    }

    // --------------------------------------------------------------- capture

    private async Task CaptureAsync()
    {
        if (Viewport.CaptureFrame() is not WriteableBitmap bitmap || _active is null)
        {
            Status("Nothing to capture yet.");
            return;
        }

        using (bitmap)
        {
            FrameStats stats = Viewport.Stats;
            Dictionary<string, object?> metadata = new()
            {
                ["scene"] = new { id = _active.Id, title = _active.Title, preset = _active.ActivePreset },
                ["parameters"] = _active.Parameters.ToDictionary(p => p.Id, p => p.Display),
                ["environment"] = _world.Describe(),
                ["render"] = _controls.Describe(),
                ["adapter"] = new
                {
                    name = Viewport.Renderer?.AdapterName,
                    driver = Viewport.Renderer?.AdapterDriver,
                    backend = _capabilities.Backend.ToString(),
                    deviceType = _capabilities.DeviceType.ToString(),
                },
                ["frame"] = new
                {
                    fps = _log.Fps,
                    frameMs = _log.LatestMs,
                    cpuMs = stats.CpuTimeMs,
                    gpuMs = stats.GpuTimeMs,
                    drawCalls = stats.DrawCalls,
                    triangles = stats.Triangles,
                    lights = stats.Lights,
                },
                ["resolution"] = new { width = Viewport.Renderer?.Width, height = Viewport.Renderer?.Height },
                ["core"] = ThreeNetRuntime.NativeVersion,
                ["capturedAt"] = DateTimeOffset.Now,
            };

            try
            {
                string path = await Capture.SaveAsync(bitmap, _active.Id, metadata);
                Status($"Captured {Path.GetFileName(path)} with its settings beside it.");
            }
            catch (IOException exception)
            {
                Status($"Capture failed: {exception.Message}");
            }
        }
    }

    // ----------------------------------------------------------------- helpers

    private void ResetView()
    {
        if (_active is not null && _orbit is not null)
        {
            _active.ConfigureCamera(_orbit);
            _orbit.Apply();
            Status("Camera framed on the scene.");
        }
    }

    /// <summary>Pushes the current settings into the live renderer.</summary>
    private void ApplyRenderOptions()
    {
        if (Viewport.Renderer is { } renderer)
        {
            // Width, height and the BGRA readback belong to the view.
            renderer.Options = _controls.Apply(renderer.Options);
        }

        Viewport.RenderScale = _controls.RenderScale;
        Viewport.MaxFramesPerSecond = _controls.FpsCap;

        if (_compare && ViewportB.Renderer is { } other)
        {
            other.Options = _compareControls.Apply(other.Options);
            ViewportB.RenderScale = _controls.RenderScale;
            ViewportB.MaxFramesPerSecond = _controls.FpsCap;
        }

        bool debugging = _controls.DebugView != DebugView.Off || _controls.Wireframe;
        DebugBanner.IsVisible = debugging;
        DebugBannerText.Text = _controls.Wireframe && _controls.DebugView != DebugView.Off
            ? $"{Humanise(_controls.DebugView)} + WIREFRAME"
            : _controls.Wireframe ? "WIREFRAME" : Humanise(_controls.DebugView);
    }

    private static string Humanise(DebugView view) => view switch
    {
        DebugView.BaseColor => "BASE COLOUR",
        DebugView.WorldNormal => "WORLD NORMAL",
        DebugView.Lighting => "LIGHTING ONLY",
        DebugView.Shadow => "SHADOW MASK",
        DebugView.Uv => "TEXTURE COORDINATES",
        _ => view.ToString().ToUpperInvariant(),
    };

    private void Status(string message) => StatusText.Text = message;

    protected override void OnClosed(EventArgs e)
    {
        _uiTimer.Stop();
        _orbit?.Dispose();
        Viewport.Scene = null;
        ViewportB.Scene = null;
        _active?.Unload();
        _scene?.Dispose();
        _scene = null;
        base.OnClosed(e);
    }
}
