using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ThreeNet.Avalonia;

/// <summary>Arguments of the per frame update raised by <see cref="ThreeNetView"/>.</summary>
public sealed class FrameEventArgs(Renderer renderer, float deltaSeconds, double totalSeconds) : EventArgs
{
    /// <summary>The renderer drawing this view.</summary>
    public Renderer Renderer { get; } = renderer;

    /// <summary>Seconds elapsed since the previous frame.</summary>
    public float DeltaSeconds { get; } = deltaSeconds;

    /// <summary>Seconds elapsed since the view started rendering.</summary>
    public double TotalSeconds { get; } = totalSeconds;
}

/// <summary>
/// Hosts a Three.Net <see cref="Scene"/> inside an Avalonia layout.
/// </summary>
/// <remarks>
/// The view renders offscreen on the GPU and blits the result into a
/// <see cref="WriteableBitmap"/>. That costs one readback per frame but keeps
/// the control a normal part of the Avalonia visual tree on every platform:
/// it clips, layers and composes like any other control.
/// </remarks>
public class ThreeNetView : Control
{
    public static readonly StyledProperty<Scene?> SceneProperty =
        AvaloniaProperty.Register<ThreeNetView, Scene?>(nameof(Scene));

    public static readonly StyledProperty<Node?> CameraProperty =
        AvaloniaProperty.Register<ThreeNetView, Node?>(nameof(Camera));

    public static readonly StyledProperty<bool> IsRenderingProperty =
        AvaloniaProperty.Register<ThreeNetView, bool>(nameof(IsRendering), defaultValue: true);

    public static readonly StyledProperty<double> RenderScaleProperty =
        AvaloniaProperty.Register<ThreeNetView, double>(nameof(RenderScale), defaultValue: 1.0);

    public static readonly StyledProperty<int> MaxFramesPerSecondProperty =
        AvaloniaProperty.Register<ThreeNetView, int>(nameof(MaxFramesPerSecond), defaultValue: 60);

