using System.Numerics;
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
    /// <summary>Enables shadow maps for lights with <see cref="Light.CastShadow"/> set.</summary>
    public bool Shadows;
    /// <summary>Resolution of each shadow map layer (256-8192).</summary>
    public int ShadowMapSize;
    /// <summary>How far from the camera directional shadows reach, in world units.</summary>
    public float ShadowDistance;
    /// <summary>Cascades per directional light (1-4); more cascades keep near shadows sharp.</summary>
    public int ShadowCascades;
    /// <summary>PCF filter radius in texels: 0 = hard, 1 = 3x3, 2 = 5x5, 3 = 7x7.</summary>
    public int ShadowSoftness;
    /// <summary>Screen space ambient occlusion: contact shadows in creases and corners.</summary>
    public bool Ssao;
    /// <summary>SSAO sampling radius in world units.</summary>
    public float SsaoRadius;
    /// <summary>Contrast exponent of the occlusion term.</summary>
    public float SsaoIntensity;
    /// <summary>Depth bias against self occlusion on flat surfaces.</summary>
    public float SsaoBias;
    /// <summary>Kernel samples per pixel (4-32).</summary>
    public int SsaoSamples;
    /// <summary>How much occlusion also darkens direct light (0 = ambient only, 1 = everything).</summary>
    public float SsaoDirectStrength;
    /// <summary>
    /// Screen space reflections: a wet floor, polished concrete, a pool of
    /// water picking up what is standing on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reflections are marched through the depth prepass, so turning this on
    /// turns that prepass on as well - the same one <see cref="Ssao"/> uses,
    /// and the cost is shared when both are on.
    /// </para>
    /// <para>
    /// It can only reflect what is already on screen. A ray that leaves the
    /// frame, or that passes behind something nearer, has nothing to return,
    /// so reflections fade towards the edges instead of ending abruptly. The
    /// weighting is Fresnel and <see cref="SsrIntensity"/>: the prepass carries
    /// geometry rather than material parameters, so roughness does not blur
    /// the reflection. For a correct reflection on one specific object, bake a
    /// cube probe with <see cref="Renderer.RenderToCube"/> instead.
    /// </para>
    /// </remarks>
    public bool Ssr;
    /// <summary>How much of the reflection to keep before the Fresnel weighting (0-1).</summary>
    public float SsrIntensity;
    /// <summary>How far a reflection ray may travel, in world units.</summary>
    public float SsrMaxDistance;
    /// <summary>
    /// How far behind a surface a ray may pass and still count as having hit
    /// it. Too small breaks reflections up; too large lets a ray latch onto a
    /// thin object it was only passing behind.
    /// </summary>
    public float SsrThickness;
    /// <summary>Coarse steps along the ray before the binary refinement (8-128).</summary>
    public int SsrSteps;
    /// <summary>
    /// A colour lookup table applied at the very end of the chain: the usual
    /// way a look is carried from a grading tool into a renderer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The table is an ordinary texture the scene owns, laid out as a strip -
    /// <c>size</c> slices of <c>size</c> by <c>size</c> side by side, so a 32
    /// step table is a 1024 by 32 image. The size is read from the texture
    /// height, so there is nothing to keep in step by hand. Build a neutral one
    /// with <see cref="Scene.CreateNeutralColorLut"/> and edit that, or export
    /// a strip from a grading tool.
    /// </para>
    /// <para>
    /// It runs after tone mapping, saturation and contrast, so a table authored
    /// against a screenshot sees what that screenshot saw. A debug view skips
    /// it, since a debug view is meant to be read rather than graded.
    /// </para>
    /// </remarks>
    public Texture? ColorLut;
    /// <summary>How much of the table to apply, 0 to 1.</summary>
    public float ColorLutStrength;
    /// <summary>Forward (default, supports MSAA) or deferred shading (cheaper with many lights).</summary>
    public RenderPath RenderPath;
    /// <summary>Bokeh depth of field around <see cref="DofFocusDistance"/>.</summary>
    public bool DepthOfField;
    /// <summary>Distance from the camera that stays sharp, in world units.</summary>
    public float DofFocusDistance;
    /// <summary>Depth band around the focus distance that stays sharp.</summary>
    public float DofFocusRange;
    /// <summary>Largest blur radius in pixels (at 1080p, scaled with the target height).</summary>
    public float DofMaxBlur;
    /// <summary>Camera motion blur reconstructed from depth and the previous frame's view.</summary>
    public bool MotionBlur;
    /// <summary>Fraction of the frame-to-frame motion that is smeared (1 = full shutter).</summary>
    public float MotionBlurStrength;
    /// <summary>Samples along the motion vector (4-32).</summary>
    public int MotionBlurSamples;
    /// <summary>
    /// Shows one channel of the surface instead of the shaded image. Exposure,
    /// tone mapping, bloom and the camera effects are skipped while a view is on.
    /// </summary>
    public DebugView DebugView;
    /// <summary>
    /// Draws every surface as lines whatever its material says. Ignored on
    /// adapters without wireframe rasterisation
    /// (<see cref="GpuCapabilities.WireframeRendering"/>).
    /// </summary>
    public bool Wireframe;

    /// <summary>
    /// Up to four world space planes that cut the scene away where a fragment
    /// falls behind them - a section through a model without opening the model.
    /// Each is <c>(normal.X, normal.Y, normal.Z, distance)</c> with a unit
    /// normal; a point is kept where <c>dot(normal, point) + distance >= 0</c>.
    /// </summary>
    /// <remarks>
    /// The prepass clips with the colour pass, so SSAO and depth of field never
    /// occlude against a surface nothing draws.
    /// </remarks>
    /// <summary>
    /// Fast approximate antialiasing, run on the finished image. This is the
    /// only antialiasing the deferred path can have: MSAA needs a multisampled
    /// target, and the G-buffer is resolved before the lighting ever sees it.
    /// Forward rendering keeps <see cref="MsaaSamples"/>; reach for this when
    /// that is not available or not enough.
    /// </summary>
    public bool Fxaa;

    /// <summary>
    /// Darkens the corners of the frame; 0 is off. Applied after tone mapping,
    /// because a vignette is something a lens does, not something the scene does.
    /// </summary>
    public float Vignette;
    /// <summary>How far in from the corner the darkening starts, 0 to 1.</summary>
    public float VignetteSoftness;
    /// <summary>Film grain; 0 is off. It moves with time, or it reads as dirt on the lens.</summary>
    public float Grain;
    /// <summary>1 leaves colour alone, 0 is greyscale, above 1 pushes it.</summary>
    public float Saturation;
    /// <summary>1 leaves contrast alone. Pivots on middle grey.</summary>
    public float Contrast;
    /// <summary>Added last; 0 leaves brightness alone.</summary>
    public float Brightness;

    public Vector4[]? ClipPlanes;

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
        Shadows = false;
        ShadowMapSize = 2048;
        ShadowDistance = 60f;
        ShadowCascades = 3;
        ShadowSoftness = 1;
        Ssao = false;
        SsaoRadius = 0.5f;
        SsaoIntensity = 1.5f;
        SsaoBias = 0.025f;
        SsaoSamples = 16;
        SsaoDirectStrength = 0.25f;
        Ssr = false;
        SsrIntensity = 1f;
        SsrMaxDistance = 12f;
        SsrThickness = 0.5f;
        SsrSteps = 32;
        ColorLut = null;
        ColorLutStrength = 1f;
        RenderPath = RenderPath.Forward;
        DepthOfField = false;
        DofFocusDistance = 10f;
        DofFocusRange = 4f;
        DofMaxBlur = 14f;
        MotionBlur = false;
        MotionBlurStrength = 0.6f;
        MotionBlurSamples = 12;
        DebugView = DebugView.Off;
        Wireframe = false;
        Fxaa = false;
        Vignette = 0f;
        VignetteSoftness = 0.5f;
        Grain = 0f;
        Saturation = 1f;
        Contrast = 1f;
        Brightness = 0f;
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
        Shadows = Shadows ? 1 : 0,
        ShadowMapSize = (uint)Math.Clamp(ShadowMapSize, 256, 8192),
        ShadowDistance = ShadowDistance,
        ShadowCascades = (uint)Math.Clamp(ShadowCascades, 1, 4),
        ShadowSoftness = (uint)Math.Clamp(ShadowSoftness, 0, 3),
        Ssao = Ssao ? 1 : 0,
        SsaoRadius = SsaoRadius,
        SsaoIntensity = SsaoIntensity,
        SsaoBias = SsaoBias,
        SsaoSamples = (uint)Math.Clamp(SsaoSamples, 4, 32),
        SsaoDirectStrength = SsaoDirectStrength,
        Ssr = Ssr ? 1 : 0,
        SsrIntensity = SsrIntensity,
        SsrMaxDistance = SsrMaxDistance,
        SsrThickness = SsrThickness,
        SsrSteps = (uint)Math.Clamp(SsrSteps, 8, 128),
        ColorLut = ColorLut?.Id ?? 0,
        ColorLutStrength = ColorLutStrength,
        RenderPath = (uint)RenderPath,
        DepthOfField = DepthOfField ? 1 : 0,
        DofFocusDistance = DofFocusDistance,
        DofFocusRange = DofFocusRange,
        DofMaxBlur = DofMaxBlur,
        MotionBlur = MotionBlur ? 1 : 0,
        MotionBlurStrength = MotionBlurStrength,
        MotionBlurSamples = (uint)Math.Clamp(MotionBlurSamples, 4, 32),
        DebugView = (uint)DebugView,
        Wireframe = Wireframe ? 1 : 0,
        Fxaa = Fxaa ? 1 : 0,
        Vignette = Vignette,
        VignetteSoftness = VignetteSoftness,
        Grain = Grain,
        Saturation = Saturation,
        Contrast = Contrast,
        Brightness = Brightness,
        ClipPlane0 = Plane(0),
        ClipPlane1 = Plane(1),
        ClipPlane2 = Plane(2),
        ClipPlane3 = Plane(3),
        ClipPlaneCount = (uint)Math.Clamp(ClipPlanes?.Length ?? 0, 0, 4),
    };

    private readonly Vector4 Plane(int index) =>
        ClipPlanes is not null && index < ClipPlanes.Length ? ClipPlanes[index] : Vector4.Zero;
}

