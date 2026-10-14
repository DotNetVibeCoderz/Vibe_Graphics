using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Rendering into a texture a material then samples. Needs a GPU; skips without
/// one.
/// </summary>
public class RenderTargetTests
{
    private const int Size = 96;

    private static Renderer? TryCreateRenderer()
    {
        try
        {
            return Renderer.CreateOffscreen(RendererOptions.Default with
            {
                Width = Size,
                Height = Size,
                MsaaSamples = 1,
                ToneMapping = ToneMapping.None,
            });
        }
        catch (ThreeNetException)
        {
            return null;
        }
    }

    [Fact]
    public void WhatIsRenderedIntoATextureCanBeSampledFromIt()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new()
        {
            Environment = SceneEnvironment.Default with
            {
                Background = new Vector4(0f, 0f, 0f, 1f),
                AmbientColor = Vector3.One,
                AmbientIntensity = 1f,
            },
        };

        // A red cube on its own layer, which the first pass sees.
        Node subject = scene.AddMesh(
            scene.CreateBoxGeometry(2f, 2f, 2f),
            scene.CreateMaterial(MaterialOptions.Basic(new Vector4(1f, 0f, 0f, 1f))),
            name: "subject");
        subject.Layers = 2;

        Texture target = scene.CreateRenderTarget(Size, Size);

        // A quad that shows the target, on the main layer.
        Node screen = scene.AddMesh(
            scene.CreatePlaneGeometry(4f, 4f),
            scene.CreateMaterial(MaterialOptions.Basic(Vector4.One) with { BaseColorMap = target }),
            name: "screen");
        screen.Layers = 1;

        Node offCamera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 5f));
        offCamera.LookAt(Vector3.Zero);
        offCamera.Layers = 2;

        Node mainCamera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 5f));
        mainCamera.LookAt(Vector3.Zero);
        mainCamera.Layers = 1;

        // The reflection first, then the view that samples it.
        renderer.RenderToTexture(scene, offCamera, target);
        renderer.Render(scene, mainCamera);

        byte[] pixels = renderer.ReadPixels();
        int i = (((Size / 2) * Size) + (Size / 2)) * 4;
        // The screen quad shows the red cube that only the first pass could see.
        Assert.True(pixels[i] > 100, $"the target was not sampled, red was {pixels[i]}");
        Assert.True(pixels[i + 1] < 90, $"the quad is not showing red, green was {pixels[i + 1]}");
    }

    [Fact]
    public void AnOrdinaryTextureIsNotARenderTarget()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new();
        byte[] white = [255, 255, 255, 255];
        Texture plain = scene.CreateTexture(1, 1, white);
        Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 5f));

        ThreeNetException error = Assert.Throws<ThreeNetException>(
            () => renderer.RenderToTexture(scene, camera, plain));
        Assert.Contains("render target", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheTargetHasToMatchTheRendererSize()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new();
        Texture wrong = scene.CreateRenderTarget(Size / 2, Size / 2);
        Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 5f));

        // Documented restriction: every pass between here and the post chain
        // owns buffers sized to the renderer.
        ThreeNetException error = Assert.Throws<ThreeNetException>(
            () => renderer.RenderToTexture(scene, camera, wrong));
        Assert.Contains("match the renderer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheMainViewIsUnaffectedByAPreviousTargetPass()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new()
        {
            Environment = SceneEnvironment.Default with
            {
                Background = new Vector4(0f, 0.6f, 0f, 1f),
            },
        };

        Texture target = scene.CreateRenderTarget(Size, Size);
        Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 5f));

        renderer.RenderToTexture(scene, camera, target);
        renderer.Render(scene, camera);

        // The override has to be cleared whatever happened, or every later
        // frame would keep drawing into the texture instead of the output.
        byte[] pixels = renderer.ReadPixels();
        int i = (((Size / 2) * Size) + (Size / 2)) * 4;
        Assert.True(pixels[i + 1] > 100, "the main view did not reach the output buffer");
    }
}
