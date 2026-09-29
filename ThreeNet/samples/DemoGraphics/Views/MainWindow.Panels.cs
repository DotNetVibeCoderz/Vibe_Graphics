using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using DemoGraphics.Diagnostics;
using DemoGraphics.Framework;
using DemoGraphics.Scenes;
using ThreeNet;

namespace DemoGraphics.Views;

/// <summary>
/// The control column. Each bench builds its own sheet from the same pieces, so
/// a parameter looks and behaves the same whether it belongs to a scene, to the
/// renderer or to the weather.
/// </summary>
public partial class MainWindow
{
    private readonly List<(string Label, TextBlock Value)> _metricRows = [];
    private string _sceneSet = "Every scene";
    private float _secondsPerScene = 6f;
    private ProgressBar? _benchmarkBar;
    private TextBlock? _benchmarkStatus;

    private static readonly (string Name, QualityPreset Quality, int Height)[] Profiles =
    [
        ("Native, scene settings", QualityPreset.Custom, 0),
        ("720p Low", QualityPreset.Low, 720),
        ("1080p Medium", QualityPreset.Medium, 1080),
        ("1080p High", QualityPreset.High, 1080),
        ("1440p Ultra", QualityPreset.Ultra, 1440),
        ("Native Cinematic", QualityPreset.Cinematic, 0),
    ];

    private void SetMode(AppMode mode)
    {
        _mode = mode;
        GalleryTab.IsChecked = mode == AppMode.Gallery;
        LabTab.IsChecked = mode == AppMode.Laboratory;
        BenchTab.IsChecked = mode == AppMode.Benchmark;
        InspectTab.IsChecked = mode == AppMode.Inspect;
        SandboxTab.IsChecked = mode == AppMode.Sandbox;

        (PanelTitleText.Text, PanelSubtitleText.Text) = mode switch
        {
            AppMode.Laboratory => ("Laboratory", "SCENE AND RENDERER PARAMETERS"),
            AppMode.Benchmark => ("Benchmark", "FIXED CAMERA PATHS, EXPORTABLE RESULTS"),
            AppMode.Inspect => ("Inspect", "DEBUG VIEWS, PIPELINE AND PICKING"),
            AppMode.Sandbox => ("Sandbox", "SPAWN, DRAG, DROP, SAVE"),
            _ => ("Gallery", "PRESETS AND SHARED WEATHER"),
        };

        HintText.Text = mode switch
        {
            AppMode.Sandbox => "LEFT DRAG MOVES THE SELECTION · RIGHT DRAG PANS · CLICK TO SELECT",
            AppMode.Inspect => "CLICK ANY SURFACE TO INSPECT IT · WHEEL ZOOM",
            _ => "LEFT DRAG ORBIT · RIGHT DRAG PAN · WHEEL ZOOM · CLICK TO INSPECT",
        };

        if (mode == AppMode.Sandbox)
        {
            if (_active != _sandbox)
            {
                LoadScene(_sandbox);
                if (Viewport.Interaction is { } interaction)
                {
                    _sandbox.EnableDragging(interaction);
                }
            }
        }
        else if (_active == _sandbox && SceneList.SelectedItem is ListBoxItem { Tag: DemoScene scene })
        {
            LoadScene(scene);
        }

        BuildPanel();
    }

    private void BuildPanel()
    {
        ControlColumn.Children.Clear();
        _metricRows.Clear();
        _benchmarkBar = null;
        _benchmarkStatus = null;

        switch (_mode)
        {
            case AppMode.Laboratory:
                BuildLaboratory();
                break;
            case AppMode.Benchmark:
                BuildBenchmark();
                break;
            case AppMode.Inspect:
                BuildInspect();
                break;
            case AppMode.Sandbox:
                BuildSandbox();
                break;
            default:
                BuildGallery();
                break;
        }
    }

    private void Add(Control control) => ControlColumn.Children.Add(control);

    // ---------------------------------------------------------------- gallery

    private void BuildGallery()
    {
        if (_active is not { } scene)
        {
            Add(Sheet.Paragraph("Pick a scene from the catalogue."));
            return;
        }

        Add(Sheet.Section("What this shows"));
        Add(Sheet.Paragraph(scene.Summary));
        if (scene.Features.Count > 0)
        {
            WrapPanel features = new() { Margin = new Thickness(0, 8, 0, 0) };
            foreach (string feature in scene.Features)
            {
                Border chip = new()
                {
                    Padding = new Thickness(7, 2, 7, 3),
                    Margin = new Thickness(0, 0, 5, 5),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Sheet.Brush("Rule"),
                    CornerRadius = new CornerRadius(2),
                    Child = new TextBlock { Text = feature, FontSize = 11, Foreground = Sheet.Brush("Muted") },
                };
                features.Children.Add(chip);
            }

            Add(features);
        }

        AddPresets(scene);
        AddActions(scene);
        AddMetrics(scene);
        AddWeather();
        AddQuality();
        AddCompare();
    }

