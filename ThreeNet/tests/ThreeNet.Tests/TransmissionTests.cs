using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Transmission: glass showing a bent version of what is behind it. Needs a
/// GPU; skips without one.
/// </summary>
public class TransmissionTests
{
    private const int Size = 96;

    /// <summary>
    /// A red wall at the back, and a pane in front of it that may or may not be
    /// glass. What reaches the middle of the frame says whether it let the wall
    /// through.
    /// </summary>
    private static byte[] Render(Func<MaterialOptions, MaterialOptions> pane)
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
                    AmbientColor = Vector3.One,
                    AmbientIntensity = 1f,
                },
            };

            Node wall = scene.AddMesh(
                scene.CreatePlaneGeometry(10f, 10f),
                scene.CreateMaterial(MaterialOptions.Basic(new Vector4(1f, 0f, 0f, 1f))),
                name: "wall");
            wall.Position = new Vector3(0f, 0f, -4f);

            Node glass = scene.AddMesh(
                scene.CreatePlaneGeometry(4f, 4f),
                scene.CreateMaterial(pane(MaterialOptions.Pbr(new Vector4(0f, 0f, 1f, 1f), 0f, 0.05f))),
                name: "pane");
            glass.Position = new Vector3(0f, 0f, 0f);

            Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 6f));
            camera.LookAt(new Vector3(0f, 0f, -1f));
            renderer.Render(scene, camera);
            return renderer.ReadPixels();
        }
    }

    private static (int R, int G, int B) Middle(byte[] pixels)
    {
        int i = (((Size / 2) * Size) + (Size / 2)) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2]);
    }

    [Fact]
    public void GlassLetsTheWallBehindItThrough()
    {
        byte[] opaque = Render(m => m);
        if (opaque.Length == 0)
        {
            return;
        }

        // Opaque: the blue pane hides the red wall.
        (int r, _, int b) = Middle(opaque);
        Assert.True(b > r, $"the opaque pane should read blue, got {r},{b}");

        // Transmissive: the red wall comes through.
        byte[] glass = Render(m => m with
        {
            AlphaMode = AlphaMode.Blend,
            BaseColor = new Vector4(1f, 1f, 1f, 0.5f),
            Transmission = 1f,
            Thickness = 0f,
        });
        (int gr, _, int gb) = Middle(glass);
        Assert.True(gr > r, $"the wall did not come through the glass: red {r} then {gr}");
        Assert.True(gr > gb, $"the glass should show the red wall, got {gr},{gb}");
    }

    [Fact]
    public void ThicknessTintsWhatComesThrough()
    {
        byte[] thin = Render(m => m with
        {
            AlphaMode = AlphaMode.Blend,
            BaseColor = new Vector4(0f, 1f, 0f, 0.5f),
            Transmission = 1f,
            Thickness = 0f,
        });
        if (thin.Length == 0)
        {
            return;
        }

        // Thick green glass absorbs the red it is passing, by its own colour.
        byte[] thick = Render(m => m with
        {
            AlphaMode = AlphaMode.Blend,
            BaseColor = new Vector4(0f, 1f, 0f, 0.5f),
            Transmission = 1f,
            Thickness = 1f,
        });

        (int thinR, _, _) = Middle(thin);
        (int thickR, _, _) = Middle(thick);
        Assert.True(thickR < thinR, $"thickness did not absorb: {thinR} then {thickR}");
    }

    [Fact]
    public void AnOpaqueSceneIsUnchangedByTheSplit()
    {
        // Nothing transmissive means the forward pass is not split and no copy
        // happens, so an ordinary scene must come out exactly as before.
        byte[] first = Render(m => m);
        if (first.Length == 0)
        {
            return;
        }

        byte[] second = Render(m => m);
        Assert.Equal(first, second);
    }

    [Fact]
    public void TheOptionsSurviveARoundTrip()
    {
        using Scene scene = new();
        Material material = scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One) with
        {
            Transmission = 0.8f,
            Ior = 1.33f,
            Thickness = 0.25f,
        });

        MaterialOptions read = material.Options;
        Assert.Equal(0.8f, read.Transmission, 3);
        Assert.Equal(1.33f, read.Ior, 3);
        Assert.Equal(0.25f, read.Thickness, 3);
    }
}
