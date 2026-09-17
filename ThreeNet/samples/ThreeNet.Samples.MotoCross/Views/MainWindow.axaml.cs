using System.Numerics;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using MotoCross.Game;
using ThreeNet;
using ThreeNet.Avalonia;
using AvaloniaPoint = Avalonia.Point;
using Key = Avalonia.Input.Key;

namespace MotoCross.Views;

public partial class MainWindow : Window
{
    private readonly GameWorld _game = new();
    private readonly HashSet<Key> _keys = [];
    private readonly Gamepads _pads = new();
    private readonly Polyline _mapTrack = new() { Stroke = new SolidColorBrush(Color.FromRgb(0x6B, 0x51, 0x36)), StrokeThickness = 5 };
    private readonly Ellipse _mapBike = new() { Width = 9, Height = 9, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x18)) };
    private readonly Ellipse _mapCheckpoint = new() { Width = 7, Height = 7, Fill = new SolidColorBrush(Color.FromRgb(0x4F, 0xD1, 0xE5)) };
    private double _messageTimer;
    private double _countdown = 3.2;
    private double _statsTimer;
    private int _frames;
    private double _fps;

    public MainWindow()
    {
        InitializeComponent();

        Viewport.Scene = _game.Scene;
        Viewport.Camera = _game.CameraNode;
        Viewport.RendererOptions = RendererOptions.Default with
        {
            BgraOutput = true,
            VSync = false,
            MsaaSamples = 4,
            ToneMapping = ToneMapping.Aces,
            Exposure = 1.05f,
            Bloom = true,
            BloomIntensity = 0.5f,
            BloomThreshold = 1.15f,
            Shadows = true,
            ShadowMapSize = 2048,
            ShadowCascades = 3,
            ShadowDistance = 85f,
            ShadowSoftness = 1,
            Ssao = true,
            SsaoRadius = 0.7f,
            SsaoIntensity = 1.4f,
            SsaoDirectStrength = 0.25f,
        };
        // Rendering happens offscreen and is blitted, so a slightly lower
        // internal resolution keeps the frame rate up on laptop GPUs.
        Viewport.RenderScale = 0.85;
        Viewport.MaxFramesPerSecond = 90;
        Viewport.Frame += OnFrame;

        CameraBox.ItemsSource = new[] { "Chase cam", "Cockpit", "Cinematic" };
        TimeBox.ItemsSource = new[] { "Morning", "Noon", "Sunset", "Night" };
        CameraBox.SelectionChanged += (_, _) => _game.CameraView = (CameraMode)Math.Max(0, CameraBox.SelectedIndex);
        TimeBox.SelectionChanged += (_, _) => _game.ApplyTimeOfDay((TimeOfDay)Math.Max(0, TimeBox.SelectedIndex));
        QualityToggle.IsCheckedChanged += (_, _) => ApplyQuality(QualityToggle.IsChecked == true);
        SoundToggle.IsCheckedChanged += (_, _) =>
        {
            _game.Audio.Muted = SoundToggle.IsChecked != true;
            SoundToggle.Content = _game.Audio.Muted ? "Sound: off" : "Sound: on";
        };
        RestartButton.Click += (_, _) => RestartRace();
        HelpButton.Click += (_, _) => ToggleHelp(true);
        CloseHelpButton.Click += (_, _) => ToggleHelp(false);

        if (_game.Audio.Unavailable is { } reason)
        {
            AudioNote.IsVisible = true;
            AudioNote.Text = $"Sound is off: {reason}.";
            SoundToggle.IsChecked = false;
            SoundToggle.Content = "Sound: n/a";
            SoundToggle.IsEnabled = false;
        }

        BuildMiniMap();
        ToggleHelp(true);
        Opened += (_, _) => Viewport.Focus();
        // Releasing every key avoids a stuck throttle when the window loses focus.
        Deactivated += (_, _) => _keys.Clear();
        Closed += (_, _) =>
        {
            _game.Dispose();
            _pads.Dispose();
        };
    }

    private void ApplyQuality(bool high)
    {
        Viewport.RendererOptions = Viewport.RendererOptions with
        {
            Shadows = high,
            Ssao = high,
            Bloom = high,
            MsaaSamples = high ? 4 : 1,
            ShadowCascades = high ? 3 : 2,
        };
        Viewport.RenderScale = high ? 0.85 : 0.7;
        QualityToggle.Content = high ? "Effects: high" : "Effects: fast";
    }

    private void RestartRace()
    {
        _game.Restart();
        _countdown = 3.2;
        Viewport.Focus();
    }

    private void ToggleHelp(bool visible)
    {
        HelpOverlay.IsVisible = visible;
        _game.Paused = visible;
        if (!visible)
        {
            Viewport.Focus();
        }
    }

    private void OnFrame(object? sender, FrameEventArgs e)
    {
        float dt = MathF.Min(e.DeltaSeconds, 0.05f);

        // The countdown holds the rider on the line for the first few seconds.
        bool racing = _countdown <= 0;
        if (!racing && !_game.Paused)
        {
            _countdown -= dt;
        }

        _pads.Update();
        HandlePadButtons();
        RiderInput input = ReadInput(racing);
        _game.Update(dt, input, e.TotalSeconds);
        UpdateHud(dt);

        // Frame rate and renderer counters, refreshed twice a second.
        _frames++;
        _statsTimer += dt;
        if (_statsTimer >= 0.5)
        {
            _fps = _frames / _statsTimer;
            _frames = 0;
            _statsTimer = 0;
            FrameStats stats = Viewport.Stats;
            StatsText.Text = $"{_fps:0} fps · {stats.DrawCalls} draws · {stats.Triangles / 1000} k tris · {stats.ShadowLayers} shadow layers";
        }
    }

    private RiderInput ReadInput(bool racing)
    {
        if (!racing || _game.Paused)
        {
            return default;
        }

        float throttle = (_keys.Contains(Key.W) || _keys.Contains(Key.Up)) ? 1f : 0f;
        float brake = (_keys.Contains(Key.S) || _keys.Contains(Key.Down)) ? 1f : 0f;
        // Positive steer is a left turn.
        float steer = (_keys.Contains(Key.A) ? 1f : 0f) - (_keys.Contains(Key.D) ? 1f : 0f);
        float lean = (_keys.Contains(Key.Left) ? 1f : 0f) - (_keys.Contains(Key.Right) ? 1f : 0f);
        bool hop = _keys.Contains(Key.Space);

        // Gamepad: triggers for throttle and brake, left stick steers, right stick leans, A hops.
        if (_pads.Connected is [var pad, ..])
        {
            throttle = MathF.Max(throttle, pad.RightTrigger);
            brake = MathF.Max(brake, pad.LeftTrigger);
            steer = Math.Clamp(steer - pad.LeftStick.X, -1f, 1f);
            lean = Math.Clamp(lean - pad.RightStick.X, -1f, 1f);
            hop |= pad.IsDown(GamepadButton.A);
        }

        return new RiderInput(throttle, brake, steer, lean, hop);
    }

    private void HandlePadButtons()
    {
        if (_pads.Connected is not [var pad, ..])
        {
            return;
        }

        if (pad.WasPressed(GamepadButton.Y))
        {
            CameraBox.SelectedIndex = (CameraBox.SelectedIndex + 1) % 3;
        }

        if (pad.WasPressed(GamepadButton.X))
        {
            TimeBox.SelectedIndex = (TimeBox.SelectedIndex + 1) % 4;
        }

        if (pad.WasPressed(GamepadButton.Select))
        {
            _game.Respawn();
        }

        if (pad.WasPressed(GamepadButton.Start))
        {
            _game.Paused = !_game.Paused;
        }

        if (pad.WasPressed(GamepadButton.B))
        {
            RestartRace();
        }
    }

    private void UpdateHud(float dt)
    {
        SpeedText.Text = ((int)_game.SpeedKph).ToString();
        GearText.Text = _game.Bike.Gear.ToString();
        RevBar.Value = _game.Bike.Revs;

        RaceState race = _game.Race;
        LapText.Text = $"{race.Lap} / {race.TotalLaps}";
        LapBar.Value = race.LapProgress;
        LapTimeText.Text = RaceState.FormatTime(race.LapTime);
        BestLapText.Text = RaceState.FormatTime(race.BestLap);
        AirText.Text = $"{(_game.Bike.Grounded ? _game.Bike.BestAirTime : _game.Bike.AirTime):0.00} s";

        UpdateMiniMap();

        // Centre message: countdown, then off-track and finish notices.
        if (_countdown > 0)
        {
            CentreText.IsVisible = true;
            CentreText.Text = _countdown > 1f ? ((int)_countdown).ToString() : "GO!";
            return;
        }

        if (race.Finished)
        {
            CentreText.IsVisible = true;
            CentreText.Text = $"FINISH  {RaceState.FormatTime(race.TotalTime)}";
            return;
        }

        if (_game.Bike.OffTrack)
        {
            _messageTimer = 0.6;
            CentreText.IsVisible = true;
            CentreText.Text = "OFF TRACK";
            return;
        }

        _messageTimer -= dt;
        CentreText.IsVisible = _messageTimer > 0;
    }

    private void BuildMiniMap()
    {
        MiniMap.Children.Clear();
        Avalonia.Collections.AvaloniaList<AvaloniaPoint> points = [];
        foreach (Vector3 sample in _game.TrackOutline.Where((_, index) => index % 12 == 0))
        {
            points.Add(MapPoint(sample));
        }

        points.Add(points[0]);
        _mapTrack.Points = points;
        MiniMap.Children.Add(_mapTrack);
        MiniMap.Children.Add(_mapCheckpoint);
        MiniMap.Children.Add(_mapBike);
    }

    private void UpdateMiniMap()
    {
        AvaloniaPoint bike = MapPoint(_game.Bike.Position);
        Canvas.SetLeft(_mapBike, bike.X - (_mapBike.Width / 2));
        Canvas.SetTop(_mapBike, bike.Y - (_mapBike.Height / 2));

        AvaloniaPoint checkpoint = MapPoint(_game.Race.CheckpointPosition(_game.Race.NextCheckpoint));
        Canvas.SetLeft(_mapCheckpoint, checkpoint.X - (_mapCheckpoint.Width / 2));
        Canvas.SetTop(_mapCheckpoint, checkpoint.Y - (_mapCheckpoint.Height / 2));
    }

    /// <summary>World XZ to mini map pixels.</summary>
    private static AvaloniaPoint MapPoint(Vector3 world)
    {
        const float half = TerrainField.WorldSize * 0.5f;
        double x = ((world.X + half) / TerrainField.WorldSize * 160) + 10;
        double y = ((half - world.Z) / TerrainField.WorldSize * 160) + 10;
        return new AvaloniaPoint(x, y);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        _keys.Add(e.Key);
        switch (e.Key)
        {
            case Key.C:
                CameraBox.SelectedIndex = (CameraBox.SelectedIndex + 1) % 3;
                break;
            case Key.T:
                TimeBox.SelectedIndex = (TimeBox.SelectedIndex + 1) % 4;
                break;
            case Key.R:
                RestartRace();
                break;
            case Key.Back:
                _game.Respawn();
                break;
            case Key.P:
                _game.Paused = !_game.Paused;
                break;
            case Key.H:
                ToggleHelp(!HelpOverlay.IsVisible);
                break;
            case Key.Escape:
                ToggleHelp(false);
                break;
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        _keys.Remove(e.Key);
        base.OnKeyUp(e);
    }

}