    private void AddPresets(DemoScene scene)
    {
        if (scene.Presets.Count == 0)
        {
            return;
        }

        Add(Sheet.Section("Presets", scene.ActivePreset ?? "custom"));
        WrapPanel chips = new();
        foreach (DemoPreset preset in scene.Presets)
        {
            ToggleButton chip = new()
            {
                Content = preset.Name,
                Margin = new Thickness(0, 0, 5, 5),
                IsChecked = scene.ActivePreset == preset.Name,
            };
            chip.Classes.Add("preset");
            chip.Click += (_, _) =>
            {
                bool rebuild = scene.ApplyPreset(preset);
                if (rebuild)
                {
                    RebuildScene();
                }
                else
                {
                    BuildPanel();
                }

                UpdateWeatherText();
                Status($"Preset applied: {preset.Name}.");
            };
            chips.Children.Add(chip);
        }

        Add(chips);
    }

    private void AddActions(DemoScene scene)
    {
        if (scene.Actions.Count == 0)
        {
            return;
        }

        Add(Sheet.Section("Do"));
        WrapPanel panel = new();
        foreach ((string label, Action run) in scene.Actions)
        {
            Button button = new() { Content = label, Margin = new Thickness(0, 0, 5, 5) };
            button.Click += (_, _) =>
            {
                run();
                Status(label + ".");
            };
            panel.Children.Add(button);
        }

        Add(panel);
    }

    private void AddMetrics(DemoScene scene)
    {
        List<(string Label, string Value)> metrics = scene.Metrics().ToList();
        if (metrics.Count == 0)
        {
            return;
        }

        Add(Sheet.Section("Scene read-out"));
        foreach ((string label, string value) in metrics)
        {
            Add(Sheet.LiveFact(label, value, out TextBlock block));
            _metricRows.Add((label, block));
        }
    }

    private void UpdateSceneMetrics()
    {
        if (_active is null || _metricRows.Count == 0)
        {
            return;
        }

        List<(string Label, string Value)> metrics = _active.Metrics().ToList();
        for (int i = 0; i < _metricRows.Count && i < metrics.Count; i++)
        {
            _metricRows[i].Value.Text = metrics[i].Value;
        }
    }

    private void AddWeather()
    {
        Add(Sheet.Section("Shared weather", _world.Clock));
        WrapPanel chips = new();
        foreach (string name in EnvironmentState.WeatherNames)
        {
            Button chip = new() { Content = name, Margin = new Thickness(0, 0, 5, 5) };
            chip.Click += (_, _) =>
            {
                _world.ApplyWeather(name);
                BuildPanel();
                Status($"Weather set to {name.ToLowerInvariant()}.");
            };
            chips.Children.Add(chip);
        }

        Add(chips);

        Add(Knob("Time of day", "h", _world.Hour, 0, 24, 0.05, value => _world.Hour = (float)value, format: _ => _world.Clock));
        Add(Knob("Sun azimuth", "deg", _world.SunAzimuth, 0, 360, 1, value => _world.SunAzimuth = (float)value));
        Add(Knob("Wind speed", "m/s", _world.WindSpeed, 0, 30, 0.5, value => _world.WindSpeed = (float)value));
        Add(Knob("Wind direction", "deg", _world.WindDirection, 0, 360, 1, value => _world.WindDirection = (float)value));
        Add(Knob("Cloud cover", "%", _world.CloudCover * 100, 0, 100, 1, value => _world.CloudCover = (float)value / 100f));
        Add(Knob("Fog density", "", _world.FogDensity, 0, 0.12, 0.001, value => _world.FogDensity = (float)value, digits: 3));
        Add(Knob("Wetness", "%", _world.Wetness * 100, 0, 100, 1, value => _world.Wetness = (float)value / 100f));
        Add(Knob("Snow cover", "%", _world.SnowAmount * 100, 0, 100, 1, value => _world.SnowAmount = (float)value / 100f));
    }

    // ------------------------------------------------------------ laboratory

    private void BuildLaboratory()
    {
        if (_active is not { } scene)
        {
            Add(Sheet.Paragraph("Pick a scene from the catalogue."));
            return;
        }

        Add(Sheet.Section("Scene parameters", scene.Id));
        if (scene.Parameters.Count == 0)
        {
            Add(Sheet.Paragraph("This scene has no parameters of its own."));
        }

        foreach (DemoParameter parameter in scene.Parameters)
        {
            Add(Sheet.Parameter(parameter, changed =>
            {
                bool rebuild = scene.SetParameter(changed.Id, changed.Value);
                if (rebuild)
                {
                    // Some parameters change the mesh, so the scene is rebuilt.
                    RebuildScene();
                }
            }));
        }

        Add(Sheet.Buttons(("Reset parameters", () =>
        {
            scene.Reset();
            RebuildScene();
            Status("Parameters back to the scene defaults.");
        })));

        AddPresets(scene);
        AddActions(scene);
        AddRenderSheet();
        AddWeather();
        AddCompare();
    }

