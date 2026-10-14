using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Skinning on the GPU. The property that matters is that turning it on does
/// not change the picture, so most of this renders the same pose both ways.
/// Needs an adapter that can run compute.
/// </summary>
public class GpuSkinningTests
{
    private const int Size = 128;

    private static Renderer? TryCreateRenderer()
    {
        try
        {
            Renderer renderer = Renderer.CreateOffscreen(RendererOptions.Default with
            {
                Width = Size,
                Height = Size,
                MsaaSamples = 1,
                ToneMapping = ToneMapping.None,
            });
            if (!renderer.SupportsCompute)
            {
                renderer.Dispose();
                return null;
            }

            return renderer;
        }
        catch (ThreeNetException)
        {
            return null;
        }
    }

    /// <summary>
    /// Loads the rigged mesh, holds it at a point in its animation, and renders.
    /// </summary>
    private static byte[] Render(Renderer renderer, bool gpuSkinning, float time)
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

        ImportResult import = scene.LoadGltf(AnimationTests.TestAsset("RiggedSimple.glb"));
        if (gpuSkinning)
        {
            foreach (Node node in Descendants(import.Root))
            {
                if (node.Geometry is not null)
                {
                    node.GpuSkinning = true;
                }
            }
        }

        scene.Animations[0].Play();
        scene.UpdateAnimations(time);

        Node camera = scene.AddCamera(Camera.Perspective(45f.ToRadians()), new Vector3(6f, 4f, 8f));
        camera.LookAt(new Vector3(0f, 1f, 0f));
        renderer.Render(scene, camera);
        return renderer.ReadPixels();
    }

    [Fact]
    public void TheSamePoseLooksTheSameEitherWay()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        byte[] cpu = Render(renderer, gpuSkinning: false, time: 0.7f);
        byte[] gpu = Render(renderer, gpuSkinning: true, time: 0.7f);

        // The two paths blend the same matrices in the same order, so they
        // should agree to within rounding. A disagreement here is a pose that
        // would make a mesh jump the moment the flag is flipped.
        long difference = 0;
        long lit = 0;
        for (int i = 0; i < cpu.Length; i += 4)
        {
            difference += Math.Abs(cpu[i] - gpu[i])
                + Math.Abs(cpu[i + 1] - gpu[i + 1])
                + Math.Abs(cpu[i + 2] - gpu[i + 2]);
            lit += cpu[i] + cpu[i + 1] + cpu[i + 2];
        }

        Assert.True(lit > 10_000, $"the rigged mesh did not render at all, brightness {lit}");
        // Under a percent of the total light in the frame.
        Assert.True(
            difference * 100 < lit,
            $"the two skinning paths disagree: {difference} against {lit} lit");
    }

    [Fact]
    public void TheMeshStillMovesWhenTheAnimationDoes()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        // Two different times on the GPU path. If the kernel never ran, both
        // frames would be the bind pose and identical - which is exactly how a
        // silently broken dispatch would look.
        byte[] early = Render(renderer, gpuSkinning: true, time: 0.1f);
        byte[] late = Render(renderer, gpuSkinning: true, time: 1.1f);

        long difference = 0;
        for (int i = 0; i < early.Length; i += 4)
        {
            difference += Math.Abs(early[i] - late[i]);
        }

        Assert.True(difference > 2000, $"the GPU pose did not change with time, difference {difference}");
    }

    [Fact]
    public void TheFlagSurvivesARoundTrip()
    {
        using Scene scene = new();
        ImportResult import = scene.LoadGltf(AnimationTests.TestAsset("RiggedSimple.glb"));

        Node mesh = Descendants(import.Root).First(node => node.Geometry is not null);
        Assert.False(mesh.GpuSkinning);

        mesh.GpuSkinning = true;
        Assert.True(mesh.GpuSkinning);

        mesh.GpuSkinning = false;
        Assert.False(mesh.GpuSkinning);
    }

    [Fact]
    public void ANodeWithNoMeshRefusesTheFlag()
    {
        using Scene scene = new();
        Node empty = scene.CreateNode();

        // Setting it on something that cannot be skinned is a caller mistake
        // worth reporting, not a no-op to be discovered later.
        Assert.Throws<ThreeNetException>(() => empty.GpuSkinning = true);
    }

    private static IEnumerable<Node> Descendants(Node root)
    {
        yield return root;
        foreach (Node child in root.Children)
        {
            foreach (Node node in Descendants(child))
            {
                yield return node;
            }
        }
    }
}
