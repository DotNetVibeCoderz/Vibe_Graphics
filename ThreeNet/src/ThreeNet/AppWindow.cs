using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using ThreeNet.Interop;

namespace ThreeNet;

/// <summary>Window settings for <see cref="AppWindow"/>.</summary>
public struct WindowOptions
{
    public string Title;
    public int Width;
    public int Height;
    public bool Resizable;
    public bool Decorations;
    public RendererOptions Renderer;

    /// <summary>A resizable 720p window using the default renderer settings.</summary>
    public static WindowOptions Default => new();

    public WindowOptions()
    {
        Title = "Three.Net";
        Width = 1280;
        Height = 720;
        Resizable = true;
        Decorations = true;
        Renderer = RendererOptions.Default;
    }
}

/// <summary>
/// A native window with a render loop, for standalone samples and tools.
/// Embedded hosts (Avalonia, WPF, WinForms) create a <see cref="Renderer"/>
/// from their own window handle instead.
/// </summary>
/// <remarks>
/// <see cref="Run"/> blocks until the window closes and must be called from the
/// process main thread on Windows and macOS.
/// </remarks>
public sealed class AppWindow : IDisposable
{
    private readonly WindowOptions _options;
    private GCHandle _self;
    private Renderer? _renderer;

    public AppWindow(WindowOptions options)
    {
        _options = options;
    }

    /// <summary>Raised once, after the window and the renderer exist.</summary>
    public event Action<Renderer>? Load;

    /// <summary>Raised every frame with the elapsed seconds since the previous one.</summary>
    public event Action<Renderer, float>? Render;

    /// <summary>Raised for keyboard, mouse, touch and window events.</summary>
    public event Action<Renderer, InputEvent>? Input;

    /// <summary>Raised on a close request; set <c>Cancel</c> to keep the window open.</summary>
    public event Action<CloseRequest>? Closing;

    /// <summary>The renderer bound to this window, available after <see cref="Load"/>.</summary>
    public Renderer? Renderer => _renderer;

    /// <summary>Opens the window and runs the loop until it is closed.</summary>
    /// <remarks>
    /// On Windows the loop needs a single threaded apartment (the window
    /// manager calls <c>OleInitialize</c> for drag and drop). A .NET main
    /// thread is MTA by default, so the loop is moved onto a dedicated STA
    /// thread and this call simply waits for it.
    /// </remarks>
    public void Run()
    {
        if (OperatingSystem.IsWindows() && Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            ExceptionDispatchInfo? failure = null;
            Thread thread = new(() =>
            {
                try
                {
                    RunCore();
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
            })
            {
                Name = "Three.Net window",
                IsBackground = false,
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            failure?.Throw();
            return;
        }

        RunCore();
    }

    private unsafe void RunCore()
    {
        _self = GCHandle.Alloc(this);
        try
        {
            byte[] title = Encoding.UTF8.GetBytes(_options.Title + '\0');
            fixed (byte* titlePointer = title)
            {
                NativeWindowDesc desc = new()
                {
                    Title = titlePointer,
                    Width = (uint)Math.Max(1, _options.Width),
                    Height = (uint)Math.Max(1, _options.Height),
                    Resizable = _options.Resizable ? 1 : 0,
                    Decorations = _options.Decorations ? 1 : 0,
                    Renderer = _options.Renderer.ToNative(),
                };
                NativeAppCallbacks callbacks = new()
                {
                    UserData = GCHandle.ToIntPtr(_self),
                    OnInit = &OnInitCallback,
                    OnFrame = &OnFrameCallback,
                    OnEvent = &OnEventCallback,
                    OnClose = &OnCloseCallback,
                };
                NativeError.Check(NativeMethods.tn_app_run(&desc, callbacks));
            }
        }
        finally
        {
            _renderer?.Dispose();
            _renderer = null;
            if (_self.IsAllocated)
            {
                _self.Free();
            }
        }
    }

    private static AppWindow? FromUserData(nint userData) =>
        userData == nint.Zero ? null : GCHandle.FromIntPtr(userData).Target as AppWindow;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnInitCallback(nint userData, nint renderer)
    {
        AppWindow? window = FromUserData(userData);
        if (window is null)
        {
            return;
        }

        try
        {
            // The loop owns the handle, so the wrapper must not free it.
            window._renderer = Renderer.FromBorrowedHandle(renderer, window._options.Renderer);
            window.Load?.Invoke(window._renderer);
        }
        catch (Exception exception)
        {
            // Exceptions must not unwind into Rust.
            ReportUnhandled(exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnFrameCallback(nint userData, nint renderer, float deltaSeconds)
    {
        AppWindow? window = FromUserData(userData);
        if (window?._renderer is not { } target)
        {
            return;
        }

        try
        {
            window.Render?.Invoke(target, deltaSeconds);
        }
        catch (Exception exception)
        {
            ReportUnhandled(exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnEventCallback(nint userData, nint renderer, NativeInputEvent* nativeEvent)
    {
        AppWindow? window = FromUserData(userData);
        if (window?._renderer is not { } target || nativeEvent is null)
        {
            return;
        }

        try
        {
            InputEvent managed = new(in *nativeEvent);
            if (managed.Kind == InputEventKind.Resized)
            {
                // The native loop already resized the swap chain; this only
                // keeps the managed copy of the size in sync.
                (int width, int height) = managed.Size;
                target.NotifyResized(width, height);
            }

            window.Input?.Invoke(target, managed);
        }
        catch (Exception exception)
        {
            ReportUnhandled(exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int OnCloseCallback(nint userData)
    {
        AppWindow? window = FromUserData(userData);
        if (window is null)
        {
            return 1;
        }

        try
        {
            CloseRequest request = new();
            window.Closing?.Invoke(request);
            return request.Cancel ? 0 : 1;
        }
        catch (Exception exception)
        {
            ReportUnhandled(exception);
            return 1;
        }
    }

    private static void ReportUnhandled(Exception exception) =>
        Console.Error.WriteLine($"[Three.Net] unhandled exception in a window callback: {exception}");

    public void Dispose()
    {
        _renderer?.Dispose();
        _renderer = null;
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }
}

/// <summary>Lets a <see cref="AppWindow.Closing"/> handler veto the close.</summary>
public sealed class CloseRequest
{
    /// <summary>Set to true to keep the window open.</summary>
    public bool Cancel { get; set; }
}