    private void AddQuality()
    {
        Add(Sheet.Section("Quality", _controls.Quality == QualityPreset.Custom ? "scene" : _controls.Quality.ToString().ToLowerInvariant()));
        WrapPanel chips = new();
        foreach (QualityPreset preset in new[]
                 {
                     QualityPreset.Low, QualityPreset.Medium, QualityPreset.High, QualityPreset.Ultra, QualityPreset.Cinematic,
                 })
        {
            Button chip = new() { Content = preset.ToString(), Margin = new Thickness(0, 0, 5, 5) };
            chip.Click += (_, _) =>
            {
                _controls.LoadQuality(preset);
                _controls.ClampTo(_capabilities);
                ApplyRenderOptions();
                BuildPanel();
                Status($"Quality: {preset.ToString().ToLowerInvariant()}. It holds until the next scene loads.");
            };
            chips.Children.Add(chip);
        }

        Add(chips);
        Add(Sheet.Note("A scene brings its own settings when it loads; a preset overrides them until then."));
    }

    private void AddRenderSheet()
    {
        Add(Sheet.Section("Renderer", _controls.Quality == QualityPreset.Custom ? "scene" : _controls.Quality.ToString().ToLowerInvariant()));
        AddQualityChips();

        Add(Choice("Render path", ["Forward", "Deferred"], (int)_controls.RenderPath, index =>
        {
            _controls.RenderPath = (RenderPath)index;
            Custom();
        }));
        Add(Sheet.Note("Deferred lights every pixel once, which pays off past a few dozen lights. MSAA does not apply to it."));

        string[] msaaOptions = ["Off", "2x", "4x", "8x"];
        int msaaIndex = _controls.MsaaSamples switch { 8 => 3, 4 => 2, 2 => 1, _ => 0 };
        Add(Choice("Anti-aliasing", msaaOptions, msaaIndex, index =>
        {
            _controls.MsaaSamples = index switch { 3 => 8, 2 => 4, 1 => 2, _ => 1 };
            _controls.ClampTo(_capabilities);
            Custom();
        }));
        if (_capabilities.MaxMsaaSamples is > 0 and < 8)
        {
            Add(Sheet.Note($"This GPU resolves at most {_capabilities.MaxMsaaSamples}x on the HDR target."));
        }

        Add(Knob("Render scale", "x", _controls.RenderScale, 0.25, 2.0, 0.05, value =>
        {
            _controls.RenderScale = value;
            ApplyRenderOptions();
        }, digits: 2));
        Add(Knob("Frame cap", "fps", _controls.FpsCap, 30, 240, 1, value =>
        {
            _controls.FpsCap = (int)value;
            ApplyRenderOptions();
        }));
        Add(Switch("Wait for vsync", _controls.VSync, value =>
        {
            _controls.VSync = value;
            Custom();
        }));
        Add(Switch("Frustum culling", _controls.FrustumCulling, value =>
        {
            _controls.FrustumCulling = value;
            Custom();
        }));

        Add(Sheet.Section("Tone mapping"));
        Add(Choice("Curve", ["None", "Reinhard", "ACES", "Filmic"], (int)_controls.ToneMapping, index =>
        {
            _controls.ToneMapping = (ToneMapping)index;
            Custom();
        }));
        Add(Knob("Exposure", "", _controls.Exposure, 0.1, 4, 0.05, value =>
        {
            _controls.Exposure = (float)value;
            Custom();
        }, digits: 2));

        Add(Sheet.Section("Shadows"));
        Add(Switch("Shadow maps", _controls.Shadows, value =>
        {
            _controls.Shadows = value;
            Custom();
        }));
        Add(Choice("Resolution", ["512", "1024", "2048", "4096"], _controls.ShadowMapSize switch
        {
            4096 => 3, 2048 => 2, 1024 => 1, _ => 0,
        }, index =>
        {
            _controls.ShadowMapSize = index switch { 3 => 4096, 2 => 2048, 1 => 1024, _ => 512 };
            Custom();
        }));
        Add(Knob("Cascades", "", _controls.ShadowCascades, 1, 4, 1, value =>
        {
            _controls.ShadowCascades = (int)value;
            Custom();
        }, digits: 0));
        Add(Knob("Softness", "PCF", _controls.ShadowSoftness, 0, 3, 1, value =>
        {
            _controls.ShadowSoftness = (int)value;
            Custom();
        }, digits: 0));
        Add(Knob("Distance", "m", _controls.ShadowDistance, 10, 300, 5, value =>
        {
            _controls.ShadowDistance = (float)value;
            Custom();
        }, digits: 0));

        Add(Sheet.Section("Ambient occlusion"));
        Add(Switch("SSAO", _controls.Ssao, value =>
        {
            _controls.Ssao = value;
            Custom();
        }));
        Add(Knob("Samples", "", _controls.SsaoSamples, 4, 32, 1, value =>
        {
            _controls.SsaoSamples = (int)value;
            Custom();
        }, digits: 0));
        Add(Knob("Radius", "m", _controls.SsaoRadius, 0.05, 3, 0.05, value =>
        {
            _controls.SsaoRadius = (float)value;
            Custom();
        }, digits: 2));
        Add(Knob("Intensity", "", _controls.SsaoIntensity, 0.2, 4, 0.05, value =>
        {
            _controls.SsaoIntensity = (float)value;
            Custom();
        }, digits: 2));

        Add(Sheet.Section("Bloom"));
        Add(Switch("Bloom", _controls.Bloom, value =>
        {
            _controls.Bloom = value;
            Custom();
        }));
        Add(Knob("Intensity", "", _controls.BloomIntensity, 0, 2, 0.05, value =>
        {
            _controls.BloomIntensity = (float)value;
            Custom();
        }, digits: 2));
        Add(Knob("Threshold", "", _controls.BloomThreshold, 0.1, 4, 0.05, value =>
        {
            _controls.BloomThreshold = (float)value;
            Custom();
        }, digits: 2));

        Add(Sheet.Section("Camera effects"));
        Add(Switch("Depth of field", _controls.DepthOfField, value =>
        {
            _controls.DepthOfField = value;
            Custom();
        }));
        Add(Knob("Focus distance", "m", _controls.DofFocusDistance, 0.5, 120, 0.5, value =>
        {
            _controls.DofFocusDistance = (float)value;
            Custom();
        }, digits: 1));
        Add(Knob("Focus range", "m", _controls.DofFocusRange, 0.2, 40, 0.2, value =>
        {
            _controls.DofFocusRange = (float)value;
            Custom();
        }, digits: 1));
        Add(Knob("Blur radius", "px", _controls.DofMaxBlur, 2, 40, 1, value =>
        {
            _controls.DofMaxBlur = (float)value;
            Custom();
        }, digits: 0));
        Add(Switch("Motion blur", _controls.MotionBlur, value =>
        {
            _controls.MotionBlur = value;
            Custom();
        }));
        Add(Knob("Shutter", "", _controls.MotionBlurStrength, 0, 1, 0.05, value =>
        {
            _controls.MotionBlurStrength = (float)value;
            Custom();
        }, digits: 2));

        void Custom()
        {
            _controls.Quality = QualityPreset.Custom;
            ApplyRenderOptions();
        }
    }

