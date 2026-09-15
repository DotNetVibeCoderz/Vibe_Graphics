using ThreeNet.Interop;

namespace ThreeNet;

/// <summary>Renderer settings; changing them takes effect on the next frame.</summary>
public struct RendererOptions
{
    public int Width;
    public int Height;
    public bool VSync;
    /// <summary>1, 2, 4 or 8; clamped to what the adapter supports.</summary>
    public int MsaaSamples;
    public float Exposure;
    public ToneMapping ToneMapping;
    public bool Bloom;
    public float BloomIntensity;
    public float BloomThreshold;
    public bool FrustumCulling;
    public PowerPreference PowerPreference;
    /// <summary>
    /// Offscreen renderers only: read pixels back as BGRA instead of RGBA,
    /// which is the layout UI toolkit bitmaps expect.
    /// </summary>
    public bool BgraOutput;

    /// <summary>Balanced defaults: 720p, vsync on, 4x MSAA, ACES tone mapping.</summary>
    public static RendererOptions Default => new();

    public RendererOptions()
    {
        Width = 1280;
        Height = 720;
        VSync = true;
        MsaaSamples = 4;
        Exposure = 1f;
        ToneMapping = ToneMapping.Aces;
        Bloom = false;
        BloomIntensity = 0.6f;
        BloomThreshold = 1f;
        FrustumCulling = true;
        PowerPreference = PowerPreference.HighPerformance;
        BgraOutput = false;
    }

    internal NativeRendererDesc ToNative() => new()
    {
        Width = (uint)Math.Max(1, Width),
        Height = (uint)Math.Max(1, Height),
        VSync = VSync ? 1 : 0,
        MsaaSamples = (uint)MsaaSamples,
        Exposure = Exposure,
        ToneMapping = (uint)ToneMapping,
        Bloom = Bloom ? 1 : 0,
        BloomIntensity = BloomIntensity,
        BloomThreshold = BloomThreshold,
        FrustumCulling = FrustumCulling ? 1 : 0,
        PowerPreference = (uint)PowerPreference,
        BgraOutput = BgraOutput ? 1 : 0,
    };
}

/// <summary>Counters collected while rendering the last frame.</summary>
public readonly record struct FrameStats(
    int DrawCalls,
    int Triangles,
    int VisibleNodes,
    int CulledNodes,
    int Lights,
    float CpuTimeMs);

/// <summary>
/// Draws a <see cref="Scene"/>. A renderer either owns a swap chain for a
/// platform window, or renders offscreen into a texture that can be read back
/// with <see cref="ReadPixels(Span{byte})"/>.
/// </summary>
public sealed class Renderer : IDisposable
{
    private nint _handle;
    private RendererOptions _options;

    private Renderer(nint handle, RendererOptions options)
    {
        _handle = handle;
        _options = options;
    }

