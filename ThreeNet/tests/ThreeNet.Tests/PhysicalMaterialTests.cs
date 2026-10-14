using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// The clearcoat and sheen lobes. Both are forward path only, so the deferred
/// case is checked for what it actually does rather than what it would be nice
/// for it to do. Needs a GPU; skips without one.
/// </summary>
public class PhysicalMaterialTests
{
    private const int Size = 96;

    private static byte[] Render(
        Func<MaterialOptions, MaterialOptions> material,
        RenderPath path = RenderPath.Forward,
        float lightIntensity = 4f)
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
                RenderPath = path,
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
                },
            };

            // A dark rough sphere, so any extra lobe is the only bright thing.
            scene.AddMesh(
                scene.CreateSphereGeometry(1.2f, 48, 32),
                scene.CreateMaterial(material(MaterialOptions.Pbr(new Vector4(0.05f, 0.05f, 0.05f, 1f), 0f, 0.9f))));

            Node light = scene.AddLight(Light.Directional(Vector3.One, lightIntensity));
            light.LookAt(new Vector3(-0.4f, -0.4f, -1f));

            Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 4f));
            camera.LookAt(Vector3.Zero);
            renderer.Render(scene, camera);
            return renderer.ReadPixels();
        }
    }

    /// <summary>Total light in the frame.</summary>
    private static long Brightness(byte[] pixels)
    {
        long sum = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            sum += pixels[i] + pixels[i + 1] + pixels[i + 2];
        }

        return sum;
    }

    /// <summary>The brightest pixel, which is where a narrow highlight shows.</summary>
    private static int Peak(byte[] pixels)
    {
        int peak = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            peak = Math.Max(peak, Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])));
        }

        return peak;
    }

    [Fact]
    public void AClearcoatAddsAHighlightARoughSurfaceWouldNotHave()
    {
        byte[] plain = Render(m => m);
        if (plain.Length == 0)
        {
            return;
        }

        byte[] coated = Render(m => m with { Clearcoat = 1f, ClearcoatRoughness = 0.05f });

        // The peak rises: a narrow, sharp highlight appears where there was none.
        Assert.True(
            Peak(coated) > Peak(plain),
            $"the coat added no highlight: peak {Peak(plain)} then {Peak(coated)}");

        // And the total falls, because the coat reflects by taking from the
        // layer underneath rather than by inventing light. A coat that only
        // ever brightened would be the bug.
        Assert.True(
            Brightness(coated) < Brightness(plain),
            $"the coat did not attenuate what is under it: {Brightness(plain)} then {Brightness(coated)}");
    }

    [Fact]
    public void SheenLightsUpACloth()
    {
        byte[] plain = Render(m => m);
        if (plain.Length == 0)
        {
            return;
        }

        byte[] velvet = Render(m => m with { Sheen = new Vector3(1f, 0.8f, 0.9f), SheenRoughness = 0.3f });
        Assert.True(
            Brightness(velvet) > Brightness(plain),
            $"sheen did nothing: {Brightness(plain)} then {Brightness(velvet)}");
    }

    [Fact]
    public void NeitherLobeChangesAnythingWhenItIsOff()
    {
        byte[] plain = Render(m => m);
        if (plain.Length == 0)
        {
            return;
        }

        // The defaults must be inert, or every existing material would shift.
        byte[] defaults = Render(m => m with
        {
            Clearcoat = 0f,
            Sheen = Vector3.Zero,
        });

        Assert.Equal(Brightness(plain), Brightness(defaults));
    }

    [Fact]
    public void TheDeferredPathIgnoresThem()
    {
        byte[] plain = Render(m => m, RenderPath.Deferred);
        if (plain.Length == 0)
        {
            return;
        }

        // Documented behaviour, not an accident: the G-buffer has five targets
        // and no room for a sixth, so a deferred frame drops the extra lobes.
        byte[] coated = Render(m => m with { Clearcoat = 1f, ClearcoatRoughness = 0.05f }, RenderPath.Deferred);
        Assert.Equal(Brightness(plain), Brightness(coated));
    }

    [Fact]
    public void AnisotropyStreaksTheHighlightWithoutDullingIt()
    {
        // A dim light on purpose: at full strength the highlight saturates at
        // 255 in both cases and the peak can no longer say anything.
        const float Dim = 0.35f;
        byte[] plain = Render(m => m with { Roughness = 0.25f }, lightIntensity: Dim);
        if (plain.Length == 0)
        {
            return;
        }

        byte[] brushed = Render(m => m with { Roughness = 0.25f, Anisotropy = 0.9f }, lightIntensity: Dim);
        Assert.True(Peak(plain) < 250, $"the control highlight saturated at {Peak(plain)}, so the peak proves nothing");

        // The highlight is stretched into a streak: wider along the grain,
        // narrower across it, so it covers more pixels.
        Assert.True(Lit(brushed) > Lit(plain), $"no streak: {Lit(plain)} then {Lit(brushed)}");

        // And the peak holds. One axis widens by exactly as much as the other
        // narrows, so the product of the two roughnesses - and with it the GGX
        // normalisation - is unchanged. A remap that only widened an axis would
        // dim the highlight, and the surface would read as rougher rather than
        // as brushed, which is the whole point of holding the mean.
        Assert.True(
            Math.Abs(Peak(brushed) - Peak(plain)) <= 4,
            $"anisotropy changed the peak, so it is not energy preserving: {Peak(plain)} then {Peak(brushed)}");
    }

    [Fact]
    public void TurningTheGrainChangesWhereTheStreakRuns()
    {
        byte[] along = Render(m => m with { Roughness = 0.25f, Anisotropy = 0.9f }, lightIntensity: 0.35f);
        if (along.Length == 0)
        {
            return;
        }

        // A quarter turn puts the grain across the surface instead of along it,
        // so the streak lies the other way and the pixels differ.
        byte[] across = Render(
            m => m with
            {
                Roughness = 0.25f,
                Anisotropy = 0.9f,
                AnisotropyRotation = MathF.PI / 2f,
            },
            lightIntensity: 0.35f);

        bool identical = true;
        for (int i = 0; i < along.Length; i += 4)
        {
            if (Math.Abs(along[i] - across[i]) > 2)
            {
                identical = false;
                break;
            }
        }

        Assert.False(identical, "turning the grain changed nothing");
    }

    /// <summary>How many pixels carry any specular at all.</summary>
    private static int Lit(byte[] pixels)
    {
        int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 8)
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void TheOptionsSurviveARoundTrip()
    {
        using Scene scene = new();
        Material material = scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One) with
        {
            Clearcoat = 0.7f,
            ClearcoatRoughness = 0.2f,
            Sheen = new Vector3(0.9f, 0.5f, 0.3f),
            SheenRoughness = 0.4f,
            Anisotropy = 0.6f,
            AnisotropyRotation = 1.1f,
        });

        MaterialOptions read = material.Options;
        Assert.Equal(0.7f, read.Clearcoat, 3);
        Assert.Equal(0.2f, read.ClearcoatRoughness, 3);
        Assert.Equal(0.5f, read.Sheen.Y, 3);
        Assert.Equal(0.4f, read.SheenRoughness, 3);
        Assert.Equal(0.6f, read.Anisotropy, 3);
        Assert.Equal(1.1f, read.AnisotropyRotation, 3);
    }
}