/// <summary>Counters collected while rendering the last frame.</summary>
/// <param name="GpuTimeMs">
/// Time the GPU spent on the most recently measured frame. The measurement is
/// read back without stalling, so it can lag a frame or two behind, and it stays
/// 0 on adapters without timestamp queries
/// (<see cref="GpuCapabilities.TimestampQueries"/>).
/// </param>
public readonly record struct FrameStats(
    int DrawCalls,
    int Triangles,
    int VisibleNodes,
    int CulledNodes,
    int Lights,
    float CpuTimeMs,
    int ShadowLayers = 0,
    int ShadowDrawCalls = 0,
    float GpuTimeMs = 0f);

/// <summary>
/// What the GPU a renderer ended up on can do, so a host can offer only the
/// features that actually work there.
/// </summary>
/// <param name="Backend">Graphics API the renderer ended up on.</param>
/// <param name="DeviceType">Discrete, integrated, virtual or a software rasteriser.</param>
/// <param name="VendorId">PCI vendor id as the adapter reports it.</param>
/// <param name="DeviceId">PCI device id as the adapter reports it.</param>
/// <param name="MaxTextureSize">Largest 2D texture edge in pixels.</param>
/// <param name="MaxBufferSize">Largest single GPU buffer in bytes.</param>
/// <param name="MaxBindGroups">Bind groups a pipeline may use; the renderer needs four.</param>
/// <param name="MaxMsaaSamples">Highest MSAA sample count the HDR target supports here.</param>
/// <param name="TimestampQueries">GPU frame timing is available (see <see cref="FrameStats.GpuTimeMs"/>).</param>
/// <param name="TextureCompressionBc">BC / DXT / BPTC blocks upload without being decoded first.</param>
/// <param name="TextureCompressionEtc2">ETC2 blocks upload without being decoded first.</param>
/// <param name="TextureCompressionAstc">ASTC blocks upload without being decoded first.</param>
/// <param name="WireframeRendering">Wireframe rasterisation (<see cref="MaterialOptions.Wireframe"/>).</param>
/// <param name="Float32Filterable">32 bit float textures can be filtered, not only point sampled.</param>
public readonly record struct GpuCapabilities(
    GpuBackend Backend,
    GpuDeviceType DeviceType,
    uint VendorId,
    uint DeviceId,
    int MaxTextureSize,
    long MaxBufferSize,
    int MaxBindGroups,
    int MaxMsaaSamples,
    bool TimestampQueries,
    bool TextureCompressionBc,
    bool TextureCompressionEtc2,
    bool TextureCompressionAstc,
    bool WireframeRendering,
    bool Float32Filterable);

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
                stats.CpuTimeMs,
                (int)stats.ShadowLayers,
                (int)stats.ShadowDrawCalls,
                stats.GpuTimeMs);
        }
    }

    /// <summary>Driver name and version, empty when the adapter reports neither.</summary>
    public unsafe string AdapterDriver => NativeError.ReadString((buffer, capacity) =>
        NativeMethods.tn_renderer_get_adapter_driver(Handle, (byte*)buffer, capacity));

    /// <summary>Features and limits of the GPU this renderer is running on.</summary>
    public GpuCapabilities Capabilities
    {
        get
        {
            NativeError.Check(NativeMethods.tn_renderer_get_capabilities(Handle, out NativeCapabilities caps));
            return new GpuCapabilities(
                (GpuBackend)caps.Backend,
                (GpuDeviceType)caps.DeviceType,
                caps.VendorId,
                caps.DeviceId,
                (int)caps.MaxTextureSize,
                (long)caps.MaxBufferSize,
                (int)caps.MaxBindGroups,
                (int)caps.MaxMsaaSamples,
                caps.TimestampQueries != 0,
                caps.TextureCompressionBc != 0,
                caps.TextureCompressionEtc2 != 0,
                caps.TextureCompressionAstc != 0,
                caps.PolygonModeLine != 0,
                caps.Float32Filterable != 0);
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

    /// <summary>
    /// Creates a renderer that draws into an Android surface.
    /// </summary>
    /// <param name="nativeWindow">
    /// An <c>ANativeWindow*</c>, which a host gets from a <c>Surface</c> with
    /// <c>ANativeWindow_fromSurface</c> in <c>libandroid.so</c>. The caller keeps
    /// ownership: release it only after disposing the renderer.
    /// </param>
    public static Renderer CreateForAndroid(nint nativeWindow, RendererOptions options)
    {
        NativeRendererDesc desc = options.ToNative();
        nint handle = NativeMethods.tn_renderer_create_android(nativeWindow, in desc);
        if (handle == nint.Zero)
        {
            throw new ThreeNetException($"failed to create the Android renderer: {NativeError.GetLastMessage()}");
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

    /// <summary>
    /// True when this adapter can run compute shaders at all. WebGL2 cannot,
    /// and some downlevel adapters report that they cannot either.
    /// </summary>
    public bool SupportsCompute => NativeMethods.tn_renderer_supports_compute(Handle) != 0;

    /// <summary>
    /// Runs a compute kernel over a set of storage buffers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The buffers are bound in the order given. The counts are workgroups, not
    /// threads: a kernel declaring <c>@workgroup_size(64)</c> dispatched with
    /// 4 groups runs 256 invocations, so the usual shape is
    /// <c>(count + 63) / 64</c> groups and an early return in the kernel for
    /// the invocations past the end.
    /// </para>
    /// <para>
    /// Work is queued on the same device queue the frame uses, so whatever a
    /// kernel writes is visible to the next dispatch and to any pass that reads
    /// the buffer afterwards.
    /// </para>
    /// </remarks>
    public unsafe void Dispatch(
        Scene scene,
        ComputeKernel kernel,
        ReadOnlySpan<StorageBuffer> buffers,
        int groupsX,
        int groupsY = 1,
        int groupsZ = 1)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(kernel);

        Span<uint> ids = buffers.Length <= 8 ? stackalloc uint[buffers.Length] : new uint[buffers.Length];
        for (int i = 0; i < buffers.Length; i++)
        {
            ids[i] = buffers[i].Id;
        }

        fixed (uint* pointer = ids)
        {
            NativeError.Check(NativeMethods.tn_renderer_dispatch_compute(
                Handle, scene.Handle, kernel.Id, pointer, (uint)ids.Length,
                (uint)groupsX, (uint)groupsY, (uint)groupsZ));
        }
    }

    /// <summary>
    /// Runs a compute kernel that writes the vertices of a geometry, so what is
    /// drawn never has to travel through system memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The storage buffers bind first, in order, and the vertex buffer binds
    /// after them: a kernel handed one buffer sees its state at binding 0 and
    /// the vertices at binding 1.
    /// </para>
    /// <para>
    /// The kernel declares the vertex layout itself, and there is a trap in it.
    /// A vertex is 48 bytes - position, normal, uv, tangent - but WGSL aligns
    /// <c>vec3&lt;f32&gt;</c> to 16, so the struct that looks right has a
    /// stride of 64 and reads every vertex after the first from the wrong
    /// place. Declare the fields as scalars:
    /// </para>
    /// <code>
    /// struct Vertex {
    ///     px: f32, py: f32, pz: f32,
    ///     nx: f32, ny: f32, nz: f32,
    ///     u: f32, v: f32,
    ///     tx: f32, ty: f32, tz: f32, tw: f32,
    /// };
    /// </code>
    /// <para>
    /// Nothing may write the geometry from the CPU afterwards, or the upload
    /// overwrites what the kernel produced. Build it at full size once and
    /// leave it to the GPU.
    /// </para>
    /// </remarks>
    public unsafe void DispatchToGeometry(
        Scene scene,
        ComputeKernel kernel,
        ReadOnlySpan<StorageBuffer> buffers,
        Geometry geometry,
        int groupsX,
        int groupsY = 1,
        int groupsZ = 1)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(geometry);

        Span<uint> ids = buffers.Length <= 8 ? stackalloc uint[buffers.Length] : new uint[buffers.Length];
        for (int i = 0; i < buffers.Length; i++)
        {
            ids[i] = buffers[i].Id;
        }

        fixed (uint* pointer = ids)
        {
            NativeError.Check(NativeMethods.tn_renderer_dispatch_compute_to_geometry(
                Handle, scene.Handle, kernel.Id, pointer, (uint)ids.Length, geometry.Id,
                (uint)groupsX, (uint)groupsY, (uint)groupsZ));
        }
    }

    /// <summary>
    /// Reads a storage buffer back into system memory.
    /// </summary>
    /// <remarks>
    /// This waits for the queue to drain, so it belongs in a bake or a test
    /// rather than in a frame.
    /// </remarks>
    public unsafe T[] ReadStorageBuffer<T>(Scene scene, StorageBuffer buffer)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(buffer);
        long size = buffer.Size;
        T[] result = new T[size / sizeof(T)];
        fixed (T* pointer = result)
        {
            NativeError.Check(NativeMethods.tn_renderer_read_storage_buffer(
                Handle, scene.Handle, buffer.Id, (byte*)pointer, (ulong)size));
        }

        return result;
    }

    /// <summary>
    /// Bakes the light arriving at a point into a <see cref="ThreeNet.LightProbe"/>:
    /// six renders, projected onto spherical harmonics.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand the result to <see cref="Scene.LightProbe"/> and every surface gets
    /// ambient light that knows which way it faces. The renderer has to be
    /// offscreen and square, because the faces are read back through the
    /// ordinary readback path; a small one is plenty, since a three band fit
    /// cannot hold detail finer than a hemisphere anyway.
    /// </para>
    /// <para>
    /// Tone mapping, exposure and antialiasing are turned off for the capture
    /// and put back afterwards, so what is projected is radiance rather than a
    /// picture of it. The readback is still eight bits a channel, so anything
    /// brighter than white clips: an indoor probe is faithful, one staring into
    /// the sun records a sun no brighter than a sheet of paper.
    /// </para>
    /// </remarks>
    public unsafe LightProbe BakeLightProbe(Scene scene, Vector3 position)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Vector3[] coefficients = new Vector3[LightProbe.CoefficientCount];
        fixed (Vector3* pointer = coefficients)
        {
            NativeError.Check(NativeMethods.tn_renderer_bake_light_probe(
                Handle, scene.Handle, position, pointer));
        }

        return new LightProbe(coefficients);
    }

    /// <summary>
    /// Captures the surroundings of a point into a cube texture - a reflection
    /// probe, or the input to a light probe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Six renders, one per face, each with a ninety degree field of view. The
    /// texture must come from <see cref="Scene.CreateCubeRenderTarget"/>, and
    /// the renderer must be square and the same size as a face - the same
    /// restriction <see cref="RenderToTexture"/> carries, for the same reason.
    /// </para>
    /// <para>
    /// This is a bake rather than something to do every frame: it is six full
    /// frames of work. A probe sees the scene exactly as a camera standing
    /// there would.
    /// </para>
    /// </remarks>
    public void RenderToCube(Scene scene, Vector3 position, Texture target)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(target);
        NativeError.Check(NativeMethods.tn_renderer_render_to_cube(
            Handle, scene.Handle, position, target.Id));
    }

    /// <summary>
    /// Renders the scene into a texture that any material can then sample: a
    /// mirror, a portal, a security monitor, a minimap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The texture must come from <see cref="Scene.CreateRenderTarget"/> and
    /// must be the same size as this renderer. That restriction is what keeps
    /// it cheap: the shadow maps, the depth prepass, SSAO and the bloom chain
    /// all own buffers sized to the renderer, and a different target size would
    /// mean rebuilding every one of them twice a frame. Rendering at another
    /// size wants a second renderer.
    /// </para>
    /// <para>
    /// Nothing goes through the CPU - the result is a GPU texture the material
    /// bind groups reference directly. Render the reflection first, then the
    /// main view, or the mirror shows the frame before.
    /// </para>
    /// </remarks>
    public void RenderToTexture(Scene scene, Node? camera, Texture target)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(target);
        NativeError.Check(NativeMethods.tn_renderer_render_to_texture(
            Handle, scene.Handle, camera?.Id ?? 0, target.Id));
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
