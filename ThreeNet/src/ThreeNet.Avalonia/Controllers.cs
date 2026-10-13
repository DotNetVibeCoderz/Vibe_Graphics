using System.Numerics;
using Avalonia;
using Avalonia.Input;

namespace ThreeNet.Avalonia;

// ThreeNet has a Key of its own for the native window, and being the enclosing
// namespace it would win here, so the Avalonia one is named outright.
using Key = global::Avalonia.Input.Key;

/// <summary>
/// Shared plumbing for the controllers that fly a camera about rather than
/// orbit it: keyboard movement, mouse look, and the yaw/pitch pair both keep.
/// </summary>
public abstract class LookController : IDisposable
{
    private readonly HashSet<Key> _held = [];
    private Point? _lastPointer;
    private bool _looking;
    private bool _disposed;

    protected LookController(ThreeNetView view, Node camera)
    {
        View = view ?? throw new ArgumentNullException(nameof(view));
        Camera = camera;

        View.PointerPressed += OnPointerPressed;
        View.PointerMoved += OnPointerMoved;
        View.PointerReleased += OnPointerReleased;
        View.KeyDown += OnKeyDown;
        View.KeyUp += OnKeyUp;
        View.Frame += OnFrame;
        // Without focus the view never sees a key, and a camera that only
        // responds after a click is a bug report waiting to happen.
        View.Focusable = true;
    }

    protected ThreeNetView View { get; }

    protected Node Camera { get; }

    /// <summary>Where the camera is, in world units.</summary>
    public Vector3 Position { get; set; }

    /// <summary>Horizontal angle in radians.</summary>
    public float Yaw { get; set; }

    /// <summary>Vertical angle in radians.</summary>
    public float Pitch { get; set; }

    /// <summary>Metres per second.</summary>
    public float Speed { get; set; } = 6f;

    /// <summary>What holding shift multiplies the speed by.</summary>
    public float SprintMultiplier { get; set; } = 3f;

    /// <summary>Radians of turn per pixel of mouse travel.</summary>
    public float LookSpeed { get; set; } = 0.004f;

    /// <summary>
    /// Look without holding a button. Avalonia has no pointer lock, so the
    /// pointer still leaves the view at the edge of the window - which is why
    /// this is off by default and dragging is the reliable way to look.
    /// </summary>
    public bool LookWithoutDragging { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Points the camera at a spot, setting yaw and pitch to match.</summary>
    public void LookAt(Vector3 target)
    {
        Vector3 offset = target - Position;
        if (offset.LengthSquared() < 1e-8f)
        {
            return;
        }

        Yaw = MathF.Atan2(offset.X, -offset.Z);
        Pitch = MathF.Asin(Math.Clamp(Vector3.Normalize(offset).Y, -1f, 1f));
        Apply();
    }

    /// <summary>Writes the current state onto the camera node.</summary>
    public void Apply()
    {
        Camera.Position = Position;
        Camera.Rotation = Quaternion.CreateFromYawPitchRoll(Yaw, Pitch, 0f);
    }

    /// <summary>True while the key is held, for a subclass to read.</summary>
    protected bool IsHeld(Key key) => _held.Contains(key);

    /// <summary>The movement the keyboard is asking for, in camera space.</summary>
    protected Vector3 KeyboardMotion()
    {
        Vector3 motion = Vector3.Zero;
        if (IsHeld(Key.W) || IsHeld(Key.Up))
        {
            motion.Z -= 1f;
        }

        if (IsHeld(Key.S) || IsHeld(Key.Down))
        {
            motion.Z += 1f;
        }

        if (IsHeld(Key.A) || IsHeld(Key.Left))
        {
            motion.X -= 1f;
        }

        if (IsHeld(Key.D) || IsHeld(Key.Right))
        {
            motion.X += 1f;
        }

        if (IsHeld(Key.E) || IsHeld(Key.Space))
        {
            motion.Y += 1f;
        }

        if (IsHeld(Key.Q) || IsHeld(Key.LeftCtrl))
        {
            motion.Y -= 1f;
        }

        return motion;
    }

    /// <summary>Current speed, sprint included.</summary>
    protected float CurrentSpeed =>
        Speed * (IsHeld(Key.LeftShift) || IsHeld(Key.RightShift) ? SprintMultiplier : 1f);

    /// <summary>Moves the camera for one frame. Subclasses decide how.</summary>
    protected abstract void Step(float deltaSeconds);

    private void OnFrame(object? sender, FrameEventArgs e)
    {
        if (!Enabled)
        {
            return;
        }

        Step(e.DeltaSeconds);
        Apply();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        View.Focus();
        _looking = true;
        _lastPointer = e.GetPosition(View);
        e.Pointer.Capture(View);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!Enabled)
        {
            return;
        }

        Point position = e.GetPosition(View);
        if (!_looking && !LookWithoutDragging)
        {
            _lastPointer = position;
            return;
        }

        if (_lastPointer is { } last)
        {
            Yaw -= (float)(position.X - last.X) * LookSpeed;
            Pitch -= (float)(position.Y - last.Y) * LookSpeed;
            // Just short of straight up and straight down: at the poles the yaw
            // becomes meaningless and the view rolls.
            Pitch = Math.Clamp(Pitch, -1.5533f, 1.5533f);
            Apply();
        }

        _lastPointer = position;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _looking = false;
        e.Pointer.Capture(null);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e) => _held.Add(e.Key);