    private void AddQualityChips()
    {
        WrapPanel chips = new();
        foreach (QualityPreset preset in new[]
                 {
                     QualityPreset.Low, QualityPreset.Medium, QualityPreset.High, QualityPreset.Ultra, QualityPreset.Cinematic,
                 })
        {
            Button chip = new() { Content = preset.ToString(), Margin = new Thickness(0, 0, 5, 6) };
            chip.Click += (_, _) =>
            {
                _controls.LoadQuality(preset);
                _controls.ClampTo(_capabilities);
                ApplyRenderOptions();
                BuildPanel();
            };
            chips.Children.Add(chip);
        }

        Add(chips);
    }

    // --------------------------------------------------------------- compare

    private void AddCompare()
    {
        Add(Sheet.Section("Compare", _compare ? "on" : "off"));
        Add(Sheet.Paragraph("B renders the same scene, the same camera and the same frame with its own settings."));
        Add(Switch("Split the viewport", _compare, value =>
        {
            _compare = value;
            ApplyCompare();
            BuildPanel();
            Status(value ? "Comparing A and B." : "Comparing off.");
        }));

        if (!_compare)
        {
            return;
        }

        Add(Knob("Wipe", "%", _wipe * 100, 0, 100, 1, value =>
        {
            _wipe = value / 100.0;
            UpdateCompareClip();
        }, digits: 0));

        Add(Sheet.Section("B side"));
        Add(Switch("Shadows", _compareControls.Shadows, value =>
        {
            _compareControls.Shadows = value;
            ApplyRenderOptions();
        }));
        Add(Switch("SSAO", _compareControls.Ssao, value =>
        {
            _compareControls.Ssao = value;
            ApplyRenderOptions();
        }));
        Add(Switch("Bloom", _compareControls.Bloom, value =>
        {
            _compareControls.Bloom = value;
            ApplyRenderOptions();
        }));
        Add(Choice("Render path", ["Forward", "Deferred"], (int)_compareControls.RenderPath, index =>
        {
            _compareControls.RenderPath = (RenderPath)index;
            ApplyRenderOptions();
        }));
        Add(Choice("Anti-aliasing", ["Off", "2x", "4x", "8x"], _compareControls.MsaaSamples switch
        {
            8 => 3, 4 => 2, 2 => 1, _ => 0,
        }, index =>
        {
            _compareControls.MsaaSamples = index switch { 3 => 8, 2 => 4, 1 => 2, _ => 1 };
            _compareControls.ClampTo(_capabilities);
            ApplyRenderOptions();
        }));
        Add(Sheet.Buttons(
            ("Copy A to B", () =>
            {
                RendererOptions live = Viewport.Renderer?.Options ?? RendererOptions.Default;
                _compareControls.CaptureFrom(_controls.Apply(live));
                ApplyRenderOptions();
                BuildPanel();
            }),
            ("Swap", () =>
            {
                RendererOptions a = _controls.Apply(RendererOptions.Default);
                RendererOptions b = _compareControls.Apply(RendererOptions.Default);
                _controls.CaptureFrom(b);
                _compareControls.CaptureFrom(a);
                ApplyRenderOptions();
                BuildPanel();
            })));
    }

    // -------------------------------------------------------------- benchmark

