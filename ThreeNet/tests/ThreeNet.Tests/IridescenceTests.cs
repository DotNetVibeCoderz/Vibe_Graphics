using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Thin film interference: the colours on a soap bubble or an oil slick, which
/// are a function of the viewing angle rather than of the material. Needs a
/// GPU; skips without one.
/// </summary>
public class IridescenceTests
{
    private const int Size = 96;

    /// <summary>
    /// A grey metal sphere under one white light. Nothing in the scene has a
    /// colour, so any colour in the result came from the film.
    /// </summary>
    private static byte[] Render(Func<MaterialOptions, MaterialOptions> material)
    {
        Renderer? renderer;
        try
        {
            renderer = Renderer.CreateOffscreen(RendererOptions.Default with
            {
                Width = Size,
                Height = Size,
                MsaaSamples = 1,
                ToneMapping = ToneMapping.None,
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
                    AmbientIntensity = 0f,
                    Sky = SkyMode.Color,
                },
            };

            scene.AddMesh(
                scene.CreateSphereGeometry(1.5f, 64, 48),
                scene.CreateMaterial(material(
                    MaterialOptions.Pbr(new Vector4(0.7f, 0.7f, 0.7f, 1f), 1f, 0.25f))));

            Node light = scene.AddLight(Light.Directional(Vector3.One, 3f));
            light.LookAt(new Vector3(-1f, -1f, -1f));

            Node camera = scene.AddCamera(Camera.Perspective(40f.ToRadians()), new Vector3(0f, 0f, 5f));
            camera.LookAt(Vector3.Zero);
            renderer.Render(scene, camera);
            return renderer.ReadPixels();
        }
    }

    /// <summary>
    /// How far the most colourful lit pixel is from grey, 0 to 1. A neutral
    /// render of a neutral scene scores near zero however bright it is.
    /// </summary>
    private static float PeakChroma(byte[] pixels)
    {
        float peak = 0f;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int max = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
            int min = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
            // Dark pixels are quantisation noise, not colour.
            if (max < 24)
            {
                continue;
            }

            peak = Math.Max(peak, (max - min) / (float)max);
        }

        return peak;
    }

    /// <summary>The average hue angle of the lit pixels, in turns.</summary>
    private static float MeanHue(byte[] pixels)
    {
        float x = 0f;
        float y = 0f;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            float r = pixels[i] / 255f;
            float g = pixels[i + 1] / 255f;
            float b = pixels[i + 2] / 255f;
            if (Math.Max(r, Math.Max(g, b)) < 24f / 255f)
            {
                continue;
            }

            // Project onto the chromaticity plane and accumulate, so the answer
            // is a direction rather than an average of wrapped angles.
            x += r - ((g + b) * 0.5f);
            y += (g - b) * 0.866f;
        }

        return MathF.Atan2(y, x) / MathF.Tau;
    }

    [Fact]
    public void TheFilmColoursAnOtherwiseNeutralHighlight()
    {
        byte[] plain = Render(m => m);
        if (plain.Length == 0)
        {
            return;
        }

        byte[] filmed = Render(m => m with { Iridescence = 1f, IridescenceThickness = 550f });

        // Grey metal, white light, black background: without a film there is
        // nothing in the scene that could produce a hue.
        float neutral = PeakChroma(plain);
        float coloured = PeakChroma(filmed);
        Assert.True(neutral < 0.05f, $"the plain sphere should be neutral, chroma was {neutral:F3}");
        Assert.True(
            coloured > neutral + 0.15f,
            $"the film did not colour the highlight: {neutral:F3} then {coloured:F3}");
    }

    [Fact]
    public void ThicknessChoosesTheHue()
    {
        byte[] thin = Render(m => m with { Iridescence = 1f, IridescenceThickness = 250f });
        if (thin.Length == 0)
        {
            return;
        }

        byte[] thick = Render(m => m with { Iridescence = 1f, IridescenceThickness = 700f });

        // This is what separates interference from a tint: the thickness of the
        // film decides which wavelengths cancel, so the same material at a
        // different thickness is a different colour rather than a stronger one.
        float a = MeanHue(thin);
        float b = MeanHue(thick);
        float apart = Math.Abs(a - b);
        apart = Math.Min(apart, 1f - apart);
        Assert.True(apart > 0.04f, $"thickness did not move the hue: {a:F3} and {b:F3} turns apart by {apart:F3}");
    }

    [Fact]
    public void TheFilmFadesOutRatherThanSwitchingOff()
    {
        byte[] none = Render(m => m);
        if (none.Length == 0)
        {
            return;
        }

        byte[] half = Render(m => m with { Iridescence = 0.5f, IridescenceThickness = 550f });
        byte[] full = Render(m => m with { Iridescence = 1f, IridescenceThickness = 550f });

        // Partway has to land partway, or the parameter is a switch and not a
        // weight - which is how it reads in a material editor.
        float a = PeakChroma(none);
        float b = PeakChroma(half);
        float c = PeakChroma(full);
        Assert.True(a < b && b < c, $"not monotonic in the factor: {a:F3}, {b:F3}, {c:F3}");
    }

    [Fact]
    public void ZeroIsBitIdenticalToNeverAskingForIt()
    {
        byte[] plain = Render(m => m);
        if (plain.Length == 0)
        {
            return;
        }

        // The branch is the only thing a material that does not want a film
        // should pay, and a zero thickness must not leave a flat tint behind.
        byte[] off = Render(m => m with { Iridescence = 0f, IridescenceThickness = 550f });
        Assert.Equal(plain, off);
    }

    [Fact]
    public void TheOptionsSurviveARoundTrip()
    {
        using Scene scene = new();
        Material material = scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One) with
        {
            Iridescence = 0.6f,
            IridescenceIor = 1.8f,
            IridescenceThickness = 320f,
        });

        MaterialOptions read = material.Options;
        Assert.Equal(0.6f, read.Iridescence, 3);
        Assert.Equal(1.8f, read.IridescenceIor, 3);
        Assert.Equal(320f, read.IridescenceThickness, 2);
    }
}