    private void OnKeyUp(object? sender, KeyEventArgs e) => _held.Remove(e.Key);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        View.PointerPressed -= OnPointerPressed;
        View.PointerMoved -= OnPointerMoved;
        View.PointerReleased -= OnPointerReleased;
        View.KeyDown -= OnKeyDown;
        View.KeyUp -= OnKeyUp;
        View.Frame -= OnFrame;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Free flight: WASD moves along the way the camera is pointing, E and Q lift
/// and drop, drag looks. Modelled on the Three.js <c>FlyControls</c>.
/// </summary>
public sealed class FlyController : LookController
{
    public FlyController(ThreeNetView view, Node camera)
        : base(view, camera)
    {
    }

    /// <summary>
    /// How much of the previous frame's velocity survives each second. 0 stops
    /// dead when the key is released; near 1 keeps drifting.
    /// </summary>
    public float Damping { get; set; } = 0.0001f;

    private Vector3 _velocity;

    protected override void Step(float deltaSeconds)
    {
        Vector3 wanted = KeyboardMotion();
        if (wanted.LengthSquared() > 0f)
        {
            wanted = Vector3.Normalize(wanted) * CurrentSpeed;
        }

        // Exponential damping, so the feel does not change with the frame rate.
        float keep = MathF.Pow(Math.Clamp(Damping, 0f, 0.9999f), deltaSeconds);
        _velocity = Vector3.Lerp(wanted, _velocity, keep);

        Quaternion orientation = Quaternion.CreateFromYawPitchRoll(Yaw, Pitch, 0f);
        Position += Vector3.Transform(_velocity, orientation) * deltaSeconds;
    }
}

/// <summary>
/// Walking: the same keys as <see cref="FlyController"/>, but movement stays in
/// the horizontal plane however far the camera is looking up or down, and the
/// eye stays at a fixed height. Modelled on <c>FirstPersonControls</c>.
/// </summary>
public sealed class FirstPersonController : LookController
{
    public FirstPersonController(ThreeNetView view, Node camera)
        : base(view, camera)
    {
        Speed = 4f;
    }

    /// <summary>Height of the eye above the ground, in metres.</summary>
    public float EyeHeight { get; set; } = 1.7f;

    /// <summary>
    /// Ground height under the camera. Hand it a terrain sampler and the walker
    /// follows the ground; leave it null and the ground is flat at zero.
    /// </summary>
    public Func<float, float, float>? GroundHeight { get; set; }

