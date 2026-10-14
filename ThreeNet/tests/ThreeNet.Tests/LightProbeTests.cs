using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Light probes: ambient light that knows which way a surface faces. The bake
/// needs a GPU; the maths does not.
/// </summary>
public class LightProbeTests
{
    private const int ProbeSize = 32;

    private static Renderer? TryCreateRenderer(int width, int height)
    {
        try
        {
            return Renderer.CreateOffscreen(RendererOptions.Default with
            {
                Width = width,
                Height = height,
                MsaaSamples = 1,
                ToneMapping = ToneMapping.None,
            });
        }
        catch (ThreeNetException)
        {
            return null;
        }
    }

    /// <summary>
    /// A room with one bright red wall on the +X side and nothing else, so any
    /// colour a probe picks up can only have come from that wall.
    /// </summary>
    private static void BuildRoom(Scene scene)
    {
        Node wall = scene.AddMesh(
            scene.CreatePlaneGeometry(30f, 30f),
            scene.CreateMaterial(MaterialOptions.Basic(new Vector4(1f, 0f, 0f, 1f)) with
            {
                CullMode = CullMode.None,
            }));
        wall.Position = new Vector3(6f, 0f, 0f);
        wall.LookAt(Vector3.Zero);
    }

    [Fact]
    public void AProbeIsBrighterOnTheSideFacingTheLight()
    {
        using Renderer? renderer = TryCreateRenderer(ProbeSize, ProbeSize);
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
        BuildRoom(scene);

        LightProbe probe = renderer.BakeLightProbe(scene, Vector3.Zero);

        // The whole point of a probe over a scalar ambient: direction.
        Vector3 towards = probe.Irradiance(Vector3.UnitX);
        Vector3 away = probe.Irradiance(-Vector3.UnitX);
        Assert.True(
            towards.X > away.X + 0.05f,
            $"facing the red wall should be brighter: {towards.X:F3} against {away.X:F3}");

        // And it is the wall's colour, not a grey average of the room.
        Assert.True(towards.X > towards.Y + 0.05f, $"the probe lost the hue: {towards}");
    }

    [Fact]
    public void TheBakeLeavesTheSceneAsItWas()
    {
        using Renderer? renderer = TryCreateRenderer(ProbeSize, ProbeSize);
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new();
        BuildRoom(scene);
        int before = CountNodes(scene);

        renderer.BakeLightProbe(scene, Vector3.Zero);

        // The camera the capture needs must not outlive it.
        Assert.Equal(before, CountNodes(scene));

        // And the settings it borrowed have to come back.
        Assert.Equal(ToneMapping.None, renderer.Options.ToneMapping);
        Assert.Equal(1f, renderer.Options.Exposure, 3);
    }

    [Fact]
    public void TheBakeRestoresToneMappingItDidNotOwn()
    {
        using Renderer? renderer = TryCreateRenderer(ProbeSize, ProbeSize);
        if (renderer is null)
        {
            return;
        }

        renderer.Options = renderer.Options with { ToneMapping = ToneMapping.Aces, Exposure = 2.5f };
        using Scene scene = new();
        BuildRoom(scene);

        renderer.BakeLightProbe(scene, Vector3.Zero);

        Assert.Equal(ToneMapping.Aces, renderer.Options.ToneMapping);
        Assert.Equal(2.5f, renderer.Options.Exposure, 3);
    }

    [Fact]
    public void ANonSquareRendererIsRefused()
    {
        using Renderer? renderer = TryCreateRenderer(64, 32);
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new();
        ThreeNetException error = Assert.Throws<ThreeNetException>(
            () => renderer.BakeLightProbe(scene, Vector3.Zero));
        Assert.Contains("square", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AProbeSurvivesTheRoundTripThroughTheScene()
    {
        using Scene scene = new();
        Assert.Null(scene.LightProbe);

        LightProbe probe = LightProbe.Uniform(new Vector3(0.25f, 0.5f, 0.75f));
        scene.LightProbe = probe;

        LightProbe? read = scene.LightProbe;
        Assert.NotNull(read);
        for (int i = 0; i < LightProbe.CoefficientCount; i++)
        {
            Assert.Equal(probe.Coefficients[i].X, read.Coefficients[i].X, 4);
            Assert.Equal(probe.Coefficients[i].Y, read.Coefficients[i].Y, 4);
            Assert.Equal(probe.Coefficients[i].Z, read.Coefficients[i].Z, 4);
        }

        scene.LightProbe = null;
        Assert.Null(scene.LightProbe);
    }

    [Fact]
    public void AUniformProbeReflectsItsOwnRadiance()
    {
        // The analytic case, where the right answer is known exactly: a probe
        // of uniform radiance r reflects r in every direction.
        LightProbe probe = LightProbe.Uniform(new Vector3(0.4f, 0.4f, 0.4f));
        foreach (Vector3 normal in new[] { Vector3.UnitX, Vector3.UnitY, -Vector3.UnitZ })
        {
            Assert.Equal(0.4f, probe.Irradiance(normal).X, 3);
        }
    }

    [Fact]
    public void ADarkProbeAddsNothing()
    {
        Assert.Equal(Vector3.Zero, new LightProbe().Irradiance(Vector3.UnitY));
        Assert.True(new LightProbe().IsDark);
    }

    [Fact]
    public void AProbeNeedsExactlyNineCoefficients()
    {
        Assert.Throws<ArgumentException>(() => new LightProbe(new Vector3[8]));
        Assert.Throws<ArgumentException>(() => new LightProbe(new Vector3[10]));
    }

    [Fact]
    public void AProbeActuallyLightsAScene()
    {
        using Renderer? renderer = TryCreateRenderer(64, 64);
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

        // A white matte sphere with no lights at all: whatever reaches it came
        // from the probe.
        scene.AddMesh(
            scene.CreateSphereGeometry(1.5f, 32, 24),
            scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 0f, 0.9f)));
        Node camera = scene.AddCamera(Camera.Perspective(45f.ToRadians()), new Vector3(0f, 0f, 5f));
        camera.LookAt(Vector3.Zero);

        renderer.Render(scene);
        byte[] dark = renderer.ReadPixels();

        scene.LightProbe = LightProbe.Uniform(new Vector3(0.5f, 0.5f, 0.5f));
        renderer.Render(scene);
        byte[] lit = renderer.ReadPixels();

        int i = (((64 / 2) * 64) + (64 / 2)) * 4;
        Assert.True(dark[i] < 10, $"with no lights and no probe the sphere should be black, got {dark[i]}");
        Assert.True(lit[i] > 80, $"the probe did not light the sphere, got {lit[i]}");
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
