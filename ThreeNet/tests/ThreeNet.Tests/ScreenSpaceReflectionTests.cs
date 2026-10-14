using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Screen space reflections: a floor picking up what is standing on it. Needs a
/// GPU; skips without one.
/// </summary>
public class ScreenSpaceReflectionTests
{
    private const int Width = 128;
    private const int Height = 96;

    /// <summary>
    /// A bright red box standing on a dark floor, with the camera low enough
    /// that the floor in front of the box is seen at a grazing angle - which is
    /// where a reflection belongs and where Fresnel makes it strongest.
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

            // A floor built here rather than from the plane generator, which
            // stands on XY facing +Z.
            Node floor = scene.AddMesh(
                scene.CreateBoxGeometry(40f, 0.2f, 40f),
                scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.02f, 0.02f, 0.02f, 1f), 0f, 0.05f)));
            floor.Position = new Vector3(0f, -0.1f, 0f);

            Node box = scene.AddMesh(
                scene.CreateBoxGeometry(2f, 2f, 2f),
                scene.CreateMaterial(MaterialOptions.Basic(new Vector4(1f, 0f, 0f, 1f))));
            box.Position = new Vector3(0f, 1f, 0f);

            Node camera = scene.AddCamera(Camera.Perspective(45f.ToRadians()), new Vector3(0f, 1.2f, 7f));
            camera.LookAt(new Vector3(0f, 0.6f, 0f));
            renderer.Render(scene, camera);
            return renderer.ReadPixels();
        }
    }

    /// <summary>
    /// How much red is sitting on the floor in the bottom strip of the frame,
    /// which is below the box and so can only have got there by reflection.
    /// </summary>
    private static long RedOnTheFloor(byte[] pixels)
    {
        long total = 0;
        for (int y = Height * 3 / 4; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = ((y * Width) + x) * 4;
                // Count red that exceeds the other channels, so the dark floor
                // itself contributes nothing.
                int red = pixels[i] - Math.Max(pixels[i + 1], pixels[i + 2]);
                if (red > 0)
                {
                    total += red;
                }
            }
        }

        return total;
    }

    [Fact]
    public void TheFloorPicksUpWhatIsStandingOnIt()
    {
        byte[] off = Render(o => o);
        if (off.Length == 0)
        {
            return;
        }

        byte[] on = Render(o => o with { Ssr = true, SsrMaxDistance = 20f, SsrThickness = 1f });

        long dry = RedOnTheFloor(off);
        long wet = RedOnTheFloor(on);
        Assert.True(wet > dry * 2 + 500, $"no reflection appeared on the floor: {dry} then {wet}");
    }

    [Fact]
    public void IntensityScalesTheReflection()
    {
        byte[] half = Render(o => o with
        {
            Ssr = true,
            SsrIntensity = 0.35f,
            SsrMaxDistance = 20f,
            SsrThickness = 1f,
        });
        if (half.Length == 0)
        {
            return;
        }

        byte[] full = Render(o => o with
        {
            Ssr = true,
            SsrIntensity = 1f,
            SsrMaxDistance = 20f,
            SsrThickness = 1f,
        });

        long a = RedOnTheFloor(half);
        long b = RedOnTheFloor(full);
        Assert.True(a > 0, "a partial intensity should still reflect something");
        Assert.True(b > a, $"intensity did not scale the reflection: {a} then {b}");
    }

    [Fact]
    public void OffIsBitIdenticalToNeverAskingForIt()
    {
        byte[] first = Render(o => o);
        if (first.Length == 0)
        {
            return;
        }

        // A disabled effect must not cost a pass or move a pixel, including the
        // prepass it would otherwise turn on.
        byte[] second = Render(o => o with { Ssr = false, SsrIntensity = 1f });
        Assert.Equal(first, second);
    }

    [Fact]
    public void TheSkyReflectsNothing()
    {
        byte[] on = Render(o => o with { Ssr = true, SsrMaxDistance = 20f });
        if (on.Length == 0)
        {
            return;
        }

        // The prepass clears to a zero depth, and the top corners of this frame
        // are background. If a zero depth were treated as geometry the march
        // would start from the camera and smear the scene across the sky.
        for (int x = 0; x < 8; x++)
        {
            int i = ((0 * Width) + x) * 4;
            Assert.True(
                on[i] < 8 && on[i + 1] < 8 && on[i + 2] < 8,
                $"the background should stay black, got {on[i]},{on[i + 1]},{on[i + 2]}");
        }
    }

    [Fact]
    public void TheOptionsSurviveARoundTrip()
    {
        Renderer? renderer;
        try
        {
            renderer = Renderer.CreateOffscreen(RendererOptions.Default with
            {
                Width = 32,
                Height = 32,
                Ssr = true,
                SsrIntensity = 0.5f,
                SsrMaxDistance = 25f,
                SsrThickness = 0.75f,
                SsrSteps = 48,
            });
        }
        catch (ThreeNetException)
        {
            return;
        }

        using (renderer)
        {
            RendererOptions read = renderer.Options;
            Assert.True(read.Ssr);
            Assert.Equal(0.5f, read.SsrIntensity, 3);
            Assert.Equal(25f, read.SsrMaxDistance, 3);
            Assert.Equal(0.75f, read.SsrThickness, 3);
            Assert.Equal(48, read.SsrSteps);
        }
    }
}