    protected override void Step(float deltaSeconds)
    {
        Vector3 wanted = KeyboardMotion();
        // Up and down are the ground's business here, not the keyboard's.
        wanted.Y = 0f;
        if (wanted.LengthSquared() > 0f)
        {
            wanted = Vector3.Normalize(wanted) * CurrentSpeed;
        }

        // Yaw only: looking at the sky must not slow the walk.
        Quaternion heading = Quaternion.CreateFromYawPitchRoll(Yaw, 0f, 0f);
        Vector3 position = Position + (Vector3.Transform(wanted, heading) * deltaSeconds);
        position.Y = (GroundHeight?.Invoke(position.X, position.Z) ?? 0f) + EyeHeight;
        Position = position;
    }
}

/// <summary>
/// Trackball: the scene turns as though it were a ball under the pointer, with
/// no up axis to stay upright against, so it can be rolled right over.
/// Modelled on the Three.js <c>TrackballControls</c>.
/// </summary>
public sealed class TrackballController : IDisposable
{
    private readonly ThreeNetView _view;
    private readonly Node _camera;
    private Point? _lastPointer;
    private bool _panning;
    private bool _disposed;

    public TrackballController(ThreeNetView view, Node camera)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _camera = camera;

        _view.PointerPressed += OnPointerPressed;
        _view.PointerMoved += OnPointerMoved;
        _view.PointerReleased += OnPointerReleased;
        _view.PointerWheelChanged += OnPointerWheelChanged;
        Apply();
    }

    /// <summary>The point the ball turns around.</summary>
    public Vector3 Target { get; set; } = Vector3.Zero;

    /// <summary>Distance from the target.</summary>
    public float Distance { get; set; } = 6f;

    public float MinDistance { get; set; } = 0.5f;

    public float MaxDistance { get; set; } = 500f;

    /// <summary>
    /// The full orientation, which is what lets a trackball roll past the poles
    /// where a yaw and pitch pair would gimbal.
    /// </summary>
    public Quaternion Orientation { get; set; } = Quaternion.Identity;

    public float RotateSpeed { get; set; } = 0.006f;

    public float PanSpeed { get; set; } = 0.0025f;

    public float ZoomSpeed { get; set; } = 0.12f;

    public bool Enabled { get; set; } = true;

    /// <summary>Writes the orientation and distance onto the camera node.</summary>
    public void Apply()
    {
        Vector3 back = Vector3.Transform(Vector3.UnitZ, Orientation);
        _camera.Position = Target + (back * Distance);
        _camera.Rotation = Orientation;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPointProperties properties = e.GetCurrentPoint(_view).Properties;
        _panning = properties.IsRightButtonPressed || properties.IsMiddleButtonPressed;
        _lastPointer = e.GetPosition(_view);
        e.Pointer.Capture(_view);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!Enabled || _lastPointer is not { } last)
        {
            return;
        }

        Point position = e.GetPosition(_view);
        float dx = (float)(position.X - last.X);
        float dy = (float)(position.Y - last.Y);
        _lastPointer = position;

        if (_panning)
        {
            Vector3 right = Vector3.Transform(Vector3.UnitX, Orientation);
            Vector3 up = Vector3.Transform(Vector3.UnitY, Orientation);
            Target += ((right * -dx) + (up * dy)) * PanSpeed * Distance;
        }
        else
        {
            // Turn about the axis perpendicular to the drag, in camera space:
            // dragging right spins about the camera's own up axis.
            Vector3 axis = new(-dy, -dx, 0f);
            float length = axis.Length();
            if (length > 1e-5f)
            {
                Quaternion turn = Quaternion.CreateFromAxisAngle(axis / length, length * RotateSpeed);
                // Applied on the camera's own side, so it compounds freely and
                // never snaps back to an up vector.
                Orientation = Quaternion.Normalize(Orientation * turn);
            }
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
        if (!Enabled)
        {
            return;
        }

        Distance = Math.Clamp(Distance * MathF.Exp((float)-e.Delta.Y * ZoomSpeed), MinDistance, MaxDistance);
        Apply();
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
        GC.SuppressFinalize(this);
    }
}
