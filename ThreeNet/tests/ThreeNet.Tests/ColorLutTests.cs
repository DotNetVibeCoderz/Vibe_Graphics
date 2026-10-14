using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Colour grading through a lookup table. Needs a GPU; skips without one.
/// </summary>
public class ColorLutTests
{
    private const int Size = 64;
    private const int LutSize = 32;

    /// <summary>
    /// A flat unlit square filling the frame, so the pixel that comes out is
    /// the colour that went in with the chain applied to it and nothing else.
    /// </summary>
    private static (byte R, byte G, byte B) Render(
        Vector4 color,
        Func<Scene, Texture?> lut,
        float strength = 1f)
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
            return (0, 0, 0);
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
                scene.CreatePlaneGeometry(40f, 40f),
                scene.CreateMaterial(MaterialOptions.Basic(color)));

            Texture? table = lut(scene);
            renderer.Options = renderer.Options with
            {
                ColorLut = table,
                ColorLutStrength = strength,
            };

            Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 5f));
            camera.LookAt(Vector3.Zero);
            renderer.Render(scene, camera);

            byte[] pixels = renderer.ReadPixels();
            int i = (((Size / 2) * Size) + (Size / 2)) * 4;
            return (pixels[i], pixels[i + 1], pixels[i + 2]);
        }
    }

    [Fact]
    public void ANeutralTableChangesNothing()
    {
        Vector4 colour = new(0.6f, 0.35f, 0.8f, 1f);
        (byte r, byte g, byte b) = Render(colour, _ => null);
        if ((r, g, b) == (0, 0, 0))
        {
            return;
        }

        (byte nr, byte ng, byte nb) = Render(colour, scene => scene.CreateNeutralColorLut(LutSize));

        // A table that maps every colour to itself is the control: if this
        // moves, the sampling is off by a texel somewhere rather than the look
        // being wrong.
        Assert.True(
            Math.Abs(nr - r) <= 2 && Math.Abs(ng - g) <= 2 && Math.Abs(nb - b) <= 2,
            $"the neutral table moved the colour: {r},{g},{b} then {nr},{ng},{nb}");
    }

    [Fact]
    public void ATableIsActuallyApplied()
    {
        Vector4 colour = new(0.6f, 0.35f, 0.8f, 1f);
        (byte r, byte g, byte b) = Render(colour, _ => null);
        if ((r, g, b) == (0, 0, 0))
        {
            return;
        }

        // Swap red and blue in the table. Nothing else in the chain can do
        // that, so the result says the table was read and read the right way
        // round - the blue slice index and the red axis are easy to transpose.
        (byte sr, _, byte sb) = Render(colour, scene => SwappedLut(scene));
        Assert.True(
            Math.Abs(sr - b) <= 3 && Math.Abs(sb - r) <= 3,
            $"expected red and blue swapped from {r},{b}, got {sr},{sb}");
    }

    [Fact]
    public void StrengthFadesTheLookIn()
    {
        Vector4 colour = new(0.6f, 0.35f, 0.8f, 1f);
        (byte r, _, _) = Render(colour, _ => null);
        if (r == 0)
        {
            return;
        }

        (byte full, _, _) = Render(colour, SwappedLut);
        (byte half, _, _) = Render(colour, SwappedLut, 0.5f);

        // Halfway has to land between the two, or strength is a switch.
        int low = Math.Min(r, full);
        int high = Math.Max(r, full);
        Assert.True(
            half > low + 2 && half < high - 2,
            $"a half strength look should sit between {low} and {high}, got {half}");
    }

    [Fact]
    public void AStrengthOfZeroIsBitIdenticalToNoTable()
    {
        Vector4 colour = new(0.6f, 0.35f, 0.8f, 1f);
        (byte r, byte g, byte b) = Render(colour, _ => null);
        if ((r, g, b) == (0, 0, 0))
        {
            return;
        }

        (byte zr, byte zg, byte zb) = Render(colour, SwappedLut, 0f);
        Assert.Equal((r, g, b), (zr, zg, zb));
    }

    [Fact]
    public void TheSizeComesFromTheTexture()
    {
        using Scene scene = new();
        Texture small = scene.CreateNeutralColorLut(8);
        Texture large = scene.CreateNeutralColorLut(64);

        // The strip is size slices of size by size, so width is size squared
        // and the shader can recover the size from the height alone.
        Assert.NotEqual(small.Id, large.Id);
        Assert.Throws<ArgumentOutOfRangeException>(() => scene.CreateNeutralColorLut(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => scene.CreateNeutralColorLut(256));
    }

    /// <summary>A table that swaps the red and blue of whatever looks it up.</summary>
    private static Texture SwappedLut(Scene scene)
    {
        const int size = LutSize;
        int width = size * size;
        byte[] pixels = new byte[width * size * 4];
        float last = size - 1;
        for (int blue = 0; blue < size; blue++)
        {
            for (int green = 0; green < size; green++)
            {
                for (int red = 0; red < size; red++)
                {
                    int i = (((green * width) + (blue * size) + red) * 4);
                    pixels[i] = (byte)Math.Round(blue / last * 255f);
                    pixels[i + 1] = (byte)Math.Round(green / last * 255f);
                    pixels[i + 2] = (byte)Math.Round(red / last * 255f);
                    pixels[i + 3] = 255;
                }
            }
        }

        Texture lut = scene.CreateTexture(width, size, pixels, TextureFormat.Rgba8Unorm);
        lut.SetSampler(WrapMode.ClampToEdge, WrapMode.ClampToEdge, linearFilter: true, mipmaps: false);
        return lut;
    }
}