    private void BuildBenchmark()
    {
        Add(Sheet.Section("Run"));
        Add(Sheet.Paragraph(
            "Each scene is warmed up, then measured along a fixed camera path, so two runs compare like for like."));

        Add(Choice("Profile", Profiles.Select(profile => profile.Name).ToArray(),
            Math.Max(0, Array.FindIndex(Profiles, profile => profile.Name == _benchmarkProfile)),
            index => _benchmarkProfile = Profiles[index].Name));

        Add(Choice("Scenes", ["Every scene", "Flagship four", "Current scene"],
            _sceneSet switch { "Flagship four" => 1, "Current scene" => 2, _ => 0 },
            index => _sceneSet = index switch { 1 => "Flagship four", 2 => "Current scene", _ => "Every scene" }));

        Add(Knob("Seconds per scene", "s", _secondsPerScene, 3, 20, 1, value => _secondsPerScene = (float)value, digits: 0));

        _benchmarkBar = new ProgressBar { Minimum = 0, Maximum = 1, Value = _runner.Progress, Height = 4, Margin = new Thickness(0, 10, 0, 6) };
        Add(_benchmarkBar);
        _benchmarkStatus = new TextBlock { Text = _runner.IsRunning ? "running" : "idle" };
        _benchmarkStatus.Classes.Add("readout");
        Add(_benchmarkStatus);

        Add(Sheet.Buttons(
            (_runner.IsRunning ? "Cancel run" : "Start run", () =>
            {
                if (_runner.IsRunning)
                {
                    CancelBenchmark();
                }
                else
                {
                    StartBenchmark();
                }
            }),
            ("Export last run", ExportBenchmark)));

        if (_runner.Results.Count == 0)
        {
            return;
        }

        Add(Sheet.Section("Results", $"{_runner.Results.Count} scenes"));
        foreach (BenchmarkEntry entry in _runner.Results)
        {
            Grid row = new()
            {
                ColumnDefinitions = new ColumnDefinitions("38,*,Auto"),
                Margin = new Thickness(0, 3, 0, 3),
            };
            TextBlock code = new() { Text = entry.SceneId, VerticalAlignment = VerticalAlignment.Center };
            code.Classes.Add("code");
            TextBlock title = new() { Text = entry.SceneTitle, TextTrimming = TextTrimming.CharacterEllipsis };
            title.Classes.Add("body");
            Grid.SetColumn(title, 1);
            TextBlock value = new()
            {
                Text = $"{entry.AverageFps,5:F0} fps  low {entry.OnePercentLowFps,4:F0}",
                Foreground = Sheet.Brush(entry.AverageFps >= 58 ? "Cyan" : entry.AverageFps >= 30 ? "Amber" : "Crimson"),
            };
            value.Classes.Add("readout");
            Grid.SetColumn(value, 2);
            row.Children.Add(code);
            row.Children.Add(title);
            row.Children.Add(value);
            Add(row);
            Add(Sheet.Fact("   frame times",
                $"median {entry.MedianMs:F2} ms  p95 {entry.P95Ms:F2}  p99 {entry.P99Ms:F2}"));
            Add(Sheet.Fact("   cost",
                $"cpu {entry.CpuMs:F2} ms  gpu {entry.GpuMs:F2} ms  {entry.DrawCalls} draws"));
            if (entry.Stutters > 0)
            {
                Add(Sheet.Fact("   stutters", entry.Stutters.ToString(), Sheet.Brush("Amber")));
            }
        }
    }

    private void StartBenchmark()
    {
        List<DemoScene> scenes = _sceneSet switch
        {
            "Flagship four" => SceneCatalog.Flagship.ToList(),
            "Current scene" => _active is null ? [] : [_active],
            _ => SceneCatalog.All.ToList(),
        };

        if (scenes.Count == 0)
        {
            Status("Nothing to measure.");
            return;
        }

        (string _, QualityPreset quality, int height) = Profiles.First(profile => profile.Name == _benchmarkProfile);
        if (quality != QualityPreset.Custom)
        {
            _controls.LoadQuality(quality);
        }

        if (height > 0 && Viewport.Bounds.Height > 0)
        {
            // The window is the window: the profile matches its internal
            // resolution instead, which the report writes down.
            _controls.RenderScale = Math.Clamp(height / Viewport.Bounds.Height, 0.25, 2.0);
        }

        _controls.DebugView = DebugView.Off;
        _controls.Wireframe = false;
        _controls.VSync = false;
        _controls.FpsCap = 240;
        _controls.ClampTo(_capabilities);
        ApplyRenderOptions();

        _compare = false;
        ApplyCompare();
        SpinToggle.IsChecked = false;
        PauseToggle.IsChecked = false;

        _runner.Start(scenes);
        if (_runner.Current is { } first)
        {
            LoadScene(first);
            _runner.SceneLoaded();
        }

        SetMode(AppMode.Benchmark);
        Status($"Benchmark started: {scenes.Count} scenes at {_secondsPerScene:F0} s each.");
    }

    private void CancelBenchmark()
    {
        _runner.Cancel();
        BuildPanel();
        Status("Benchmark cancelled.");
    }

    private void FinishBenchmark()
    {
        BuildPanel();
        float average = _runner.Results.Count == 0 ? 0f : _runner.Results.Average(entry => entry.AverageFps);
        Status($"Benchmark finished: {_runner.Results.Count} scenes, {average:F0} fps on average. Export writes JSON and CSV.");
    }

