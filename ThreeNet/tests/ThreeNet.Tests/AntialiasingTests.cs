using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// FXAA on the finished image. Needs a GPU; skips without one.
/// </summary>
public class AntialiasingTests
{
    private const int Size = 128;

    /// <summary>
    /// A white quad turned off axis on black: a long diagonal edge, which is
    /// exactly what aliasing shows up on and what FXAA is for.
    /// </summary>
    private static byte[] Render(bool fxaa, RenderPath path = RenderPath.Forward)
    {
        Renderer? renderer;
        try
        {
            renderer = Renderer.CreateOffscreen(RendererOptions.Default with
            {
                Width = Size,
                Height = Size,
                // No MSAA, or there would be nothing left for FXAA to do.
                MsaaSamples = 1,
                ToneMapping = ToneMapping.None,
                RenderPath = path,
                Fxaa = fxaa,
            });
        }
        catch (ThreeNetException)
        {
            return [];
        }

        using (renderer)
        {
            using Scene scene = new()
            {
                Environment = SceneEnvironment.Default with
                {
                    Background = new Vector4(0f, 0f, 0f, 1f),
                    AmbientColor = Vector3.One,
                    AmbientIntensity = 1f,
                },
            };

            Node quad = scene.AddMesh(
                scene.CreatePlaneGeometry(3f, 3f),
                scene.CreateMaterial(MaterialOptions.Basic(Vector4.One)));
            quad.EulerAngles = new Vector3(0f, 0f, 0.35f);

            Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 4f));
            camera.LookAt(Vector3.Zero);
            renderer.Render(scene, camera);
            return renderer.ReadPixels();
        }
    }

    /// <summary>
    /// How many pixels sit between black and white. An aliased edge jumps
    /// straight from one to the other; a smoothed one leaves a trail.
    /// </summary>
    private static int Intermediates(byte[] pixels)
    {
        int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 24 && pixels[i] < 232)
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void FxaaSoftensADiagonalEdge()
    {
        byte[] aliased = Render(fxaa: false);
        if (aliased.Length == 0)
        {
            return;
        }

        byte[] smoothed = Render(fxaa: true);

        Assert.Equal(aliased.Length, smoothed.Length);
        Assert.True(
            Intermediates(smoothed) > Intermediates(aliased),
            $"FXAA left no more partial pixels than the aliased frame: {Intermediates(aliased)} then {Intermediates(smoothed)}");
    }

    [Fact]
    public void FxaaWorksOnTheDeferredPathToo()
    {
        byte[] aliased = Render(fxaa: false, RenderPath.Deferred);
        if (aliased.Length == 0)
        {
            return;
        }

        byte[] smoothed = Render(fxaa: true, RenderPath.Deferred);
        Assert.True(
            Intermediates(smoothed) > Intermediates(aliased),
            "FXAA did nothing on the deferred path, which is the path that needs it");
    }

    [Fact]
    public void FxaaLeavesAFlatFrameAlone()
    {
        byte[] plain = Render(fxaa: false);
        if (plain.Length == 0)
        {
            return;
        }

        // The middle of the quad is flat white; the edge test should skip it
        // entirely rather than blurring a region with no edge in it.
        byte[] smoothed = Render(fxaa: true);
        int i = (((Size / 2) * Size) + (Size / 2)) * 4;
        Assert.Equal(plain[i], smoothed[i]);
    }
}
