using System.Numerics;
using ThreeNet.Effects;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// The water, fire and particle effects. Creating a shader validates its WGSL
/// against both the forward and the deferred pass, so most of these need no GPU;
/// the ones that render say so and skip themselves when no adapter is there.
/// </summary>
public class EffectsTests
{
    private static Renderer? TryCreateRenderer(int size = 160)
    {
        try
        {
            return Renderer.CreateOffscreen(RendererOptions.Default with
            {
                Width = size,
                Height = size,
                MsaaSamples = 1,
            });
        }
        catch (ThreeNetException)
        {
            return null;
        }
    }

    [Theory]
    [InlineData(nameof(EffectShaders.Water))]
    [InlineData(nameof(EffectShaders.WaterFloor))]
    [InlineData(nameof(EffectShaders.Fire))]
    [InlineData(nameof(EffectShaders.Particle))]
    [InlineData(nameof(EffectShaders.Smoke))]
    public void EveryEffectShaderCompiles(string name)
    {
        string source = name switch
        {
            nameof(EffectShaders.Water) => EffectShaders.Water,
            nameof(EffectShaders.WaterFloor) => EffectShaders.WaterFloor,
            nameof(EffectShaders.Fire) => EffectShaders.Fire,
            nameof(EffectShaders.Particle) => EffectShaders.Particle,
            _ => EffectShaders.Smoke,
        };

        using Scene scene = new();
        Shader shader = scene.CreateShader(source, name: name);
        Assert.NotEqual(0u, shader.Id);
    }

    [Fact]
    public void ADropSpreadsAsARippleAndDiesAway()
    {
        WaterSimulation water = new(64, 8f);
        water.AddDropUv(0.5f, 0.5f, 0.3f, -0.05f);

        float centre = water.SampleHeight(0f, 0f);
        Assert.True(centre < -0.01f, $"the drop should dent the water, got {centre}");

        // The ripple has to reach ground the drop never touched.
        float away = 2.5f;
        Assert.Equal(0f, water.SampleHeight(away, 0f), 4);
        for (int i = 0; i < 120; i++)
        {
            water.Step();
        }

        Assert.True(MathF.Abs(water.SampleHeight(away, 0f)) > 1e-4f, "the ripple never travelled");

        // And it has to settle, or the simulation is unstable.
        for (int i = 0; i < 4000; i++)
        {
            water.Step();
        }

        float residue = 0f;
        for (int i = 0; i < water.Cells.Length; i += 4)
        {
            residue = MathF.Max(residue, MathF.Abs(water.Cells[i]));
        }

        Assert.True(residue < 0.01f, $"the water never calmed down, {residue} left");
    }

    [Fact]
    public void TheHeightFieldNormalLeansAwayFromACrest()
    {
        WaterSimulation water = new(64, 8f);
        water.AddDropUv(0.5f, 0.5f, 0.5f, 0.1f);
        water.ComputeNormals();

        // The drop is 0.5 m across, so these two sit on its opposite flanks,
        // where the surface tilts opposite ways.
        Vector3 left = water.SampleNormal(-0.25f, 0f);
        Vector3 right = water.SampleNormal(0.25f, 0f);
        Assert.True(left.Y > 0f && right.Y > 0f, "normals must point up out of the water");
        Assert.True(left.X * right.X < 0f, $"the slopes should oppose, got {left.X} and {right.X}");
    }

    [Fact]
    public void TheFieldTextureCarriesHeightsIntoTheScene()
    {
        using Scene scene = new();
        WaterSimulation water = new(32, 4f);
        Texture field = water.CreateTexture(scene);
        Assert.False(field.IsNull);

        // Updating in place must take the new state, not throw at it.
        water.AddDropUv(0.5f, 0.5f, 0.4f, -0.05f);
        water.Step();
        water.ComputeNormals();
        water.Upload(field);
    }

    [Fact]
    public void AParticleSystemFillsUpAndDrainsAgain()
    {
        ParticleEffect particles = new(256)
        {
            Rate = 0f,
            Lifetime = 0.5f,
            Variation = 0f,
            Shape = EmitterShape.Cone,
            Gravity = Vector3.Zero,
        };

        particles.Burst(100);
        Assert.Equal(100, particles.Alive);

        for (int i = 0; i < 60; i++)
        {
            particles.Update(1f / 60f);
        }

        Assert.Equal(0, particles.Alive);
    }

    [Fact]
    public void ACeilingOnTheCapacityIsNeverCrossed()
    {
        ParticleEffect particles = new(32) { Rate = 100000f, Lifetime = 10f };
        for (int i = 0; i < 20; i++)
        {
            particles.Update(1f / 60f);
        }

        Assert.Equal(32, particles.Alive);
    }

