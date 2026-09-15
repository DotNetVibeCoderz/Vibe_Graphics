using System.Numerics;
using Avalonia;
using Avalonia.Input;

namespace ThreeNet.Avalonia;

/// <summary>
/// Mouse and touch camera controller: left drag orbits, right or middle drag
/// pans, the wheel zooms. Modelled on the Three.js <c>OrbitControls</c>.
/// </summary>
public sealed class OrbitController : IDisposable
{
    private readonly ThreeNetView _view;
    private readonly Node _camera;
    private Point? _lastPointer;
    private bool _panning;
    private bool _disposed;

    public OrbitController(ThreeNetView view, Node camera)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _camera = camera;

        _view.PointerPressed += OnPointerPressed;
        _view.PointerMoved += OnPointerMoved;
        _view.PointerReleased += OnPointerReleased;
        _view.PointerWheelChanged += OnPointerWheelChanged;
        _view.Frame += OnFrame;

        Apply();
    }

    /// <summary>Point the camera orbits around.</summary>
    public Vector3 Target { get; set; } = Vector3.Zero;

    /// <summary>Distance from the target in world units.</summary>
    public float Distance { get; set; } = 6f;

    public float MinDistance { get; set; } = 0.5f;

    public float MaxDistance { get; set; } = 500f;

    /// <summary>Horizontal angle in radians.</summary>
    public float Yaw { get; set; } = 0.6f;

    /// <summary>Vertical angle in radians, clamped just short of the poles.</summary>
    public float Pitch { get; set; } = 0.4f;

    /// <summary>Radians of orbit per pixel of drag.</summary>
    public float RotateSpeed { get; set; } = 0.008f;

    public float PanSpeed { get; set; } = 0.0025f;

    public float ZoomSpeed { get; set; } = 0.12f;

    public bool EnableRotate { get; set; } = true;

    public bool EnablePan { get; set; } = true;

    public bool EnableZoom { get; set; } = true;

    /// <summary>Radians per second added to <see cref="Yaw"/> when idle.</summary>
    public float AutoRotateSpeed { get; set; }

    /// <summary>Spins the camera on its own; paused while the user drags.</summary>
    public bool AutoRotate { get; set; }

    /// <summary>Frames the given bounds so the whole object is visible.</summary>
    public void FrameBounds(BoundingBox bounds, float padding = 1.6f)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        Target = bounds.Center;
        Distance = Math.Clamp(MathF.Max(bounds.Radius, 0.1f) * padding, MinDistance, MaxDistance);
        Apply();
    }

    /// <summary>Writes the current orbit state into the camera node.</summary>
    public void Apply()
    {
        float pitch = Math.Clamp(Pitch, -1.5f, 1.5f);
        Pitch = pitch;
        Distance = Math.Clamp(Distance, MinDistance, MaxDistance);

        float cosPitch = MathF.Cos(pitch);
        Vector3 offset = new(
            MathF.Sin(Yaw) * cosPitch,
            MathF.Sin(pitch),
            MathF.Cos(Yaw) * cosPitch);

        _camera.Position = Target + (offset * Distance);
        _camera.LookAt(Target);
    }

    private void OnFrame(object? sender, FrameEventArgs e)
    {
        if (!AutoRotate || _lastPointer is not null || AutoRotateSpeed == 0f)
        {
            return;
        }

        Yaw += AutoRotateSpeed * e.DeltaSeconds;
        Apply();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPointProperties properties = e.GetCurrentPoint(_view).Properties;
        _panning = properties.IsRightButtonPressed || properties.IsMiddleButtonPressed;
        _lastPointer = e.GetPosition(_view);
        e.Pointer.Capture(_view);
        _view.Focus();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_lastPointer is not { } previous)
        {
            return;
        }

        Point current = e.GetPosition(_view);
        double dx = current.X - previous.X;
        double dy = current.Y - previous.Y;
        _lastPointer = current;

        if (_panning)
        {
            if (!EnablePan)
            {
                return;
            }

            // Pan in the camera plane, scaled by the distance so the grab point
            // roughly follows the cursor.
            Matrix4x4 world = _camera.WorldMatrix;
            Vector3 right = new(world.M11, world.M12, world.M13);
            Vector3 up = new(world.M21, world.M22, world.M23);
            float scale = PanSpeed * Distance;
            Target += (right * (float)-dx * scale) + (up * (float)dy * scale);
        }
        else
        {
            if (!EnableRotate)
            {
                return;
            }

            Yaw -= (float)dx * RotateSpeed;
            Pitch += (float)dy * RotateSpeed;
        }

        Apply();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _lastPointer = null;
        _panning = false;
        e.Pointer.Capture(null);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!EnableZoom)
        {
            return;
        }

        // Exponential zoom keeps the step proportional to the current distance.
        Distance *= MathF.Exp((float)-e.Delta.Y * ZoomSpeed);
        Apply();
        e.Handled = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _view.PointerPressed -= OnPointerPressed;
        _view.PointerMoved -= OnPointerMoved;
        _view.PointerReleased -= OnPointerReleased;
        _view.PointerWheelChanged -= OnPointerWheelChanged;
        _view.Frame -= OnFrame;
    }
}
