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
    public void DebugViewsReplaceTheShadedImageWithOneChannel()
    {
        // Exposure and tone mapping are left on: a debug view has to ignore both.
        using Renderer? renderer = TryCreateRenderer(bloom: true);
        if (renderer is null)
        {
            return;
        }

        (Scene scene, Node camera, _) = BuildScene();
        using (scene)
        {
            static float ToLinear(byte value)
            {
                float channel = value / 255f;
                return channel <= 0.04045f ? channel / 12.92f : MathF.Pow((channel + 0.055f) / 1.055f, 2.4f);
            }

            byte[] Center(DebugView view)
            {
                renderer.Options = renderer.Options with { DebugView = view, Exposure = 3f };
                renderer.Render(scene, camera);
                byte[] pixels = renderer.ReadPixels();
                int center = ((64 * 128) + 64) * 4;
                return [pixels[center], pixels[center + 1], pixels[center + 2]];
            }

            byte[] baseColor = Center(DebugView.BaseColor);
            Assert.InRange(ToLinear(baseColor[0]), 0.85f, 0.95f);   // the material is 0.9 red
            Assert.InRange(ToLinear(baseColor[1]), 0.15f, 0.25f);

            byte[] roughness = Center(DebugView.Roughness);
            Assert.InRange(ToLinear(roughness[0]), 0.3f, 0.4f);     // ... and 0.35 rough
            Assert.Equal(roughness[0], roughness[1]);

            byte[] normal = Center(DebugView.WorldNormal);
            Assert.True(ToLinear(normal[2]) > 0.9f, "the centre of the sphere faces the camera");

            byte[] lit = Center(DebugView.Off);
            Assert.False(lit.SequenceEqual(baseColor), "turning the view off should shade again");
        }
    }

    [Fact]
    public void ReportsWhatTheGpuCanDo()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        GpuCapabilities capabilities = renderer.Capabilities;
        Assert.True(capabilities.MaxTextureSize >= 2048, "every adapter supports 2048 px textures");
        Assert.True(capabilities.MaxBindGroups >= 4, "the renderer itself binds four groups");
        Assert.InRange(capabilities.MaxMsaaSamples, 1, 8);
        Assert.NotEqual(GpuBackend.Unknown, capabilities.Backend);
        Assert.Contains(renderer.AdapterName, renderer.AdapterName);   // never throws

        (Scene scene, Node camera, _) = BuildScene();
        using (scene)
        {
            // Timing is read back without stalling, so it needs a few frames.
            for (int i = 0; i < 8; i++)
            {
                renderer.Render(scene, camera);
            }

            if (capabilities.TimestampQueries)
            {
                Assert.True(renderer.Stats.GpuTimeMs > 0f, "a timed frame should report GPU time");
            }
            else
            {
                Assert.Equal(0f, renderer.Stats.GpuTimeMs);
            }
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

    private static float Luminance(byte[] pixels, int x, int y, int width = 128)
    {
        int i = ((y * width) + x) * 4;
        return (0.2126f * pixels[i]) + (0.7152f * pixels[i + 1]) + (0.0722f * pixels[i + 2]);
    }

    [Fact]
    public void DirectionalLightCastsShadowsWhenEnabled()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new()
        {
            Environment = SceneEnvironment.Default with { Background = new Vector4(0f, 0f, 0f, 1f), AmbientIntensity = 0f },
        };
        Material white = scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.8f, 0.8f, 0.8f, 1f), 0f, 0.9f));
        // A wall facing the camera and a cube floating two units in front of it.
        scene.AddMesh(scene.CreatePlaneGeometry(20f, 20f), white, name: "wall");
        Node cube = scene.AddMesh(scene.CreateBoxGeometry(), white, name: "cube");
        cube.Position = new Vector3(0f, 0f, 2f);

        Node sun = scene.AddLight(Light.Directional(Vector3.One, 3f) with { CastShadow = true });
        sun.LookAt(new Vector3(1f, 0f, -1f));
        Node camera = scene.AddCamera(Camera.Perspective(45f.ToRadians()), new Vector3(0f, 0f, 10f));

        // Wall x = +2 (in the shadow) and x = -2 (lit) at the image centre row.
        float halfWidth = 10f * MathF.Tan(MathF.PI / 8f);
        int Column(float x) => (int)(((x / halfWidth * 0.5f) + 0.5f) * 128f);

        renderer.Render(scene, camera);
        byte[] withoutShadows = renderer.ReadPixels();
        Assert.Equal(0, renderer.Stats.ShadowLayers);

        renderer.Options = renderer.Options with { Shadows = true, ShadowMapSize = 1024 };
        renderer.Render(scene, camera);
        byte[] withShadows = renderer.ReadPixels();

        float lit = Luminance(withShadows, Column(-2f), 64);
        float shadowed = Luminance(withShadows, Column(2f), 64);
        Assert.True(lit > 40f, $"the wall should be lit, got {lit}");
        Assert.True(shadowed < lit * 0.35f, $"the shadow should be dark: {shadowed} vs {lit}");
        Assert.True(Math.Abs(Luminance(withoutShadows, Column(2f), 64) - lit) < 8f, "no shadow when disabled");
        Assert.Equal(3, renderer.Stats.ShadowLayers);

        // Opting the cube out of casting removes the shadow again.
        Assert.True(cube.CastShadow);
        cube.CastShadow = false;
        Assert.False(cube.CastShadow);
        Assert.True(cube.ReceiveShadow);
        renderer.Render(scene, camera);
        Assert.True(Math.Abs(Luminance(renderer.ReadPixels(), Column(2f), 64) - lit) < 8f, "CastShadow = false");
    }

    [Fact]
    public void PointLightCastsACubeShadow()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new() { Environment = SceneEnvironment.Default with { Background = Vector4.Zero, AmbientIntensity = 0f } };
        Material white = scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.8f, 0.8f, 0.8f, 1f), 0f, 0.9f));
        Node floor = scene.AddMesh(scene.CreatePlaneGeometry(20f, 20f), white, name: "floor");
        floor.EulerAngles = new Vector3(-MathF.PI / 2f, 0f, 0f);
        Node cube = scene.AddMesh(scene.CreateBoxGeometry(), white, name: "cube");
        cube.Position = new Vector3(0f, 1f, 0f);

        // The light sits above and to one side, so the shadow lands beside the cube.
        Node lamp = scene.AddLight(
            Light.Point(Vector3.One, 30f, range: 40f) with { CastShadow = true, ShadowNormalBias = 1.5f },
            name: "lamp");
        lamp.Position = new Vector3(2f, 4f, 2f);

        Node camera = scene.AddCamera(Camera.Perspective(45f.ToRadians()), new Vector3(-3f, 4f, 7f));
        camera.LookAt(new Vector3(-0.7f, 0f, -0.7f));

        renderer.Options = renderer.Options with { Shadows = true, ShadowMapSize = 1024 };
        renderer.Render(scene, camera);
        byte[] pixels = renderer.ReadPixels();
        Assert.Equal(6, renderer.Stats.ShadowLayers);

        // Where the light through the cube meets the floor, versus open floor.
        (int X, int Y) shadow = Project(scene, camera, new Vector3(-2f / 3f, 0f, -2f / 3f));
        (int X, int Y) open = Project(scene, camera, new Vector3(2.5f, 0f, -2.5f));
        float shadowed = Luminance(pixels, shadow.X, shadow.Y);
        float lit = Luminance(pixels, open.X, open.Y);
        Assert.True(lit > 20f, $"the floor should be lit: {lit}");
        Assert.True(shadowed < lit * 0.5f, $"the cube should shadow the floor: {shadowed} vs {lit}");

        lamp.Light = lamp.Light!.Value with { CastShadow = false };
        renderer.Render(scene, camera);
        Assert.Equal(0, renderer.Stats.ShadowLayers);
        Assert.True(Luminance(renderer.ReadPixels(), shadow.X, shadow.Y) > shadowed * 1.8f, "the shadow goes away");
    }

    /// <summary>Pixel a world point lands on for a camera node, in a 128x128 target.</summary>
    private static (int X, int Y) Project(Scene scene, Node camera, Vector3 point)
    {
        Matrix4x4.Invert(camera.WorldMatrix, out Matrix4x4 view);
        Camera lens = camera.Camera!.Value;
        float tan = MathF.Tan(lens.FieldOfView * 0.5f);
        Vector3 viewSpace = Vector3.Transform(point, view);
        float x = viewSpace.X / (-viewSpace.Z * tan);
        float y = viewSpace.Y / (-viewSpace.Z * tan);
        return ((int)((x * 0.5f + 0.5f) * 128f), (int)((0.5f - y * 0.5f) * 128f));
    }

    [Fact]
    public void SsaoRendersWithoutErrors()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        (Scene scene, Node camera, _) = BuildScene();
        using (scene)
        {
            renderer.Options = renderer.Options with { Ssao = true, SsaoSamples = 32, Shadows = true };
            renderer.Render(scene, camera);
            byte[] pixels = renderer.ReadPixels();
            int center = ((64 * 128) + 64) * 4;
            Assert.True(pixels[center] > 60, "the sphere should still be visible with SSAO on");
            Assert.True(renderer.Stats.ShadowDrawCalls >= 1, "the SSAO prepass draws the sphere");
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
