using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Cube map environment textures. Needs a GPU; skips without one.
/// </summary>
public class CubeMapTests
{
    private const int Size = 64;

    /// <summary>A solid face of one colour.</summary>
    private static byte[] Face(int size, byte r, byte g, byte b)
    {
        byte[] pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

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
    public void ACubeNeedsSixFacesOfTheRightSize()
    {
        using Scene scene = new();
        byte[] good = Face(8, 255, 255, 255);
        byte[] short_ = new byte[16];

        // A face of the wrong length is rejected rather than read past. The
        // native side takes one length for all six faces and so cannot tell,
        // which makes this the only place the check can happen.
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            scene.CreateCubeTexture(8, good, good, good, short_, good, good));
        Assert.Contains("face 3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMirrorPicksUpTheFaceItIsLookingAt()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        // Each face a different colour, so what a reflection shows says which
        // direction it sampled.
        const int FaceSize = 16;
        Texture cube = scene_CreateCube(out Scene scene, FaceSize);
        using (scene)
        {
            scene.Environment = SceneEnvironment.Default with
            {
                Background = new Vector4(0f, 0f, 0f, 1f),
                EnvironmentMap = cube,
                EnvironmentIntensity = 1f,
                AmbientIntensity = 0f,
            };

            // A mirror ball: metal, perfectly smooth, so it shows the cube and
            // nothing else.
            scene.AddMesh(
                scene.CreateSphereGeometry(1.5f, 48, 32),
                scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 1f, 0.02f)));

            Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 4f));
            camera.LookAt(Vector3.Zero);
            renderer.Render(scene, camera);

            byte[] pixels = renderer.ReadPixels();
            int i = (((Size / 2) * Size) + (Size / 2)) * 4;
            // Looking down -Z at the middle of the ball, the reflected ray goes
            // back towards the camera: the +Z face, which is blue here.
            Assert.True(pixels[i + 2] > pixels[i], $"expected the blue face, got {pixels[i]},{pixels[i + 1]},{pixels[i + 2]}");
        }
    }

    [Fact]
    public void AnEquirectangularMapStillWorks()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new();
        // A flat green environment, through the equirectangular path.
        Texture flat = scene.CreateTexture(4, 4, Face(4, 0, 255, 0));
        scene.Environment = SceneEnvironment.Default with
        {
            Background = new Vector4(0f, 0f, 0f, 1f),
            EnvironmentMap = flat,
            EnvironmentIntensity = 1f,
            AmbientIntensity = 0f,
        };

        scene.AddMesh(
            scene.CreateSphereGeometry(1.5f, 48, 32),
            scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 1f, 0.02f)));

        Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 4f));
        camera.LookAt(Vector3.Zero);
        renderer.Render(scene, camera);

        byte[] pixels = renderer.ReadPixels();
        int i = (((Size / 2) * Size) + (Size / 2)) * 4;
        // The two paths share one slot, so adding the cube must not have broken
        // the flat one.
        Assert.True(pixels[i + 1] > pixels[i], "the equirectangular path stopped working");
    }

    private static Texture scene_CreateCube(out Scene scene, int faceSize)
    {
        scene = new Scene();
        return scene.CreateCubeTexture(
            faceSize,
            Face(faceSize, 255, 0, 0),     // +X red
            Face(faceSize, 128, 0, 0),     // -X dark red
            Face(faceSize, 0, 255, 0),     // +Y green
            Face(faceSize, 0, 128, 0),     // -Y dark green
            Face(faceSize, 0, 0, 255),     // +Z blue
            Face(faceSize, 0, 0, 128));    // -Z dark blue
    }
}
