using System.Numerics;
using ThreeNet.Interop;

namespace ThreeNet;

/// <summary>Gamepad buttons in the standard (Xbox-style) layout.</summary>
[Flags]
public enum GamepadButton : uint
{
    None = 0,
    /// <summary>A (Xbox) / Cross (PlayStation).</summary>
    South = 1 << 0,
    /// <summary>B / Circle.</summary>
    East = 1 << 1,
    /// <summary>X / Square.</summary>
    West = 1 << 2,
    /// <summary>Y / Triangle.</summary>
    North = 1 << 3,
    LeftBumper = 1 << 4,
    RightBumper = 1 << 5,
    Select = 1 << 6,
    Start = 1 << 7,
    Guide = 1 << 8,
    LeftStick = 1 << 9,
    RightStick = 1 << 10,
    DPadUp = 1 << 11,
    DPadDown = 1 << 12,
    DPadLeft = 1 << 13,
    DPadRight = 1 << 14,
    /// <summary>Set when the analog trigger passes <see cref="Gamepads.TriggerThreshold"/>.</summary>
    LeftTrigger = 1 << 15,
    RightTrigger = 1 << 16,
    A = South,
    B = East,
    X = West,
    Y = North,
}

/// <summary>Snapshot of one controller after <see cref="Gamepads.Update"/>.</summary>
public readonly record struct GamepadState(
    int Slot,
    bool IsConnected,
    bool IsVirtual,
    string Name,
    GamepadButton Buttons,
    GamepadButton PreviousButtons,
    Vector2 LeftStick,
    Vector2 RightStick,
    float LeftTrigger,
    float RightTrigger)
{
    public bool IsDown(GamepadButton button) => (Buttons & button) == button && button != GamepadButton.None;

    /// <summary>Went down since the previous update.</summary>
    public bool WasPressed(GamepadButton button) => IsDown(button) && (PreviousButtons & button) != button;

    /// <summary>Went up since the previous update.</summary>
    public bool WasReleased(GamepadButton button) => !IsDown(button) && (PreviousButtons & button) == button && button != GamepadButton.None;
}

/// <summary>
/// Gamepad input (XInput / Windows.Gaming.Input on Windows, evdev on Linux,
/// IOKit on macOS). Call <see cref="Update"/> once per frame, then read
/// <see cref="Connected"/> or a slot. Controllers keep their slot while the
/// process runs. Virtual pads (tests, on-screen controls, replays) can be
/// written with <see cref="SetVirtual"/> after <see cref="Update"/>.
/// </summary>
public sealed class Gamepads : IDisposable
{
    private nint _handle;
    private readonly HashSet<int> _connected = [];
    private float _deadZone = 0.12f;
    private float _triggerThreshold = 0.35f;

    public Gamepads()
    {
        _handle = NativeMethods.tn_gamepads_create();
        if (_handle == nint.Zero)
        {
            throw new ThreeNetException("failed to create the gamepad context");
        }
    }

    /// <summary>Why the platform backend is unavailable, or null when gamepads work.</summary>
    public unsafe string? PlatformError
    {
        get
        {
            string message = NativeError.ReadString((buffer, capacity) => NativeMethods.tn_gamepads_get_error(Handle, (byte*)buffer, capacity));
            return message.Length == 0 ? null : message;
        }
    }

    /// <summary>Radial stick dead zone (default 0.12).</summary>
    public float DeadZone
    {
        get => _deadZone;
        set
        {
            _deadZone = value;
            NativeError.Check(NativeMethods.tn_gamepads_configure(Handle, _deadZone, _triggerThreshold));
        }
    }

    /// <summary>Analog trigger value that sets the digital trigger button (default 0.35).</summary>
    public float TriggerThreshold
    {
        get => _triggerThreshold;
        set
        {
            _triggerThreshold = value;
            NativeError.Check(NativeMethods.tn_gamepads_configure(Handle, _deadZone, _triggerThreshold));
        }
    }

    /// <summary>Number of slots ever used (connected or not).</summary>
    public int SlotCount => NativeMethods.tn_gamepads_slot_count(Handle);

    public event Action<GamepadState>? ControllerConnected;

    public event Action<GamepadState>? ControllerDisconnected;

    /// <summary>Polls the platform. Returns the number of connected controllers.</summary>
    public int Update()
    {
        int count = NativeMethods.tn_gamepads_update(Handle);
        NativeError.Check(count);
        RaiseConnectionEvents();
        return count;
    }

    public GamepadState this[int slot] => Get(slot);

    /// <summary>Connected controllers, physical and virtual.</summary>
    public IReadOnlyList<GamepadState> Connected
    {
        get
        {
            List<GamepadState> states = [];
            for (int slot = 0; slot < SlotCount; slot++)
            {
                GamepadState state = Get(slot);
                if (state.IsConnected)
                {
                    states.Add(state);
                }
            }

            return states;
        }
    }

    public unsafe GamepadState Get(int slot)
    {
        NativeError.Check(NativeMethods.tn_gamepad_get_state(Handle, (uint)slot, out NativeGamepadState n));
        string name = NativeError.ReadString((buffer, capacity) => NativeMethods.tn_gamepad_get_name(Handle, (uint)slot, (byte*)buffer, capacity));
        return new GamepadState(
            slot,
            n.Connected != 0,
            n.IsVirtual != 0,
            name,
            (GamepadButton)n.Buttons,
            (GamepadButton)n.PreviousButtons,
            new Vector2(n.LeftX, n.LeftY),
            new Vector2(n.RightX, n.RightY),
            n.LeftTrigger,
            n.RightTrigger);
    }

    /// <summary>
    /// Writes a virtual controller into <paramref name="slot"/> (an existing
    /// virtual slot or <see cref="SlotCount"/> for a new one).
    /// </summary>
    public void SetVirtual(int slot, GamepadButton buttons, Vector2 leftStick = default, Vector2 rightStick = default,
        float leftTrigger = 0f, float rightTrigger = 0f, bool connected = true, string name = "Virtual gamepad")
    {
        NativeGamepadState state = new()
        {
            Connected = connected ? 1 : 0,
            IsVirtual = 1,
            Buttons = (uint)buttons,
            LeftX = leftStick.X,
            LeftY = leftStick.Y,
            RightX = rightStick.X,
            RightY = rightStick.Y,
            LeftTrigger = leftTrigger,
            RightTrigger = rightTrigger,
        };
        NativeError.Check(NativeMethods.tn_gamepad_set_virtual(Handle, (uint)slot, in state, name));
    }

    /// <summary>Vibrates a physical controller. Returns false when it has no force feedback.</summary>
    public bool Rumble(int slot, float strongMotor, float weakMotor, TimeSpan duration) =>
        NativeMethods.tn_gamepad_rumble(Handle, (uint)slot, strongMotor, weakMotor, (uint)Math.Clamp(duration.TotalMilliseconds, 1, uint.MaxValue)) == 1;

    private void RaiseConnectionEvents()
    {
        for (int slot = 0; slot < SlotCount; slot++)
        {
            GamepadState state = Get(slot);
            if (state.IsConnected && _connected.Add(slot))
            {
                ControllerConnected?.Invoke(state);
            }
            else if (!state.IsConnected && _connected.Remove(slot))
            {
                ControllerDisconnected?.Invoke(state);
            }
        }
    }

    private nint Handle => _handle != nint.Zero ? _handle : throw new ObjectDisposedException(nameof(Gamepads));

    public void Dispose()
    {
        if (_handle != nint.Zero)
        {
            NativeMethods.tn_gamepads_destroy(_handle);
            _handle = nint.Zero;
        }
    }
}
