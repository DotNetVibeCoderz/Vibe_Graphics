using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// The grading stage of the post chain: saturation, contrast, brightness, the
/// vignette and the grain. All of it runs after tone mapping, so these compare
/// pixels of the finished frame. Needs a GPU; skips without one.
/// </summary>
public class GradingTests
{
    private const int Size = 64;

    /// <summary>A flat mid-grey frame, which makes every grading change legible.</summary>
    private static byte[] Render(Func<RendererOptions, RendererOptions> configure)
    {
        Renderer? renderer;
        try
        {
            renderer = Renderer.CreateOffscreen(configure(RendererOptions.Default with
            {
                Width = Size,
                Height = Size,
                MsaaSamples = 1,
                ToneMapping = ToneMapping.None,
            }));
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
                    Background = new Vector4(0.5f, 0.5f, 0.5f, 1f),
                },
            };

            Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 5f));
            renderer.Render(scene, camera);
            return renderer.ReadPixels();
        }
    }

    private static (int R, int G, int B) Pixel(byte[] pixels, int x, int y)
    {
        int i = ((y * Size) + x) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2]);
    }

    [Fact]
    public void BrightnessLiftsTheWholeFrame()
    {
        byte[] plain = Render(o => o);
        if (plain.Length == 0)
        {
            return;
        }

        byte[] lifted = Render(o => o with { Brightness = 0.2f });
        (int r, _, _) = Pixel(plain, Size / 2, Size / 2);
        (int lit, _, _) = Pixel(lifted, Size / 2, Size / 2);
        Assert.True(lit > r + 10, $"brightness did nothing: {r} then {lit}");
    }

    [Fact]
    public void SaturationAtZeroLeavesGreyscale()
    {
        Renderer? probe;
        try
        {
            probe = Renderer.CreateOffscreen(RendererOptions.Default with { Width = 8, Height = 8 });
        }
        catch (ThreeNetException)
        {
            return;
        }

        probe.Dispose();

        // A coloured background, so there is something to desaturate.
        byte[] grey = RenderColored(new Vector4(0.8f, 0.2f, 0.1f, 1f), o => o with { Saturation = 0f });
        (int r, int g, int b) = Pixel(grey, Size / 2, Size / 2);

        // Every channel should land on the same luma.
        Assert.True(Math.Abs(r - g) <= 2 && Math.Abs(g - b) <= 2, $"not greyscale: {r},{g},{b}");
    }

    [Fact]
    public void AVignetteDarkensTheCornersAndLeavesTheCentre()
    {
        byte[] shaded = Render(o => o with { Vignette = 1f, VignetteSoftness = 0.8f });
        if (shaded.Length == 0)
        {
            return;
        }

        (int centre, _, _) = Pixel(shaded, Size / 2, Size / 2);
        (int corner, _, _) = Pixel(shaded, 1, 1);
        Assert.True(centre > corner + 20, $"the corner should be darker: centre {centre}, corner {corner}");
    }

    [Fact]
    public void GrainBreaksUpAFlatFrame()
    {
        byte[] flat = Render(o => o);
        if (flat.Length == 0)
        {
            return;
        }

        byte[] grainy = Render(o => o with { Grain = 0.3f });

        // A flat frame has one value everywhere; grain should scatter it.
        Assert.True(Spread(flat) <= 1, $"the control frame was not flat, spread {Spread(flat)}");
        Assert.True(Spread(grainy) > 8, $"grain did nothing, spread {Spread(grainy)}");
    }

    /// <summary>Difference between the lightest and darkest red channel.</summary>
    private static int Spread(byte[] pixels)
    {
        int low = 255;
        int high = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            low = Math.Min(low, pixels[i]);
            high = Math.Max(high, pixels[i]);
        }

        return high - low;
    }

    private static byte[] RenderColored(Vector4 background, Func<RendererOptions, RendererOptions> configure)
    {
        Renderer? renderer;
        try
        {
            renderer = Renderer.CreateOffscreen(configure(RendererOptions.Default with
            {
                Width = Size,
                Height = Size,
                MsaaSamples = 1,
                ToneMapping = ToneMapping.None,
            }));
        }
        catch (ThreeNetException)
        {
            return [];
        }

        using (renderer)
        {
            using Scene scene = new()
            {
                Environment = SceneEnvironment.Default with { Background = background },
            };

            Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 5f));
            renderer.Render(scene, camera);
            return renderer.ReadPixels();
        }
    }
}