    internal nint Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);
            return _handle;
        }
    }

    /// <summary>Width of the render target in pixels.</summary>
    public int Width => _options.Width;

    /// <summary>Height of the render target in pixels.</summary>
    public int Height => _options.Height;

    /// <summary>Width divided by height, guarded against a zero height.</summary>
    public float AspectRatio => Width / (float)Math.Max(1, Height);

    /// <summary>True when the renderer has no swap chain and draws into a texture.</summary>
    public bool IsOffscreen { get; private init; }

    /// <summary>Name, kind and backend of the GPU in use.</summary>
    public unsafe string AdapterName => NativeError.ReadString((buffer, capacity) =>
        NativeMethods.tn_renderer_get_adapter_name(Handle, (byte*)buffer, capacity));

    /// <summary>Current settings; assigning applies them.</summary>
    public RendererOptions Options
    {
        get => _options;
        set
        {
            _options = value;
            NativeRendererDesc desc = value.ToNative();
            NativeError.Check(NativeMethods.tn_renderer_set_config(Handle, in desc));
        }
    }

    /// <summary>Statistics for the most recently rendered frame.</summary>
    public FrameStats Stats
    {
        get
        {
            NativeError.Check(NativeMethods.tn_renderer_get_stats(Handle, out NativeFrameStats stats));
            return new FrameStats(
                (int)stats.DrawCalls,
                (int)stats.Triangles,
                (int)stats.VisibleNodes,
                (int)stats.CulledNodes,
                (int)stats.Lights,
                stats.CpuTimeMs);
        }
    }

    /// <summary>Creates a renderer that draws into an offscreen texture.</summary>
    public static Renderer CreateOffscreen(RendererOptions options)
    {
        NativeRendererDesc desc = options.ToNative();
        nint handle = NativeMethods.tn_renderer_create_offscreen(in desc);
        if (handle == nint.Zero)
        {
            throw new ThreeNetException($"failed to create the offscreen renderer: {NativeError.GetLastMessage()}");
        }

        return new Renderer(handle, options) { IsOffscreen = true };
    }

    /// <summary>Creates a renderer for a Win32 window (Avalonia, WPF, WinForms).</summary>
    public static Renderer CreateForWin32(nint hwnd, RendererOptions options, nint hinstance = default)
    {
        NativeRendererDesc desc = options.ToNative();
        nint handle = NativeMethods.tn_renderer_create_win32(hwnd, hinstance, in desc);
        if (handle == nint.Zero)
        {
            throw new ThreeNetException($"failed to create the Win32 renderer: {NativeError.GetLastMessage()}");
        }

        return new Renderer(handle, options);
    }

    /// <summary>Creates a renderer for an X11 window.</summary>
    public static Renderer CreateForX11(ulong window, nint display, int screen, RendererOptions options)
    {
        NativeRendererDesc desc = options.ToNative();
        nint handle = NativeMethods.tn_renderer_create_xlib(window, display, screen, in desc);
        if (handle == nint.Zero)
        {
            throw new ThreeNetException($"failed to create the X11 renderer: {NativeError.GetLastMessage()}");
        }

        return new Renderer(handle, options);
    }

    /// <summary>Creates a renderer for a macOS NSView.</summary>
    public static Renderer CreateForAppKit(nint nsView, RendererOptions options)
    {
        NativeRendererDesc desc = options.ToNative();
        nint handle = NativeMethods.tn_renderer_create_appkit(nsView, in desc);
        if (handle == nint.Zero)
        {
            throw new ThreeNetException($"failed to create the AppKit renderer: {NativeError.GetLastMessage()}");
        }

        return new Renderer(handle, options);
    }

    /// <summary>Wraps a renderer handle owned by the native window loop.</summary>
    internal static Renderer FromBorrowedHandle(nint handle, RendererOptions options) =>
        new(handle, options) { _borrowed = true };

    private bool _borrowed;

    /// <summary>Resizes the swap chain and the internal render targets.</summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        NativeError.Check(NativeMethods.tn_renderer_resize(Handle, (uint)width, (uint)height));
        _options.Width = width;
        _options.Height = height;
    }

    /// <summary>
    /// Records a size change that the native side has already applied, without
    /// reconfiguring the swap chain again.
    /// </summary>
    internal void NotifyResized(int width, int height)
    {
        _options.Width = Math.Max(1, width);
        _options.Height = Math.Max(1, height);
    }

    /// <summary>Renders the scene from the scene active camera.</summary>
    public void Render(Scene scene) => Render(scene, null);

    /// <summary>Renders the scene from <paramref name="camera"/>.</summary>
    public void Render(Scene scene, Node? camera)
    {
        ArgumentNullException.ThrowIfNull(scene);
        NativeError.Check(NativeMethods.tn_renderer_render(Handle, scene.Handle, camera?.Id ?? 0));
    }

    /// <summary>Number of bytes <see cref="ReadPixels(Span{byte})"/> needs.</summary>
    public int PixelBufferSize => Width * Height * 4;

    /// <summary>
    /// Copies the offscreen framebuffer into <paramref name="destination"/> as
    /// tightly packed RGBA8 rows, top row first.
    /// </summary>
    public unsafe int ReadPixels(Span<byte> destination)
    {
        if (!IsOffscreen)
        {
            throw new InvalidOperationException("only offscreen renderers can read pixels back");
        }

        if (destination.Length < PixelBufferSize)
        {
            throw new ArgumentException(
                $"the buffer needs at least {PixelBufferSize} bytes, got {destination.Length}", nameof(destination));
        }

        fixed (byte* pointer = destination)
        {
            int written = NativeMethods.tn_renderer_read_pixels(Handle, pointer, (uint)destination.Length);
            NativeError.Check(written);
            return written;
        }
    }

    /// <summary>Allocates a buffer and reads the offscreen framebuffer into it.</summary>
    public byte[] ReadPixels()
    {
        byte[] pixels = new byte[PixelBufferSize];
        ReadPixels(pixels);
        return pixels;
    }

    public void Dispose()
    {
        if (_handle == nint.Zero)
        {
            return;
        }

        // Handles owned by the native window loop are freed by that loop.
        if (!_borrowed)
        {
            NativeMethods.tn_renderer_destroy(_handle);
        }

        _handle = nint.Zero;
        GC.SuppressFinalize(this);
    }

    ~Renderer() => Dispose();
}