    private void UpdateBenchmarkProgress()
    {
        if (_benchmarkBar is null || _benchmarkStatus is null)
        {
            return;
        }

        _benchmarkBar.Value = _runner.Progress;
        _benchmarkStatus.Text = _runner.Current is { } scene
            ? $"{_runner.Phase.ToString().ToLowerInvariant()} {scene.Id} ({_runner.Index + 1}/{_runner.Total})"
            : "idle";
    }

    private void ExportBenchmark()
    {
        if (_runner.Results.Count == 0)
        {
            Status("Run a benchmark first.");
            return;
        }

        Renderer? renderer = Viewport.Renderer;
        BenchmarkHeader header = new(
            renderer?.AdapterName ?? "unknown",
            renderer?.AdapterDriver ?? string.Empty,
            _capabilities.Backend.ToString(),
            _capabilities.DeviceType.ToString(),
            _benchmarkProfile,
            _controls.RenderPath.ToString(),
            renderer?.Width ?? 0,
            renderer?.Height ?? 0,
            _controls.MsaaSamples,
            _controls.Shadows,
            _controls.Ssao,
            _controls.Bloom,
            _capabilities.TimestampQueries,
            ThreeNetRuntime.NativeVersion,
            DateTimeOffset.Now);

        try
        {
            (string json, string csv) = BenchmarkReport.Write(header, _runner.Results);
            Status($"Exported {Path.GetFileName(json)} and {Path.GetFileName(csv)} to {Path.GetDirectoryName(json)}.");
        }
        catch (IOException exception)
        {
            Status($"Export failed: {exception.Message}");
        }
    }

    // ---------------------------------------------------------------- inspect

    private void BuildInspect()
    {
        Add(Sheet.Section("Debug view", _controls.DebugView == DebugView.Off ? "off" : Humanise(_controls.DebugView).ToLowerInvariant()));
        Add(Sheet.Paragraph(
            "A debug view replaces the shaded image with one channel of the surface. Tone mapping, bloom and the camera effects step aside so the values arrive unchanged."));

        WrapPanel views = new();
        foreach (DebugView view in Enum.GetValues<DebugView>())
        {
            ToggleButton chip = new()
            {
                Content = view == DebugView.Off ? "Shaded" : Humanise(view).ToLowerInvariant(),
                Margin = new Thickness(0, 0, 5, 5),
                IsChecked = _controls.DebugView == view,
            };
            chip.Classes.Add("preset");
            chip.Classes.Add("inspect");
            chip.Click += (_, _) =>
            {
                _controls.DebugView = view;
                ApplyRenderOptions();
                BuildPanel();
                Status(view == DebugView.Off ? "Back to the shaded image." : $"Showing {Humanise(view).ToLowerInvariant()}.");
            };
            views.Children.Add(chip);
        }

        Add(views);
        if (_controls.DebugView == DebugView.Uv && _controls.RenderPath == RenderPath.Deferred)
        {
            Add(Sheet.Note("The G-buffer carries no texture coordinates, so the deferred path draws magenta here. Switch to forward to see them."));
        }

        ToggleSwitch wireframe = new()
        {
            IsChecked = _controls.Wireframe,
            IsEnabled = _capabilities.WireframeRendering,
            Margin = new Thickness(0, 8, 0, 0),
            OnContent = null,
            OffContent = null,
        };
        wireframe.IsCheckedChanged += (_, _) =>
        {
            _controls.Wireframe = wireframe.IsChecked == true;
            ApplyRenderOptions();
        };
        Add(Sheet.Fact("Wireframe", _capabilities.WireframeRendering ? "available" : "unsupported",
            Sheet.Brush(_capabilities.WireframeRendering ? "Text" : "Crimson")));
        Add(wireframe);
        if (!_capabilities.WireframeRendering)
        {
            Add(Sheet.Note("This GPU has no line polygon mode, so wireframe stays off."));
        }

        // -------------------------------------------------------- pass ladder
        FrameStats stats = Viewport.Stats;
        Add(Sheet.Section("Pipeline", $"{stats.DrawCalls + stats.ShadowDrawCalls} draws"));
        Add(Sheet.Fact("1  visibility", $"{stats.VisibleNodes} drawn, {stats.CulledNodes} culled"));
        Add(Sheet.Fact("2  shadow maps", _controls.Shadows ? $"{stats.ShadowLayers} layers, {stats.ShadowDrawCalls} draws" : "off"));
        Add(Sheet.Fact("3  depth prepass", _controls.Ssao || _controls.DepthOfField || _controls.MotionBlur ? "on" : "off"));
        Add(Sheet.Fact("4  ambient occlusion", _controls.Ssao ? $"{_controls.SsaoSamples} samples" : "off"));
        Add(Sheet.Fact("5  geometry", _controls.RenderPath == RenderPath.Deferred ? "G-buffer, 5 targets" : $"forward, {_controls.MsaaSamples}x MSAA"));
        Add(Sheet.Fact("6  lighting", $"{stats.Lights} lights"));
        Add(Sheet.Fact("7  camera effects", _controls.DepthOfField || _controls.MotionBlur
            ? $"{(_controls.DepthOfField ? "depth of field" : "")}{(_controls.DepthOfField && _controls.MotionBlur ? " + " : "")}{(_controls.MotionBlur ? "motion blur" : "")}"
            : "off"));
        Add(Sheet.Fact("8  bloom", _controls.Bloom ? $"threshold {_controls.BloomThreshold:F2}" : "off"));
        Add(Sheet.Fact("9  tone mapping", _controls.DebugView == DebugView.Off ? _controls.ToneMapping.ToString() : "bypassed"));
        Add(Sheet.Fact("10 overlay", "HUD panels drawn by the engine"));

        // ------------------------------------------------------------ picking
        Add(Sheet.Section("Picked surface", _selection is null ? "nothing" : "hit"));
        if (_selection is { } hit)
        {
            BoundingBox bounds = _scene!.GetBounds(hit.Node);
            Add(Sheet.Fact("node", hit.Node.Name));
            Add(Sheet.Fact("distance", $"{hit.Distance:F3} m"));
            Add(Sheet.Fact("point", $"{hit.Point.X:F2}, {hit.Point.Y:F2}, {hit.Point.Z:F2}"));
            Add(Sheet.Fact("normal", $"{hit.Normal.X:F2}, {hit.Normal.Y:F2}, {hit.Normal.Z:F2}"));
            Add(Sheet.Fact("triangle", hit.TriangleIndex.ToString()));
            Add(Sheet.Fact("bounds", $"{bounds.Size.X:F2} x {bounds.Size.Y:F2} x {bounds.Size.Z:F2} m"));
            Add(Sheet.Fact("casts shadow", hit.Node.CastShadow ? "yes" : "no"));
            Add(Sheet.Fact("receives shadow", hit.Node.ReceiveShadow ? "yes" : "no"));
        }
        else
        {
            Add(Sheet.Paragraph("Click any surface in the viewport to read its node, material and bounds."));
        }

        AddCapabilities();
        AddCompare();
    }