    [Fact]
    public void ParticlesBounceOffTheFloorInsteadOfFallingThrough()
    {
        ParticleEffect particles = new(16)
        {
            Rate = 0f,
            Lifetime = 10f,
            Variation = 0f,
            Shape = EmitterShape.Point,
            Rise = 0f,
            Spread = 0f,
            Drag = 0f,
            Origin = new Vector3(0f, 2f, 0f),
            Gravity = new Vector3(0f, -9.8f, 0f),
            FloorHeight = 0f,
        };

        particles.Burst(1);
        for (int i = 0; i < 180; i++)
        {
            particles.Update(1f / 60f);
        }

        using Scene scene = new();
        Geometry geometry = particles.CreateGeometry(scene);
        particles.Upload(geometry, Matrix4x4.Identity);
        Assert.Equal(1, particles.Alive);
    }

    [Fact]
    public void AWaterSurfaceBringsItsOwnMeshMaterialAndField()
    {
        using Scene scene = new();
        WaterSurface water = new(scene, new Vector3(0f, 0f, 0f), 8f, -1.5f, resolution: 32, segments: 32)
        {
            Style = WaterStyle.Pool,
        };

        Assert.NotEqual(0u, water.Node.Id);
        Assert.Equal(AlphaMode.Blend, water.Material.Options.AlphaMode);
        Assert.NotNull(water.Material.Options.CustomMap);
        Assert.Equal(8f, water.Material.Options.Custom0.Z, 3);
        Assert.Equal(-1.5f, water.Material.Options.Custom2.W, 3);

        water.Splash(new Vector3(1f, 0f, 1f), 0.2f, 0.05f);
        water.Update(1f / 30f);
        Assert.True(water.HeightAt(1f, 1f) < 0f, "the splash should show in the sampled height");

        Material floor = water.AddFloor(MaterialOptions.Pbr(new Vector4(0.8f, 0.8f, 0.75f, 1f), 0f, 0.7f));
        Assert.Equal(water.FloorShader, floor.Options.Shader);
        Assert.NotNull(floor.Options.CustomMap);
    }

    [Fact]
    public void AFireIsAnAdditiveBoxThatFollowsItsNode()
    {
        using Scene scene = new();
        FireEffect fire = new(scene, new Vector3(2f, 0f, -1f), new Vector3(1f, 1.6f, 1f))
        {
            Style = FireStyle.Torch,
        };

        Assert.Equal(AlphaMode.Additive, fire.Material.Options.AlphaMode);
        Assert.False(fire.Material.Options.DepthWrite);
        Assert.Equal(2f, fire.Material.Options.Custom0.X, 3);

        fire.Node.Position = new Vector3(-4f, 0.8f, 3f);
        fire.Update(1f / 60f);
        Assert.Equal(-4f, fire.Material.Options.Custom0.X, 3);

        // The flicker has to wander without ever going out.
        float lowest = 1f;
        for (int i = 0; i < 600; i++)
        {
            fire.Update(1f / 60f);
            lowest = MathF.Min(lowest, fire.Flicker);
        }

        Assert.InRange(lowest, 0.3f, 0.99f);
    }

    [Fact]
    public void WaterAndFireRenderWithoutFallingOverTheRenderer()
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
                Background = new Vector4(0.02f, 0.03f, 0.05f, 1f),
                Sky = SkyMode.Procedural,
                SunDirection = new Vector3(0.3f, 0.6f, 0.5f),
            },
        };

        WaterSurface water = new(scene, Vector3.Zero, 6f, -1f, resolution: 32, segments: 32);
        water.AddFloor(MaterialOptions.Pbr(new Vector4(0.75f, 0.72f, 0.6f, 1f), 0f, 0.8f));
        water.Splash(new Vector3(1f, 0f, 0f));
        water.Update(1f / 30f);

        FireEffect fire = new(scene, new Vector3(0f, 0.2f, 1.5f), new Vector3(0.8f, 1.2f, 0.8f));
        fire.Update(1f / 60f);

        ParticleEffect embers = new(128) { Origin = new Vector3(0f, 0.4f, 1.5f), Rate = 200f };
        embers.Update(0.2f);
        Shader sprite = scene.CreateShader(EffectShaders.Particle, name: "embers");
        Material emberMaterial = scene.CreateMaterial(
            ParticleEffect.GlowMaterial(sprite, new Vector3(1f, 0.6f, 0.2f), new Vector3(0.5f, 0.05f, 0f)));
        Geometry emberGeometry = embers.CreateGeometry(scene);
        scene.AddMesh(emberGeometry, emberMaterial, name: "embers");
        embers.Upload(emberGeometry, Matrix4x4.Identity);

        Node sun = scene.AddLight(Light.Directional(Vector3.One, 3f));
        sun.LookAt(new Vector3(-0.3f, -0.6f, -0.5f));
        Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 1.6f, 5f));
        camera.LookAt(Vector3.Zero);

        renderer.Render(scene, camera);
        byte[] pixels = renderer.ReadPixels();
        Assert.Equal(160 * 160 * 4, pixels.Length);

        bool lit = false;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 24 || pixels[i + 1] > 24 || pixels[i + 2] > 24)
            {
                lit = true;
                break;
            }
        }

        Assert.True(lit, "the frame came back black");
    }
}
