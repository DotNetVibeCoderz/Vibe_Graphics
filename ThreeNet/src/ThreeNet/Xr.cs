using System.Numerics;
using ThreeNet.Interop;

namespace ThreeNet;

/// <summary>What <see cref="XrRuntime.Probe"/> found.</summary>
public sealed record XrRuntimeInfo(
    bool LoaderFound,
    bool RuntimeFound,
    bool HeadsetFound,
    string RuntimeName,
    string RuntimeVersion,
    string SystemName,
    uint VendorId,
    int RecommendedEyeWidth,
    int RecommendedEyeHeight,
    int ViewCount,
    bool OrientationTracking,
    bool PositionTracking,
    string Message);

/// <summary>OpenXR discovery (the loader is opened dynamically).</summary>
public static class XrRuntime
{
    /// <summary>
    /// Looks for the OpenXR loader, an installed runtime and a connected head
    /// mounted display, and reads the recommended per-eye resolution.
    /// Submitting frames to a headset is not implemented yet; render eyes with
    /// <see cref="StereoRig"/>.
    /// </summary>
    public static unsafe XrRuntimeInfo Probe()
    {
        NativeXrInfo info = default;
        string text = NativeError.ReadString((buffer, capacity) => NativeMethods.tn_xr_probe(out info, (byte*)buffer, capacity));
        string[] parts = text.Split('|');
        string Part(int index) => index < parts.Length ? parts[index] : string.Empty;
        return new XrRuntimeInfo(
            info.LoaderFound != 0,
            info.RuntimeFound != 0,
            info.HeadsetFound != 0,
            Part(0),
            Part(1),
            Part(2),
            info.VendorId,
            (int)info.RecommendedWidth,
            (int)info.RecommendedHeight,
            (int)info.ViewCount,
            info.OrientationTracking != 0,
            info.PositionTracking != 0,
            Part(3));
    }
}

/// <summary>
/// Two eye cameras under a head node with off-axis frusta that converge at a
/// focal distance (no toe-in, so no vertical parallax). Render each eye with
/// its own camera, or use <see cref="RenderSideBySide"/> for 3D displays and
/// previews.
/// </summary>
public sealed class StereoRig
{
    public StereoRig(Scene scene, Node head, float interpupillaryDistance = 0.064f, float verticalFieldOfView = 1.6f, float focalDistance = 2f)
    {
        Scene = scene;
        Head = head;
        LeftEye = scene.CreateNode(head, "left eye");
        RightEye = scene.CreateNode(head, "right eye");
        InterpupillaryDistance = interpupillaryDistance;
        VerticalFieldOfView = verticalFieldOfView;
        FocalDistance = focalDistance;
        Apply(1f);
    }

    public Scene Scene { get; }

    public Node Head { get; }

    public Node LeftEye { get; }

    public Node RightEye { get; }

    public float InterpupillaryDistance { get; set; }

    public float VerticalFieldOfView { get; set; }

    /// <summary>Distance at which both eyes' images coincide (zero parallax plane).</summary>
    public float FocalDistance { get; set; }

    public float Near { get; set; } = 0.05f;

    public float Far { get; set; } = 1000f;

    /// <summary>Recomputes eye offsets and frusta for an eye aspect ratio (width / height).</summary>
    public void Apply(float eyeAspect)
    {
        float halfIpd = InterpupillaryDistance * 0.5f;
        float tanY = MathF.Tan(VerticalFieldOfView * 0.5f);
        float tanX = tanY * eyeAspect;
        float focal = MathF.Max(FocalDistance, 0.01f);
        (Node node, float offset)[] eyes = [(LeftEye, -halfIpd), (RightEye, halfIpd)];
        foreach ((Node node, float offset) in eyes)
        {
            node.Position = new Vector3(offset, 0f, 0f);
            // Shift the frustum so the focal plane centre stays on the head axis.
            float shift = -offset / focal;
            node.Camera = Camera.OffAxis(MathF.Atan(-tanX + shift), MathF.Atan(tanX + shift), MathF.Atan(tanY), MathF.Atan(-tanY), Near, Far);
        }
    }

    /// <summary>
    /// Renders both eyes with <paramref name="eyeRenderer"/> (sized for one eye)
    /// and returns a side-by-side RGBA / BGRA image twice the renderer width.
    /// </summary>
    public byte[] RenderSideBySide(Renderer eyeRenderer)
    {
        Apply(eyeRenderer.AspectRatio);
        int width = eyeRenderer.Width;
        int height = eyeRenderer.Height;
        byte[] result = new byte[width * 2 * height * 4];
        eyeRenderer.Render(Scene, LeftEye);
        byte[] left = eyeRenderer.ReadPixels();
        eyeRenderer.Render(Scene, RightEye);
        byte[] right = eyeRenderer.ReadPixels();
        for (int y = 0; y < height; y++)
        {
            left.AsSpan(y * width * 4, width * 4).CopyTo(result.AsSpan(y * width * 2 * 4, width * 4));
            right.AsSpan(y * width * 4, width * 4).CopyTo(result.AsSpan((y * width * 2 * 4) + (width * 4), width * 4));
        }

        return result;
    }
}