    private void AddCapabilities()
    {
        Add(Sheet.Section("This GPU"));
        Renderer? renderer = Viewport.Renderer;
        Add(Sheet.Wide("adapter", renderer?.AdapterName ?? "-"));
        if (renderer?.AdapterDriver is { Length: > 0 } driver)
        {
            Add(Sheet.Wide("driver", driver));
        }

        Add(Sheet.Fact("backend", _capabilities.Backend.ToString()));
        Add(Sheet.Fact("device", _capabilities.DeviceType.ToString()));
        Add(Sheet.Fact("max texture", $"{_capabilities.MaxTextureSize} px"));
        Add(Sheet.Fact("max buffer", $"{_capabilities.MaxBufferSize / (1024 * 1024)} MB"));
        Add(Sheet.Fact("max MSAA", $"{_capabilities.MaxMsaaSamples}x"));
        Yes("GPU frame timing", _capabilities.TimestampQueries);
        Yes("wireframe", _capabilities.WireframeRendering);
        Yes("BC textures", _capabilities.TextureCompressionBc);
        Yes("ETC2 textures", _capabilities.TextureCompressionEtc2);
        Yes("ASTC textures", _capabilities.TextureCompressionAstc);
        Yes("filterable float", _capabilities.Float32Filterable);
        Add(Sheet.Fact("core", ThreeNetRuntime.NativeVersion));

        void Yes(string label, bool value) =>
            Add(Sheet.Fact(label, value ? "yes" : "no", Sheet.Brush(value ? "Lime" : "Dim")));
    }

    // ---------------------------------------------------------------- sandbox

