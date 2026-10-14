using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Capturing the surroundings of a point into a cube. Needs a GPU; skips
/// without one.
/// </summary>
public class CubeCameraTests
{
    private const int Size = 64;

    private static Renderer? TryCreateRenderer(int size = Size)
    {
        try
        {
            return Renderer.CreateOffscreen(RendererOptions.Default with
            {
                Width = size,
                Height = size,
                MsaaSamples = 1,
                ToneMapping = ToneMapping.None,
            });
        }
        catch (ThreeNetException)
        {
            return null;
        }
    }

    /// <summary>A coloured wall on each side of the origin, facing inwards.</summary>
    private static void BuildRoom(Scene scene)
    {
        (Vector3 at, Vector4 colour)[] walls =
        [
            (new Vector3(6f, 0f, 0f), new Vector4(1f, 0f, 0f, 1f)),
            (new Vector3(-6f, 0f, 0f), new Vector4(0f, 1f, 0f, 1f)),
            (new Vector3(0f, 0f, 6f), new Vector4(0f, 0f, 1f, 1f)),
            (new Vector3(0f, 0f, -6f), new Vector4(1f, 1f, 0f, 1f)),
        ];

        foreach ((Vector3 at, Vector4 colour) in walls)
        {
            // Double sided on purpose: LookAt points a node -Z at its target
            // while a plane faces +Z, so a wall turned towards the probe shows
            // it the back face. Culling it would leave the probe looking at
            // nothing, which is a property of the test scene and not of the
            // capture.
            Node wall = scene.AddMesh(
                scene.CreatePlaneGeometry(20f, 20f),
                scene.CreateMaterial(MaterialOptions.Basic(colour) with { CullMode = CullMode.None }));
            wall.Position = at;
            wall.LookAt(Vector3.Zero);
        }
    }

    [Fact]
    public void ACaptureFillsTheCubeAndTheSceneIsLeftAsItWas()
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

        BuildRoom(scene);
        int before = CountNodes(scene);

        Texture probe = scene.CreateCubeRenderTarget(Size);
        renderer.RenderToCube(scene, Vector3.Zero, probe);

        // The temporary camera the capture needs must not outlive it.
        Assert.Equal(before, CountNodes(scene));

        // A mirror ball lit only by the probe shows the room that was captured.
        scene.Environment = scene.Environment with
        {
            EnvironmentMap = probe,
            EnvironmentIntensity = 1f,
            AmbientIntensity = 0f,
        };
        scene.AddMesh(
            scene.CreateSphereGeometry(1.5f, 48, 32),
            scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 1f, 0.02f)));

        Node camera = scene.AddCamera(Camera.Perspective(40f.ToRadians()), new Vector3(0f, 0f, 4f));
        camera.LookAt(Vector3.Zero);
        renderer.Render(scene, camera);

        byte[] pixels = renderer.ReadPixels();
        int i = (((Size / 2) * Size) + (Size / 2)) * 4;
        // Looking down -Z, the middle of the ball reflects back towards +Z,
        // which is the blue wall.
        Assert.True(
            pixels[i + 2] > pixels[i] && pixels[i + 2] > 30,
            $"expected the blue wall in the probe, got {pixels[i]},{pixels[i + 1]},{pixels[i + 2]}");
    }

    [Fact]
    public void AFlatTargetIsNotACube()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new();
        Texture flat = scene.CreateRenderTarget(Size, Size);
        ThreeNetException error = Assert.Throws<ThreeNetException>(
            () => renderer.RenderToCube(scene, Vector3.Zero, flat));
        Assert.Contains("cube", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheFaceSizeHasToMatchTheRenderer()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new();
        Texture wrong = scene.CreateCubeRenderTarget(Size * 2);
        ThreeNetException error = Assert.Throws<ThreeNetException>(
            () => renderer.RenderToCube(scene, Vector3.Zero, wrong));
        Assert.Contains("match the renderer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountNodes(Scene scene)
    {
        int count = 0;
        void Walk(Node node)
        {
            count++;
            foreach (Node child in node.Children)
            {
                Walk(child);
            }
        }

        Walk(scene.Root);
        return count;
    }
}
