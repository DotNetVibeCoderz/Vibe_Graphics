using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Clipping planes cut a fragment away where it falls behind them. These need a
/// GPU and skip themselves when none is there.
/// </summary>
public class ClippingTests
{
    private const int Size = 96;

    private static Renderer? TryCreateRenderer(Vector4[]? planes)
    {
        try
        {
            return Renderer.CreateOffscreen(RendererOptions.Default with
            {
                Width = Size,
                Height = Size,
                MsaaSamples = 1,
                ClipPlanes = planes,
            });
        }
        catch (ThreeNetException)
        {
            return null;
        }
    }

    /// <summary>A red sphere filling the frame, on black.</summary>
    private static (Scene Scene, Node Camera) BuildScene()
    {
        Scene scene = new()
        {
            Environment = SceneEnvironment.Default with
            {
                Background = new Vector4(0f, 0f, 0f, 1f),
                AmbientIntensity = 1f,
                AmbientColor = Vector3.One,
            },
        };

        scene.AddMesh(
            scene.CreateSphereGeometry(1.5f, 32, 24),
            scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.9f, 0.1f, 0.1f, 1f), 0f, 1f)));

        Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 5f));
        camera.LookAt(Vector3.Zero);
        return (scene, camera);
    }

    private static byte[] Render(Vector4[]? planes)
    {
        using Renderer? renderer = TryCreateRenderer(planes);
        if (renderer is null)
        {
            return [];
        }

        using Scene scene = BuildScene().Scene;
        Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 5f));
        camera.LookAt(Vector3.Zero);
        renderer.Render(scene, camera);
        return renderer.ReadPixels();
    }

    private static bool IsLit(byte[] pixels, int x, int y)
    {
        int i = ((y * Size) + x) * 4;
        return pixels[i] > 24 || pixels[i + 1] > 24 || pixels[i + 2] > 24;
    }

    [Fact]
    public void APlaneCutsAwayWhatFallsBehindIt()
    {
        byte[] whole = Render(null);
        if (whole.Length == 0)
        {
            return;
        }

        // Keep only x >= 0: the normal points along +X, through the origin.
        byte[] halved = Render([new Vector4(1f, 0f, 0f, 0f)]);

        // The sphere covers the middle of the frame before clipping.
        Assert.True(IsLit(whole, Size / 4, Size / 2), "the sphere should cover the left of the frame");
        Assert.True(IsLit(whole, Size * 3 / 4, Size / 2), "the sphere should cover the right of the frame");

        // After clipping the left half is gone and the right half stays.
        Assert.False(IsLit(halved, Size / 4, Size / 2), "the clipped half is still drawn");
        Assert.True(IsLit(halved, Size * 3 / 4, Size / 2), "the kept half was clipped away");
    }

    [Fact]
    public void TwoPlanesCutAwayWhatFallsBehindEither()
    {
        byte[] quartered = Render([
            new Vector4(1f, 0f, 0f, 0f),
            new Vector4(0f, 1f, 0f, 0f),
        ]);
        if (quartered.Length == 0)
        {
            return;
        }

        // Only the quadrant with x >= 0 and y >= 0 survives. The frame has y
        // growing downwards, so that is the top right. The samples sit well
        // inside the sphere, which only covers about two thirds of the frame -
        // a corner sample would miss it whether it was clipped or not.
        Assert.True(IsLit(quartered, Size * 5 / 8, Size * 3 / 8), "the kept quadrant was clipped away");
        Assert.False(IsLit(quartered, Size * 3 / 8, Size * 3 / 8), "the -x quadrant is still drawn");
        Assert.False(IsLit(quartered, Size * 5 / 8, Size * 5 / 8), "the -y quadrant is still drawn");
    }

    [Fact]
    public void NoPlanesLeaveTheSceneAlone()
    {
        byte[] whole = Render(null);
        byte[] none = Render([]);
        if (whole.Length == 0)
        {
            return;
        }

        Assert.Equal(whole.Length, none.Length);
        Assert.True(IsLit(none, Size / 4, Size / 2), "an empty plane set should clip nothing");
    }
}