    private void BuildSandbox()
    {
        Add(Sheet.Section("Spawn"));
        Add(Sheet.Buttons(
            ("Box", () => SpawnInSandbox(SpawnKind.Box)),
            ("Sphere", () => SpawnInSandbox(SpawnKind.Sphere)),
            ("Cylinder", () => SpawnInSandbox(SpawnKind.Cylinder)),
            ("Cone", () => SpawnInSandbox(SpawnKind.Cone)),
            ("Torus", () => SpawnInSandbox(SpawnKind.Torus)),
            ("Point light", () => SpawnInSandbox(SpawnKind.PointLight)),
            ("Spot light", () => SpawnInSandbox(SpawnKind.SpotLight))));

        Add(Sheet.Section("Selection", _sandbox.Selected?.Name ?? "nothing"));
        if (_sandbox.Selected is { } selected)
        {
            Add(Sheet.Fact("position", $"{selected.Position.X:F2}, {selected.Position.Y:F2}, {selected.Position.Z:F2}"));
            if (_sandbox.SelectedMaterial() is { } material)
            {
                Add(Knob("Metallic", "", material.Metallic, 0, 1, 0.01, value =>
                    _sandbox.UpdateSelectedMaterial(options => options with { Metallic = (float)value }), digits: 2));
                Add(Knob("Roughness", "", material.Roughness, 0.02, 1, 0.01, value =>
                    _sandbox.UpdateSelectedMaterial(options => options with { Roughness = (float)value }), digits: 2));
                Add(Knob("Hue", "deg", 0, 0, 360, 1, value =>
                    _sandbox.UpdateSelectedMaterial(options => options with { BaseColor = Hue((float)value) }), digits: 0));
            }

            Add(Sheet.Buttons(
                ("Duplicate", () =>
                {
                    _sandbox.DuplicateSelected();
                    RefreshSandboxDragging();
                    BuildPanel();
                }),
                ("Delete", () =>
                {
                    _sandbox.DeleteSelected();
                    BuildPanel();
                    Status("Deleted.");
                })));
        }
        else
        {
            Add(Sheet.Paragraph("Click an object to select it, then drag it on the ground."));
        }

        Add(Sheet.Section("Physics", _sandbox.PhysicsRunning ? "running" : "stopped"));
        Add(Switch("Run physics", _sandbox.PhysicsRunning, value =>
        {
            _sandbox.SetPhysics(value);
            BuildPanel();
            Status(value ? "Physics running." : "Physics stopped and everything is back where it started.");
        }));
        Add(Sheet.Buttons(("Shake", () =>
        {
            _sandbox.SetPhysics(true);
            _sandbox.Nudge();
            BuildPanel();
            Status("Impulse applied to every body.");
        })));

        Add(Sheet.Section("Scene parameters"));
        foreach (DemoParameter parameter in _sandbox.Parameters)
        {
            Add(Sheet.Parameter(parameter, changed => _sandbox.SetParameter(changed.Id, changed.Value)));
        }

        Add(Sheet.Section("Save"));
        Add(Sheet.Paragraph("The stage is written as a scene document, which ThreeEditor can open."));
        Add(Sheet.Buttons(("Save scene document", () =>
        {
            try
            {
                string path = _sandbox.Save(Path.Combine(BenchmarkReport.OutputDirectory, "sandbox.json"));
                Status($"Saved {path}.");
            }
            catch (IOException exception)
            {
                Status($"Save failed: {exception.Message}");
            }
        })));

        AddMetrics(_sandbox);
        AddQuality();
    }

    private void SpawnInSandbox(SpawnKind kind)
    {
        _sandbox.Spawn(kind);
        RefreshSandboxDragging();
        BuildPanel();
        Status($"Spawned a {kind.ToString().ToLowerInvariant()}.");
    }

    private void RefreshSandboxDragging()
    {
        if (Viewport.Interaction is { } interaction)
        {
            _sandbox.EnableDragging(interaction);
        }
    }

    private static Vector4 Hue(float degrees)
    {
        float h = degrees / 60f;
        float x = 1f - MathF.Abs((h % 2f) - 1f);
        Vector3 rgb = h switch
        {
            < 1f => new Vector3(1f, x, 0f),
            < 2f => new Vector3(x, 1f, 0f),
            < 3f => new Vector3(0f, 1f, x),
            < 4f => new Vector3(0f, x, 1f),
            < 5f => new Vector3(x, 0f, 1f),
            _ => new Vector3(1f, 0f, x),
        };
        return new Vector4(rgb * 0.75f, 1f);
    }

    // ----------------------------------------------------------- small pieces

    /// <summary>A slider row that reports its own value.</summary>
    private static Control Knob(
        string label,
        string unit,
        double value,
        double min,
        double max,
        double step,
        Action<double> changed,
        int digits = 1,
        Func<double, string>? format = null)
    {
        StackPanel block = new() { Margin = new Thickness(0, 4, 0, 8) };
        Control row = Sheet.LiveFact(label, Text(value), out TextBlock valueBlock);
        block.Children.Add(row);

        Slider slider = new()
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            TickFrequency = step,
            IsSnapToTickEnabled = step > 0,
        };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty)
            {
                return;
            }

            changed(slider.Value);
            valueBlock.Text = Text(slider.Value);
        };
        block.Children.Add(slider);
        return block;

        string Text(double current) =>
            format is not null
                ? format(current)
                : unit.Length == 0
                    ? current.ToString("F" + digits)
                    : $"{current.ToString("F" + digits)} {unit}";
    }

    private static Control Switch(string label, bool value, Action<bool> changed)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4, 0, 4) };
        TextBlock caption = new() { Text = label, VerticalAlignment = VerticalAlignment.Center };
        caption.Classes.Add("body");
        row.Children.Add(caption);

        ToggleSwitch toggle = new()
        {
            IsChecked = value,
            OnContent = null,
            OffContent = null,
            MinHeight = 22,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        toggle.IsCheckedChanged += (_, _) => changed(toggle.IsChecked == true);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return row;
    }

    private static Control Choice(string label, string[] options, int index, Action<int> changed)
    {
        StackPanel block = new() { Margin = new Thickness(0, 4, 0, 8) };
        TextBlock caption = new() { Text = label };
        caption.Classes.Add("body");
        block.Children.Add(caption);

        ComboBox combo = new()
        {
            ItemsSource = options,
            SelectedIndex = Math.Clamp(index, 0, options.Length - 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 3, 0, 0),
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
            {
                changed(combo.SelectedIndex);
            }
        };
        block.Children.Add(combo);
        return block;
    }
}
