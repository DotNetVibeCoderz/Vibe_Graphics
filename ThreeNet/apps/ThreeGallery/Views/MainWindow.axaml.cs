using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaEdit.Highlighting;
using ThreeGallery.Samples;
using ThreeNet;
using ThreeNet.Avalonia;

namespace ThreeGallery.Views;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<object> _items = [];
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch _fpsClock = Stopwatch.StartNew();

    private GallerySample? _sample;
    private Scene? _scene;
    private Node? _camera;
    private OrbitController? _orbit;
    private int _framesSinceTick;
    private double _fps;

    public MainWindow()
    {
        InitializeComponent();

        CodeEditor.SyntaxHighlighting = DarkHighlighting.Get("C#");
        SampleList.ItemsSource = _items;
        SearchBox.TextChanged += (_, _) => RebuildList();

        Viewport.Frame += OnViewportFrame;
        Viewport.RendererCreated += (_, renderer) => AdapterText.Text = $"GPU: {renderer.AdapterName}";
        Viewport.RenderFailed += (_, message) => StatusText.Text = $"Renderer error: {message}";
        Viewport.PointerPressed += OnViewportPointerPressed;

        PauseToggle.IsCheckedChanged += (_, _) => Viewport.IsRendering = PauseToggle.IsChecked != true;
        AutoRotateToggle.IsCheckedChanged += (_, _) =>
        {
            if (_orbit is not null)
            {
                _orbit.AutoRotate = AutoRotateToggle.IsChecked == true;
            }
        };

        _statsTimer.Tick += (_, _) => UpdateStats();
        _statsTimer.Start();

        RebuildList();
        if (_items.OfType<GallerySample>().FirstOrDefault() is { } first)
        {
            SampleList.SelectedItem = first;
        }
    }

    /// <summary>Rebuilds the sidebar, grouping by category and applying the search filter.</summary>
    private void RebuildList()
    {
        string query = SearchBox.Text?.Trim() ?? string.Empty;
        object? previous = SampleList.SelectedItem;

        _items.Clear();
        IEnumerable<GallerySample> matches = SampleCatalog.All.Where(sample =>
            query.Length == 0 ||
            sample.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            sample.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            sample.Summary.Contains(query, StringComparison.OrdinalIgnoreCase));

        foreach (IGrouping<string, GallerySample> group in matches.GroupBy(sample => sample.Category))
        {
            _items.Add(group.Key.ToUpperInvariant());
            foreach (GallerySample sample in group)
            {
                _items.Add(sample);
            }
        }

        if (previous is GallerySample sample2 && _items.Contains(sample2))
        {
            SampleList.SelectedItem = sample2;
        }
    }

    private void OnSampleSelected(object? sender, SelectionChangedEventArgs e)
    {
        // Category rows are plain strings; skip forward to the next real sample.
        if (SampleList.SelectedItem is string)
        {
            int index = SampleList.SelectedIndex + 1;
            SampleList.SelectedIndex = index < _items.Count ? index : -1;
            return;
        }

        if (SampleList.SelectedItem is not GallerySample sample || ReferenceEquals(sample, _sample))
        {
            return;
        }

        LoadSample(sample);
    }

    private void LoadSample(GallerySample sample)
    {
        _sample = sample;

        _orbit?.Dispose();
        _orbit = null;
        Viewport.Scene = null;
        _scene?.Dispose();

        _scene = new Scene();
        sample.Build(_scene);

        _camera = _scene.AddCamera(Camera.Perspective(50f.ToRadians(), 0.05f, 500f), new Vector3(0f, 2f, 8f));
        Viewport.RendererOptions = sample.ConfigureRenderer(RendererOptions.Default with
        {
            MsaaSamples = 4,
            VSync = false,
            BgraOutput = true,
        });
        Viewport.Scene = _scene;
        Viewport.Camera = _camera;

        _orbit = new OrbitController(Viewport, _camera)
        {
            AutoRotate = AutoRotateToggle.IsChecked == true,
            AutoRotateSpeed = 0.25f,
        };
        sample.ConfigureCamera(_orbit);
        _orbit.Apply();

        SampleTitleText.Text = sample.Title;
        SampleSummaryText.Text = sample.Summary;
        CodeFileText.Text = $"{sample.GetType().Name}.cs";
        CodeEditor.Text = sample.SourceCode;
        StatusText.Text = $"Loaded '{sample.Title}' - {_scene.NodeCount} nodes";
    }

    private void OnViewportFrame(object? sender, FrameEventArgs e)
    {
        _framesSinceTick++;
        if (_scene is not null)
        {
            _sample?.Update(_scene, e.DeltaSeconds, e.TotalSeconds);
        }
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_scene is null || _sample is null || _camera is null || Viewport.Renderer is null)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(Viewport);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Avalonia gives logical coordinates; the ray needs normalised device ones.
        float ndcX = (float)((point.Position.X / Viewport.Bounds.Width * 2.0) - 1.0);
        float ndcY = (float)(1.0 - (point.Position.Y / Viewport.Bounds.Height * 2.0));
        Ray ray = _scene.CreateCameraRay(_camera, ndcX, ndcY, Viewport.Renderer.AspectRatio);
        _sample.OnPick(_scene, ray);
    }

    private void UpdateStats()
    {
        double seconds = _fpsClock.Elapsed.TotalSeconds;
        if (seconds > 0.2)
        {
            _fps = _framesSinceTick / seconds;
            _framesSinceTick = 0;
            _fpsClock.Restart();
        }

        FrameStats stats = Viewport.Stats;
        FpsText.Text = $"{_fps,5:F1} fps";
        DrawText.Text = $"draws {stats.DrawCalls}";
        TriangleText.Text = $"tris  {stats.Triangles:N0}";
        CullText.Text = $"culled {stats.CulledNodes}";
    }

    private void OnResetView(object? sender, RoutedEventArgs e)
    {
        if (_sample is null || _orbit is null)
        {
            return;
        }

        _sample.ConfigureCamera(_orbit);
        _orbit.Apply();
    }

    private async void OnScreenshot(object? sender, RoutedEventArgs e)
    {
        if (Viewport.CaptureFrame() is not WriteableBitmap bitmap)
        {
            return;
        }

        using (bitmap)
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "ThreeNet");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{_sample?.GetType().Name ?? "frame"}-{DateTime.Now:yyyyMMdd-HHmmss}.png");

            await Task.Run(() => bitmap.Save(path));
            StatusText.Text = $"Screenshot saved to {path}";
        }
    }

    private async void OnCopyCode(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard && _sample is not null)
        {
            await clipboard.SetTextAsync(_sample.SourceCode);
            StatusText.Text = "Sample source copied to the clipboard";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _statsTimer.Stop();
        _orbit?.Dispose();
        Viewport.Scene = null;
        _scene?.Dispose();
        _scene = null;
        base.OnClosed(e);
    }
}

