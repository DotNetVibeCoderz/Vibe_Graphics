using System.Numerics;
using ThreeNet.Effects;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Particles stepped and billboarded entirely on the GPU. Needs an adapter that
/// can run compute; skips without one.
/// </summary>
public class GpuParticleTests
{
    private const int Size = 96;

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

    [Fact]
    public void TheKernelFillsTheFrameWithParticles()
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
                AmbientIntensity = 0f,
                Sky = SkyMode.Color,
            },
        };

        GpuParticleEffect particles = new(scene, renderer, capacity: 2048)
        {
            Origin = Vector3.Zero,
            EmitterRadius = 0.5f,
            Speed = 1.5f,
            Spread = 1.2f,
            Gravity = Vector3.Zero,
            Size = 0.25f,
        };

        Node node = scene.AddMesh(
            particles.Geometry,
            scene.CreateMaterial(GpuParticleEffect.DefaultMaterial(new Vector4(1f, 0.4f, 0.1f, 1f))));
        Assert.NotNull(node);

        Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 8f));
        camera.LookAt(Vector3.Zero);

        // Nothing has been stepped, so the geometry is still the zeroed quads
        // it was built with and the frame has to be empty.
        renderer.Render(scene, camera);
        long before = Brightness(renderer.ReadPixels());

        for (int i = 0; i < 20; i++)
        {
            particles.Update(renderer, scene, 1f / 60f, camera.WorldMatrix);
        }

        renderer.Render(scene, camera);
        long after = Brightness(renderer.ReadPixels());

        Assert.True(before < 500, $"the unstepped system should draw nothing, got {before}");
        Assert.True(after > before + 5000, $"the kernel drew no particles: {before} then {after}");
    }

    [Fact]
    public void ParticlesMoveTheWayTheyAreSent()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        // Fired hard along +X with no spread and no gravity: after enough steps
        // the plume has to be on the right of the frame and not the left.
        (long left, long right) = RenderPlume(renderer, Vector3.UnitX);
        Assert.True(right > left * 2 + 1000, $"the plume did not go right: {left} against {right}");

        (long leftBack, long rightBack) = RenderPlume(renderer, -Vector3.UnitX);
        Assert.True(leftBack > rightBack * 2 + 1000, $"the plume did not go left: {leftBack} against {rightBack}");
    }

    /// <summary>Runs a plume in one direction and weighs each half of the frame.</summary>
    private static (long Left, long Right) RenderPlume(Renderer renderer, Vector3 direction)
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

        GpuParticleEffect particles = new(scene, renderer, capacity: 1024)
        {
            Origin = Vector3.Zero,
            EmitterRadius = 0.05f,
            Direction = direction,
            Speed = 4f,
            Spread = 0.05f,
            Gravity = Vector3.Zero,
            Drag = 0f,
            Lifetime = 10f,
            LifetimeVariation = 0f,
            Size = 0.2f,
        };

        scene.AddMesh(
            particles.Geometry,
            scene.CreateMaterial(GpuParticleEffect.DefaultMaterial(Vector4.One)));

        Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 10f));
        camera.LookAt(Vector3.Zero);

        for (int i = 0; i < 30; i++)
        {
            particles.Update(renderer, scene, 1f / 60f, camera.WorldMatrix);
        }

        renderer.Render(scene, camera);
        byte[] pixels = renderer.ReadPixels();

        long left = 0;
        long right = 0;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int value = pixels[(((y * Size) + x) * 4) + 1];
                if (x < Size / 2)
                {
                    left += value;
                }
                else
                {
                    right += value;
                }
            }
        }

        return (left, right);
    }

    [Fact]
    public void ACapacityOfNothingIsRefused()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => new GpuParticleEffect(scene, renderer, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GpuParticleEffect(scene, renderer, -4));
    }

    [Fact]
    public void TheGeometryIsSizedForTheCapacity()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new();
        GpuParticleEffect particles = new(scene, renderer, capacity: 100);

        // Four corners and six indices a particle, uploaded once and never
        // touched by the CPU again.
        (int vertices, int indices) = particles.Geometry.Counts;
        Assert.Equal(400, vertices);
        Assert.Equal(600, indices);
        Assert.Equal(100, particles.Capacity);
    }

    private static long Brightness(byte[] pixels)
    {
        long total = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            total += pixels[i] + pixels[i + 1] + pixels[i + 2];
        }

        return total;
    }
}
