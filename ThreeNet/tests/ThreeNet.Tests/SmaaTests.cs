using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Morphological antialiasing. Needs a GPU; skips without one.
/// </summary>
public class SmaaTests
{
    private const int Width = 160;
    private const int Height = 120;

    /// <summary>
    /// A white quad tilted a few degrees off horizontal against black. A
    /// shallow edge like this is the case antialiasing exists for: without it
    /// the boundary is a run of long flat steps.
    /// </summary>
    private static byte[] Render(Func<RendererOptions, RendererOptions> options)
    {
        Renderer? renderer;
        try
        {
            renderer = Renderer.CreateOffscreen(options(RendererOptions.Default with
            {
                Width = Width,
                Height = Height,
                // No MSAA: the point is to measure what the post pass does.
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
                    Background = new Vector4(0f, 0f, 0f, 1f),
                    AmbientColor = Vector3.One,
                    AmbientIntensity = 1f,
                    Sky = SkyMode.Color,
                },
            };

            Node quad = scene.AddMesh(
                scene.CreatePlaneGeometry(40f, 20f),
                scene.CreateMaterial(MaterialOptions.Basic(Vector4.One)));
            // A few degrees off flat, which is what produces long steps.
            quad.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 4f.ToRadians());
            quad.Position = new Vector3(0f, -10f, 0f);

            Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 14f));
            camera.LookAt(Vector3.Zero);
            renderer.Render(scene, camera);
            return renderer.ReadPixels();
        }
    }

    /// <summary>
    /// How step-like the edge is: for each column, where the brightness crosses
    /// half way. On a hard edge that height jumps a whole pixel at a time and
    /// sits flat in between; antialiasing turns it into a gradual slope, so the
    /// jumps get smaller.
    /// </summary>
    private static double StaircaseEnergy(byte[] pixels)
    {
        double[] crossing = new double[Width];
        for (int x = 0; x < Width; x++)
        {
            crossing[x] = double.NaN;
            for (int y = 1; y < Height; y++)
            {
                int above = pixels[((((y - 1) * Width) + x) * 4) + 1];
                int below = pixels[(((y * Width) + x) * 4) + 1];
                if (above < 128 && below >= 128)
                {
                    // Where between the two rows the half way point falls.
                    double t = (128.0 - above) / Math.Max(below - above, 1);
                    crossing[x] = y - 1 + t;
                    break;
                }
            }
        }

        // The second difference of the edge height. A straight slope has none;
        // a staircase has a spike at every step.
        double total = 0;
        int counted = 0;
        for (int x = 1; x < Width - 1; x++)
        {
            if (double.IsNaN(crossing[x - 1]) || double.IsNaN(crossing[x]) || double.IsNaN(crossing[x + 1]))
            {
                continue;
            }

            double second = crossing[x + 1] - (2 * crossing[x]) + crossing[x - 1];
            total += second * second;
            counted++;
        }

        return counted > 0 ? total / counted : double.NaN;
    }

    [Fact]
    public void SmaaSmoothsAShallowEdge()
    {
        byte[] off = Render(o => o);
        if (off.Length == 0)
        {
            return;
        }

        byte[] on = Render(o => o with { Smaa = true });

        double rough = StaircaseEnergy(off);
        double smooth = StaircaseEnergy(on);
        Assert.False(double.IsNaN(rough), "the test edge was not found at all");
        Assert.False(double.IsNaN(smooth), "the antialiased edge was not found at all");

        // The whole claim of the technique, measured rather than eyeballed.
        Assert.True(
            smooth < rough * 0.75,
            $"SMAA did not smooth the edge: {rough:F4} then {smooth:F4}");
    }

    [Fact]
    public void SmaaKeepsTheEdgeWhereItWas()
    {
        byte[] off = Render(o => o);
        if (off.Length == 0)
        {
            return;
        }

        byte[] on = Render(o => o with { Smaa = true });

        // Smoothing that also moves the edge is not antialiasing, it is a blur.
        long dark = 0;
        long light = 0;
        for (int i = 0; i < off.Length; i += 4)
        {
            dark += off[i + 1];
            light += on[i + 1];
        }

        double shift = Math.Abs(light - dark) / (double)Math.Max(dark, 1);
        Assert.True(shift < 0.02, $"the image got {shift:P1} brighter or darker, which is a blur not an edge fix");
    }

    [Fact]
    public void SmaaTakesPrecedenceOverFxaa()
    {
        byte[] both = Render(o => o with { Smaa = true, Fxaa = true });
        if (both.Length == 0)
        {
            return;
        }

        byte[] smaaOnly = Render(o => o with { Smaa = true });
        // Asking for both has to pick one and say which, not run them in series.
        Assert.Equal(smaaOnly, both);
    }

    [Fact]
    public void OffIsBitIdenticalToNeverAskingForIt()
    {
        byte[] first = Render(o => o);
        if (first.Length == 0)
        {
            return;
        }

        byte[] second = Render(o => o with { Smaa = false });
        Assert.Equal(first, second);
    }

    [Fact]
    public void TheOptionSurvivesARoundTrip()
    {
        Renderer? renderer;
        try
        {
            renderer = Renderer.CreateOffscreen(RendererOptions.Default with
            {
                Width = 32,
                Height = 32,
                Smaa = true,
            });
        }
        catch (ThreeNetException)
        {
            return;
        }

        using (renderer)
        {
            Assert.True(renderer.Options.Smaa);
        }
    }
}