    public static readonly StyledProperty<RendererOptions> RendererOptionsProperty =
        AvaloniaProperty.Register<ThreeNetView, RendererOptions>(
            nameof(RendererOptions),
            defaultValue: RendererOptions.Default with { BgraOutput = true, VSync = false });

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render);
    private readonly Stopwatch _clock = new();
    private Renderer? _renderer;
    private WriteableBitmap? _bitmap;
    private PixelSize _surfaceSize;
    private TimeSpan _lastFrame;
    private byte[] _staging = [];
    private string? _failure;

    public ThreeNetView()
    {
        _timer.Tick += (_, _) => RenderFrame();
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>Scene rendered by this view.</summary>
    public Scene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    /// <summary>Camera node; the scene active camera is used when null.</summary>
    public Node? Camera
    {
        get => GetValue(CameraProperty);
        set => SetValue(CameraProperty, value);
    }

    /// <summary>Runs or pauses the render loop.</summary>
    public bool IsRendering
    {
        get => GetValue(IsRenderingProperty);
        set => SetValue(IsRenderingProperty, value);
    }

    /// <summary>
    /// Resolution multiplier, 1.0 renders at the native control size. Lower it
    /// (0.5 - 0.75) to trade sharpness for frame rate on slow GPUs.
    /// </summary>
    public double RenderScale
    {
        get => GetValue(RenderScaleProperty);
        set => SetValue(RenderScaleProperty, value);
    }

    /// <summary>Upper bound for the render loop; the GPU may still be slower.</summary>
    public int MaxFramesPerSecond
    {
        get => GetValue(MaxFramesPerSecondProperty);
        set => SetValue(MaxFramesPerSecondProperty, value);
    }

    /// <summary>Renderer settings applied when the renderer is (re)created.</summary>
    public RendererOptions RendererOptions
    {
        get => GetValue(RendererOptionsProperty);
        set => SetValue(RendererOptionsProperty, value);
    }

    /// <summary>
    /// Node level pointer events for the current <see cref="Scene"/> (created with
    /// the scene). Register handlers with <c>Interaction.OnClick(node, ...)</c> or
    /// make nodes draggable; a drag captures the pointer so camera controllers
    /// attached to this view do not react to it.
    /// </summary>
    public InteractionManager? Interaction { get; private set; }

    /// <summary>Set to false to stop feeding pointer input to <see cref="Interaction"/>.</summary>
    public bool EnableInteraction { get; set; } = true;

    /// <summary>The live renderer, available after <see cref="RendererCreated"/>.</summary>
    public Renderer? Renderer => _renderer;

    /// <summary>Statistics of the last rendered frame.</summary>
    public FrameStats Stats => _renderer?.Stats ?? default;

    /// <summary>Raised once the GPU renderer exists (and after every resize rebuild).</summary>
    public event EventHandler<Renderer>? RendererCreated;

    /// <summary>Raised before each frame; animate the scene here.</summary>
    public event EventHandler<FrameEventArgs>? Frame;

    /// <summary>Raised when the renderer cannot be created or a frame fails.</summary>
    public event EventHandler<string>? RenderFailed;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _clock.Restart();
        _lastFrame = TimeSpan.Zero;
        UpdateTimerState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
        _clock.Stop();
        DisposeGpuResources();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (EnableInteraction && Interaction is { } interaction && interaction.PointerMove(ToVector(e.GetPosition(this)), ViewportSize()))
        {
            e.Handled = true;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!EnableInteraction || Interaction is not { } interaction)
        {
            return;
        }

        PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
        MouseButton button = properties.IsRightButtonPressed ? MouseButton.Right
            : properties.IsMiddleButtonPressed ? MouseButton.Middle
            : MouseButton.Left;
        if (interaction.PointerDown(ToVector(e.GetPosition(this)), ViewportSize(), button))
        {
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!EnableInteraction || Interaction is not { } interaction)
        {
            return;
        }

        bool captured = interaction.HasPointerCapture;
        MouseButton button = e.InitialPressMouseButton switch
        {
            global::Avalonia.Input.MouseButton.Right => MouseButton.Right,
            global::Avalonia.Input.MouseButton.Middle => MouseButton.Middle,
            _ => MouseButton.Left,
        };
        interaction.PointerUp(ToVector(e.GetPosition(this)), ViewportSize(), button);
        if (captured)
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Interaction?.PointerExit();
    }

    private static System.Numerics.Vector2 ToVector(Point point) => new((float)point.X, (float)point.Y);

    private System.Numerics.Vector2 ViewportSize()
    {
        // The HUD is laid out in render target pixels, which differ from DIPs.
        if (Interaction is { } interaction && _renderer is { } renderer)
        {
            interaction.OverlayTargetSize = new System.Numerics.Vector2(renderer.Width, renderer.Height);
        }

        return new((float)Bounds.Width, (float)Bounds.Height);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CameraProperty && Interaction is { } current)
        {
            current.Camera = Camera;
        }

        if (change.Property == IsRenderingProperty)
        {
            UpdateTimerState();
        }
        else if (change.Property == MaxFramesPerSecondProperty)
        {
            ApplyInterval();
        }
        else if (change.Property == RendererOptionsProperty || change.Property == RenderScaleProperty)
        {
            // Both change how the target is allocated, so rebuild it.
            DisposeGpuResources();
            InvalidateVisual();
        }
        else if (change.Property == SceneProperty)
        {
            _failure = null;
            Interaction = Scene is { } scene ? new InteractionManager(scene, Camera) : null;
            InvalidateVisual();
        }
    }

    private void UpdateTimerState()
    {
        ApplyInterval();
        if (IsRendering && IsAttachedToVisualTree())
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private bool IsAttachedToVisualTree() => TopLevel.GetTopLevel(this) is not null;

    /// <summary>Device pixels per logical pixel for the window hosting the view.</summary>
    private double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    private void ApplyInterval()
    {
        int fps = Math.Clamp(MaxFramesPerSecond, 1, 240);
        _timer.Interval = TimeSpan.FromSeconds(1.0 / fps);
    }

    /// <summary>Renders one frame immediately, outside the loop.</summary>
    public void RenderFrame()
    {
        if (Scene is not { } scene || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        double scaling = RenderScaling * Math.Clamp(RenderScale, 0.1, 4.0);
        PixelSize size = new(
            Math.Max(1, (int)Math.Round(Bounds.Width * scaling)),
            Math.Max(1, (int)Math.Round(Bounds.Height * scaling)));

        if (!EnsureTarget(size))
        {
            return;
        }

        TimeSpan now = _clock.Elapsed;
        float delta = _lastFrame == TimeSpan.Zero ? 1f / 60f : (float)(now - _lastFrame).TotalSeconds;
        _lastFrame = now;

        try
        {
            Frame?.Invoke(this, new FrameEventArgs(_renderer!, delta, now.TotalSeconds));
            _renderer!.Render(scene, Camera);
            CopyToBitmap();
        }
        catch (ThreeNetException exception)
        {
            Fail(exception.Message);
            return;
        }

        InvalidateVisual();
    }

    /// <summary>
    /// Copies the most recent frame into a new bitmap, for a screenshot or a
    /// thumbnail. Returns null when nothing has been rendered yet.
    /// </summary>
    public unsafe WriteableBitmap? CaptureFrame()
    {
        if (_renderer is null || _surfaceSize == default)
        {
            return null;
        }

        byte[] pixels = _renderer.ReadPixels();
        WriteableBitmap bitmap = new(_surfaceSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using ILockedFramebuffer buffer = bitmap.Lock();
        int rowBytes = _surfaceSize.Width * 4;
        for (int y = 0; y < _surfaceSize.Height; y++)
        {
            Span<byte> destination = new((void*)(buffer.Address + (nint)(y * buffer.RowBytes)), rowBytes);
            pixels.AsSpan(y * rowBytes, rowBytes).CopyTo(destination);
        }

        return bitmap;
    }

    private bool EnsureTarget(PixelSize size)
    {
        if (_failure is not null)
        {
            return false;
        }

        if (_renderer is not null && _surfaceSize == size)
        {
            return true;
        }

        try
        {
            if (_renderer is null)
            {
                RendererOptions options = RendererOptions with
                {
                    Width = size.Width,
                    Height = size.Height,
                    // The bitmap blit needs BGRA regardless of what the caller set.
                    BgraOutput = true,
                };
                _renderer = Renderer.CreateOffscreen(options);
                RendererCreated?.Invoke(this, _renderer);
            }
            else
            {
                _renderer.Resize(size.Width, size.Height);
            }

            _surfaceSize = size;
            _staging = new byte[_renderer.PixelBufferSize];
            _bitmap?.Dispose();
            double dpi = 96 * RenderScaling;
            _bitmap = new WriteableBitmap(size, new Vector(dpi, dpi), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            return true;
        }
        catch (ThreeNetException exception)
        {
            Fail(exception.Message);
            return false;
        }
    }

    private unsafe void CopyToBitmap()
    {
        if (_renderer is null || _bitmap is null)
        {
            return;
        }

        using ILockedFramebuffer buffer = _bitmap.Lock();
        int rowBytes = _surfaceSize.Width * 4;

        if (buffer.RowBytes == rowBytes)
        {
            // Tightly packed: read straight into the bitmap memory.
            Span<byte> destination = new((void*)buffer.Address, rowBytes * _surfaceSize.Height);
            _renderer.ReadPixels(destination);
            return;
        }

        // Padded rows: read once, then copy row by row.
        _renderer.ReadPixels(_staging);
        for (int y = 0; y < _surfaceSize.Height; y++)
        {
            Span<byte> source = _staging.AsSpan(y * rowBytes, rowBytes);
            Span<byte> destination = new((void*)(buffer.Address + (nint)(y * buffer.RowBytes)), rowBytes);
            source.CopyTo(destination);
        }
    }

    private void Fail(string message)
    {
        _failure = message;
        _timer.Stop();
        DisposeGpuResources();
        RenderFailed?.Invoke(this, message);
        InvalidateVisual();
    }

    private void DisposeGpuResources()
    {
        _renderer?.Dispose();
        _renderer = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _surfaceSize = default;
        _staging = [];
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_bitmap is not null)
        {
            Rect source = new(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
            context.DrawImage(_bitmap, source, new Rect(Bounds.Size));
            return;
        }

        // Nothing rendered yet: draw a placeholder so the control is visible.
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(14, 16, 22)), new Rect(Bounds.Size));
        if (_failure is not null)
        {
            FormattedText text = new(
                $"Three.Net: {_failure}",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                12,
                new SolidColorBrush(Color.FromRgb(229, 72, 77)))
            {
                MaxTextWidth = Math.Max(40, Bounds.Width - 24),
            };
            context.DrawText(text, new Point(12, 12));
        }
    }
}
