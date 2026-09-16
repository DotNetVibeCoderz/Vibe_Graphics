using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HomeComplexCad.Navigation;
using HomeComplexCad.World;
using ThreeNet;
using ThreeNet.Avalonia;
using Key = Avalonia.Input.Key;
using Location = HomeComplexCad.World.Location;

namespace HomeComplexCad.Views;

public partial class MainWindow : Window
{
    private readonly HashSet<Key> _keys = [];
    private ComplexWorld? _world;
    private PropertyInfo? _shown;
    private PropertyInfo? _pinned;
    private double _pinnedUntil;
    private double _lookTimer;
    private double _statsTimer;
    private int _frames;
    private double _time;
    private Point? _dragStart;
    private Point _lastPointer;
    private bool _dragged;
    private float _pendingYaw;
    private float _pendingPitch;
    private float _pendingZoom;
    private bool _updatingSlider;

    public MainWindow()
    {
        InitializeComponent();

        Viewport.RendererOptions = RendererOptions.Default with
        {
            BgraOutput = true,
            VSync = false,
            MsaaSamples = 4,
            ToneMapping = ToneMapping.Aces,
            Exposure = 1.0f,
            Bloom = true,
            BloomIntensity = 0.35f,
            BloomThreshold = 1.2f,
            Shadows = true,
            ShadowMapSize = 2048,
            ShadowCascades = 3,
            ShadowDistance = 70f,
            Ssao = true,
            SsaoRadius = 0.45f,
            SsaoIntensity = 1.5f,
            SsaoDirectStrength = 0.3f,
        };
        Viewport.RenderScale = 0.8;
        Viewport.MaxFramesPerSecond = 60;
        Viewport.IsRendering = false;

        FirstPersonButton.Click += (_, _) => SetMode(CameraMode.FirstPerson);
        ThirdPersonButton.Click += (_, _) => SetMode(CameraMode.ThirdPerson);
        FlyButton.Click += (_, _) => SetMode(CameraMode.Fly);

        HourSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty && !_updatingSlider)
            {
                _world?.SetHour((float)HourSlider.Value);
                UpdateClock();
            }
        };
        MorningButton.Click += (_, _) => SetHour(7f);
        NoonButton.Click += (_, _) => SetHour(12.5f);
        EveningButton.Click += (_, _) => SetHour(17.4f);
        NightButton.Click += (_, _) => SetHour(20.5f);
        QualityButton.IsCheckedChanged += (_, _) => ApplyQuality(QualityButton.IsChecked == true);
        VisitButton.Click += (_, _) => VisitShown();

        Viewport.PointerPressed += OnViewportPressed;
        Viewport.PointerMoved += OnViewportMoved;
        Viewport.PointerReleased += OnViewportReleased;
        Viewport.PointerWheelChanged += (_, e) => _pendingZoom += (float)e.Delta.Y;
        Viewport.Frame += OnFrame;

        Deactivated += (_, _) => _keys.Clear();
        Closed += (_, _) => _world?.Dispose();

        // Build after the window is on screen so the loading overlay is visible.
        Opened += (_, _) => Dispatcher.UIThread.Post(BuildWorld, DispatcherPriority.Background);
    }

    private void BuildWorld()
    {
        DateTime started = DateTime.Now;
        _world = new ComplexWorld();
        Viewport.Scene = _world.Scene;
        Viewport.Camera = _world.CameraNode;
        Viewport.IsRendering = true;
        LoadingOverlay.IsVisible = false;

        BuildLocationList();
        BuildSummary();
        ShowProperty(null);
        UpdateClock();
        StatusBar.Text = $"Kawasan siap dalam {(DateTime.Now - started).TotalSeconds:0.0} dtk · {_world.Properties.Count} bangunan & fasilitas · {_world.Locations.Count} lokasi";
        ApplyCommandLine();
        Viewport.Focus();
    }

    /// <summary>
    /// <c>--location "Ruang Tamu" --group "Dahlia" --hour 20 --mode fp</c>: start at a
    /// given place and time (handy for demos and screenshots).
    /// </summary>
    private void ApplyCommandLine()
    {
        if (_world is null)
        {
            return;
        }

        string[] args = Environment.GetCommandLineArgs();
        string? Value(string name)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        if (Value("--hour") is { } hourText && float.TryParse(hourText, NumberStyles.Float, CultureInfo.InvariantCulture, out float hour))
        {
            SetHour(hour);
        }

        if (Value("--location") is { } name)
        {
            string? group = Value("--group");
            Location? location = _world.Locations.FirstOrDefault(l =>
                l.Name.Contains(name, StringComparison.OrdinalIgnoreCase) &&
                (group is null || l.Group.Contains(group, StringComparison.OrdinalIgnoreCase)));
            if (location is not null)
            {
                GoTo(location);
            }
        }

        switch (Value("--mode"))
        {
            case "fp":
                SetMode(CameraMode.FirstPerson);
                break;
            case "tp":
                SetMode(CameraMode.ThirdPerson);
                break;
        }
    }

    // ------------------------------------------------------------ menus

    private void BuildLocationList()
    {
        if (_world is null)
        {
            return;
        }

        LocationList.Children.Clear();
        foreach (IGrouping<string, Location> group in _world.Locations.GroupBy(l => l.Group))
        {
            LocationList.Children.Add(new TextBlock
            {
                Text = group.Key.ToUpperInvariant(),
                Classes = { "section" },
                Margin = new Thickness(8, 14, 0, 4),
            });

            foreach (Location location in group)
            {
                Button button = new()
                {
                    Classes = { "location" },
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock { Text = location.Icon, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                            new TextBlock { Text = location.Name, VerticalAlignment = VerticalAlignment.Center },
                        },
                    },
                };
                button.Click += (_, _) => GoTo(location);
                LocationList.Children.Add(button);
            }
        }
    }

    private void BuildSummary()
    {
        if (_world is null)
        {
            return;
        }

        List<PropertyInfo> homes = _world.Properties.Where(p => p.Id.StartsWith("house", StringComparison.Ordinal)).ToList();
        SummaryPanel.Children.Clear();
        AddSummary("Total unit rumah", homes.Count.ToString(CultureInfo.InvariantCulture));
        AddSummary("Tersedia", homes.Count(h => h.Status == PropertyStatus.Available).ToString(CultureInfo.InvariantCulture));
        AddSummary("Dipesan", homes.Count(h => h.Status == PropertyStatus.Reserved).ToString(CultureInfo.InvariantCulture));
        AddSummary("Terjual", homes.Count(h => h.Status == PropertyStatus.Sold).ToString(CultureInfo.InvariantCulture));
        AddSummary("Luas kawasan", "± 2,4 ha");
        AddSummary("Fasilitas", "Clubhouse, kolam renang, taman, lapangan, ruko, keamanan 24 jam");
    }

    private void AddSummary(string label, string value)
    {
        SummaryPanel.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                new TextBlock { Text = label, Classes = { "muted" } },
                new TextBlock { Text = value, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 170, TextAlignment = TextAlignment.Right, [Grid.ColumnProperty] = 1 },
            },
        });
    }

    private void GoTo(Location location)
    {
        if (_world is null)
        {
            return;
        }

        _world.Rig.GoTo(location.Eye, location.Target, location.Interior);
        if (location.PropertyId is { } id && _world.Properties.FirstOrDefault(p => p.Id == id) is { } info)
        {
            Pin(info);
        }

        StatusBar.Text = $"Menuju: {location.Group} · {location.Name}";
        Viewport.Focus();
    }

    private void VisitShown()
    {
        if (_world is null || _shown is null)
        {
            return;
        }

        Location? location = _world.Locations.FirstOrDefault(l => l.PropertyId == _shown.Id && !l.Interior)
            ?? _world.Locations.FirstOrDefault(l => l.PropertyId == _shown.Id);
        if (location is not null)
        {
            GoTo(location);
        }
    }

    private void SetMode(CameraMode mode)
    {
        _world?.Rig.SetMode(mode);
        SyncModeButtons();
        Viewport.Focus();
    }

    private void SyncModeButtons()
    {
        CameraMode mode = _world?.Rig.Mode ?? CameraMode.Fly;
        FirstPersonButton.IsChecked = mode == CameraMode.FirstPerson;
        ThirdPersonButton.IsChecked = mode == CameraMode.ThirdPerson;
        FlyButton.IsChecked = mode == CameraMode.Fly;
        Crosshair.IsVisible = mode != CameraMode.ThirdPerson;
        HintText.Text = mode switch
        {
            CameraMode.FirstPerson => "WASD berjalan · Shift lari · seret mouse kanan untuk melihat · naik tangga dengan berjalan",
            CameraMode.ThirdPerson => "WASD berjalan · Shift lari · seret mouse untuk memutar kamera · scroll untuk zoom",
            _ => "WASD terbang · Q/E naik-turun · Shift cepat · seret mouse kanan untuk melihat · klik bangunan untuk info",
        };
    }

    private void SetHour(float hour)
    {
        PlayTimeButton.IsChecked = false;
        _world?.SetHour(hour);
        UpdateClock();
    }

    private void UpdateClock()
    {
        if (_world is null)
        {
            return;
        }

        float hour = _world.Hour;
        int h = (int)hour;
        int m = (int)((hour - h) * 60);
        HourText.Text = $"{h:00}:{m:00}";
        SunIcon.Text = hour switch
        {
            < 5.5f or > 19f => "🌙",
            < 8f => "🌅",
            > 16.8f => "🌇",
            _ => "☀",
        };
        _updatingSlider = true;
        HourSlider.Value = hour;
        _updatingSlider = false;
    }

    private void ApplyQuality(bool high)
    {
        Viewport.RendererOptions = Viewport.RendererOptions with
        {
            Shadows = true,
            ShadowMapSize = high ? 2048 : 1024,
            ShadowCascades = high ? 3 : 2,
            Ssao = high,
            Bloom = high,
            MsaaSamples = high ? 4 : 1,
        };
        Viewport.RenderScale = high ? 0.8 : 0.6;
        QualityButton.Content = high ? "✨ HQ" : "⚡ Cepat";
    }

    // ------------------------------------------------------------ frame

    private void OnFrame(object? sender, FrameEventArgs e)
    {
        if (_world is null)
        {
            return;
        }

        float dt = MathF.Min(e.DeltaSeconds, 0.05f);
        _time += dt;

        if (PlayTimeButton.IsChecked == true)
        {
            // One simulated hour every four seconds.
            _world.SetHour(_world.Hour + (dt / 4f));
            UpdateClock();
        }

        NavigationInput input = new()
        {
            Forward = Axis(Key.W, Key.S) + Axis(Key.Up, Key.Down),
            Right = Axis(Key.D, Key.A),
            Up = Axis(Key.E, Key.Q),
            Sprint = _keys.Contains(Key.LeftShift) || _keys.Contains(Key.RightShift),
            LookYaw = _pendingYaw + (Axis(Key.Right, Key.Left) * 1.8f * dt),
            LookPitch = _pendingPitch,
            Zoom = _pendingZoom,
        };
        _pendingYaw = 0f;
        _pendingPitch = 0f;
        _pendingZoom = 0f;

        CameraMode before = _world.Rig.Mode;
        _world.Rig.Update(dt, input);
        if (before != _world.Rig.Mode)
        {
            SyncModeButtons();
        }

        // What is the viewer looking at? Checked a few times a second.
        _lookTimer -= dt;
        if (_lookTimer <= 0 && !_world.Rig.InTransition)
        {
            _lookTimer = 0.25;
            PropertyInfo? looked = _world.PropertyInView();
            LookBadge.IsVisible = looked is not null;
            if (looked is not null)
            {
                LookText.Text = $"👁 {looked.Name} · {looked.Type}";
            }

            if (_time > _pinnedUntil && looked is not null && looked != _shown)
            {
                ShowProperty(looked);
            }
        }

        _frames++;
        _statsTimer += dt;
        if (_statsTimer >= 1)
        {
            FrameStats stats = Viewport.Stats;
            StatusBar.Text = $"{_frames / _statsTimer:0} fps · {stats.DrawCalls} draw calls · {stats.Triangles / 1000} k segitiga · {stats.Lights} lampu aktif · mode {ModeName(_world.Rig.Mode)}";
            _frames = 0;
            _statsTimer = 0;
        }
    }

    private static string ModeName(CameraMode mode) => mode switch
    {
        CameraMode.FirstPerson => "orang pertama",
        CameraMode.ThirdPerson => "orang ketiga",
        _ => "terbang",
    };

    private float Axis(Key positive, Key negative) =>
        (_keys.Contains(positive) ? 1f : 0f) - (_keys.Contains(negative) ? 1f : 0f);

    // ------------------------------------------------------------ info panel

    private void Pin(PropertyInfo info)
    {
        _pinned = info;
        _pinnedUntil = _time + 6;
        ShowProperty(info);
    }

    private void ShowProperty(PropertyInfo? info)
    {
        _shown = info;
        SpecGrid.Children.Clear();
        SpecGrid.RowDefinitions.Clear();
        BuildGrid.Children.Clear();
        BuildGrid.RowDefinitions.Clear();
        FeatureList.Children.Clear();

        if (info is null)
        {
            InfoKind.Text = "Arahkan kamera ke bangunan";
            InfoName.Text = "Informasi Bangunan";
            InfoPrice.Text = "";
            StatusText.Text = "-";
            StatusChip.Background = new SolidColorBrush(Color.Parse("#2A3340"));
            InfoDescription.Text = "Informasi tanah dan bangunan akan muncul untuk setiap rumah, ruko atau fasilitas yang sedang Anda lihat atau klik.";
            VisitButton.IsVisible = false;
            return;
        }

        InfoKind.Text = string.IsNullOrEmpty(info.Block) ? info.Type : $"Blok {info.Block} · {info.Type}";
        InfoName.Text = info.Name;
        InfoPrice.Text = info.Status == PropertyStatus.Facility ? "" : info.PriceText;
        StatusText.Text = info.StatusText;
        StatusChip.Background = new SolidColorBrush(Color.Parse(info.Status switch
        {
            PropertyStatus.Available => "#1F6F4A",
            PropertyStatus.Reserved => "#8A6414",
            PropertyStatus.Sold => "#7A2E2E",
            _ => "#284A73",
        }));
        InfoDescription.Text = info.Description;
        VisitButton.IsVisible = true;

        void Row(Grid grid, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.Children.Add(new TextBlock { Text = label, Classes = { "muted" }, Margin = new Thickness(0, 3, 14, 3), [Grid.RowProperty] = row });
            grid.Children.Add(new TextBlock { Text = value, Classes = { "value" }, Margin = new Thickness(0, 3), [Grid.RowProperty] = row, [Grid.ColumnProperty] = 1 });
        }

        Row(SpecGrid, "Luas tanah", info.LandArea > 0 ? $"{info.LandArea:0} m²" : "");
        Row(SpecGrid, "Luas bangunan", info.BuildingArea > 0 ? $"{info.BuildingArea:0} m²" : "");
        Row(SpecGrid, "Ukuran kavling", info.LotSize != default ? $"{info.LotSize.X:0.#} × {info.LotSize.Y:0.#} m" : "");
        Row(SpecGrid, "Jumlah lantai", info.BuildingArea > 0 ? info.Floors.ToString(CultureInfo.InvariantCulture) : "");
        Row(SpecGrid, "Kamar tidur", info.Bedrooms > 0 ? info.Bedrooms.ToString(CultureInfo.InvariantCulture) : "");
        Row(SpecGrid, "Kamar mandi", info.Bathrooms > 0 ? info.Bathrooms.ToString(CultureInfo.InvariantCulture) : "");
        Row(SpecGrid, "Carport", info.Carports > 0 ? $"{info.Carports} mobil" : "");
        Row(SpecGrid, "Sertifikat", info.Status == PropertyStatus.Facility ? "Fasos / fasum" : info.Certificate);
        Row(SpecGrid, "Hadap", info.Status == PropertyStatus.Facility ? "" : info.Facing);
        Row(SpecGrid, "Listrik", $"{info.PowerVa:N0} VA");
        Row(SpecGrid, "Air bersih", info.Water);
        Row(SpecGrid, "Tahun", info.YearBuilt.ToString(CultureInfo.InvariantCulture));

        Row(BuildGrid, "Struktur", info.Structure);
        Row(BuildGrid, "Atap", info.Roof);
        Row(BuildGrid, "Lantai", info.Flooring);

        foreach (string feature in info.Features)
        {
            FeatureList.Children.Add(new TextBlock { Text = $"✓  {feature}", Classes = { "value" } });
        }
    }

    // ------------------------------------------------------------ input

    private void OnViewportPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStart = e.GetPosition(Viewport);
        _lastPointer = _dragStart.Value;
        _dragged = false;
        Viewport.Focus();
    }

    private void OnViewportMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is null)
        {
            return;
        }

        Point position = e.GetPosition(Viewport);
        Vector delta = position - _lastPointer;
        _lastPointer = position;
        if (Math.Abs(position.X - _dragStart.Value.X) + Math.Abs(position.Y - _dragStart.Value.Y) > 4)
        {
            _dragged = true;
        }

        _pendingYaw += (float)delta.X * 0.0045f;
        _pendingPitch += (float)delta.Y * 0.0045f;
    }

    private void OnViewportReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragStart is { } start && !_dragged && _world is not null && e.InitialPressMouseButton == Avalonia.Input.MouseButton.Left)
        {
            // A click (not a drag) picks the building under the cursor.
            float ndcX = (float)((start.X / Math.Max(1, Viewport.Bounds.Width) * 2) - 1);
            float ndcY = (float)(1 - (start.Y / Math.Max(1, Viewport.Bounds.Height) * 2));
            float aspect = (float)(Viewport.Bounds.Width / Math.Max(1, Viewport.Bounds.Height));
            if (_world.Pick(ndcX, ndcY, aspect) is { } info)
            {
                Pin(info);
                StatusBar.Text = $"Dipilih: {info.Name}";
            }
        }

        _dragStart = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Text boxes do not exist in this window, so every key drives the viewer.
        _keys.Add(e.Key);
        switch (e.Key)
        {
            case Key.D1:
                SetMode(CameraMode.FirstPerson);
                break;
            case Key.D2:
                SetMode(CameraMode.ThirdPerson);
                break;
            case Key.D3:
                SetMode(CameraMode.Fly);
                break;
            case Key.N:
                SetHour(_world is { IsNight: true } ? 10f : 20.5f);
                break;
        }

        e.Handled = e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Space;
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        _keys.Remove(e.Key);
        base.OnKeyUp(e);
    }
}
