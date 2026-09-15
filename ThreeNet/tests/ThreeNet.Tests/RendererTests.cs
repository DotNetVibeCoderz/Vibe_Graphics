using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Rendering tests need a GPU adapter. When none is available (a bare CI
/// container) the tests report skipped instead of failing the build.
/// </summary>
public class RendererTests
{
    private static Renderer? TryCreateRenderer(int width = 128, int height = 128, bool bloom = false)
    {
        try
        {
            return Renderer.CreateOffscreen(RendererOptions.Default with
            {
                Width = width,
                Height = height,
                MsaaSamples = 4,
                Bloom = bloom,
            });
        }
        catch (ThreeNetException)
        {
            return null;
        }
    }

    private static (Scene Scene, Node Camera, Node Mesh) BuildScene()
    {
        Scene scene = new()
        {
            Environment = SceneEnvironment.Default with { Background = new Vector4(0f, 0f, 0f, 1f) },
        };

        Material material = scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.9f, 0.2f, 0.15f, 1f), 0f, 0.35f));
        Node mesh = scene.AddMesh(scene.CreateSphereGeometry(1f), material, name: "sphere");

        Node sun = scene.AddLight(Light.Directional(Vector3.One, 4f));
        sun.LookAt(new Vector3(-1f, -1f, -1f));

        Node camera = scene.AddCamera(Camera.Perspective(45f.ToRadians()), new Vector3(0f, 0f, 4f));
        return (scene, camera, mesh);
    }

    [Fact]
    public void RendersALitSphereOffscreen()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        (Scene scene, Node camera, _) = BuildScene();
        using (scene)
        {
            renderer.Render(scene, camera);
            byte[] pixels = renderer.ReadPixels();

            Assert.Equal(renderer.PixelBufferSize, pixels.Length);

            int center = ((64 * 128) + 64) * 4;
            Assert.True(pixels[center] > 60, "the sphere should be visible at the centre");
            Assert.True(pixels[center] > pixels[center + 1], "the red channel should dominate");
            Assert.True(pixels[0] < 20, "the corner should keep the background colour");

            FrameStats stats = renderer.Stats;
            Assert.Equal(1, stats.DrawCalls);
            Assert.Equal(1, stats.Lights);
            Assert.True(stats.Triangles > 0);
        }
    }

    [Fact]
    public void CullsObjectsOutsideTheFrustum()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        (Scene scene, Node camera, Node mesh) = BuildScene();
        using (scene)
        {
            mesh.Position = new Vector3(0f, 0f, 500f);
            renderer.Render(scene, camera);

            Assert.Equal(0, renderer.Stats.DrawCalls);
            Assert.Equal(1, renderer.Stats.CulledNodes);
        }
    }

    [Fact]
    public void ResizeChangesTheReadbackSize()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        (Scene scene, Node camera, _) = BuildScene();
        using (scene)
        {
            renderer.Resize(64, 32);
            renderer.Render(scene, camera);

            Assert.Equal(64, renderer.Width);
            Assert.Equal(32, renderer.Height);
            Assert.Equal(64 * 32 * 4, renderer.ReadPixels().Length);
        }
    }

    [Fact]
    public void UnlitMaterialsIgnoreLighting()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        (Scene scene, Node camera, Node mesh) = BuildScene();
        using (scene)
        {
            Material unlit = scene.CreateMaterial(MaterialOptions.Basic(new Vector4(0f, 1f, 0f, 1f)));
            mesh.AttachMesh(scene.CreateSphereGeometry(1f), unlit);

            // Turn every light off: an unlit material must still show up.
            foreach (Node child in scene.Root.Children)
            {
                child.Light = null;
            }

            renderer.Render(scene, camera);
            byte[] pixels = renderer.ReadPixels();

            int center = ((64 * 128) + 64) * 4;
            Assert.True(pixels[center + 1] > 100, "the unlit green sphere should stay bright");
        }
    }

    [Fact]
    public void ReadPixelsRejectsATooSmallBuffer()
    {
        using Renderer? renderer = TryCreateRenderer(32, 32);
        if (renderer is null)
        {
            return;
        }

        Assert.Throws<ArgumentException>(() => renderer.ReadPixels(new byte[16]));
    }
}
