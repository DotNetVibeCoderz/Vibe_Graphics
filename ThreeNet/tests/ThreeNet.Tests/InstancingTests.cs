using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Instanced rendering: many copies of one mesh from a single draw call.
/// The counts come off the frame stats, so most of these need a GPU.
/// </summary>
public class InstancingTests
{
    private static Renderer? TryCreateRenderer()
    {
        try
        {
            return Renderer.CreateOffscreen(RendererOptions.Default with
            {
                Width = 96,
                Height = 96,
                MsaaSamples = 1,
                FrustumCulling = false,
            });
        }
        catch (ThreeNetException)
        {
            return null;
        }
    }

    private static Matrix4x4[] Row(int count, float spacing)
    {
        Matrix4x4[] transforms = new Matrix4x4[count];
        for (int i = 0; i < count; i++)
        {
            transforms[i] = Matrix4x4.CreateTranslation(new Vector3((i - (count / 2)) * spacing, 0f, 0f));
        }

        return transforms;
    }

    [Fact]
    public void TheInstanceCountIsWhatWasSet()
    {
        using Scene scene = new();
        Node node = scene.AddMesh(scene.CreateBoxGeometry(), scene.CreateMaterial(Vector4.One));
        Assert.Equal(0, node.InstanceCount);

        node.SetInstances(Row(12, 2f));
        Assert.Equal(12, node.InstanceCount);

        node.ClearInstances();
        Assert.Equal(0, node.InstanceCount);
    }

    [Fact]
    public void PositionsAreEnoughToScatterWith()
    {
        using Scene scene = new();
        Node node = scene.AddMesh(scene.CreateBoxGeometry(), scene.CreateMaterial(Vector4.One));
        node.SetInstances([new Vector3(0f, 0f, 0f), new Vector3(3f, 0f, 0f)], scale: 0.5f);
        Assert.Equal(2, node.InstanceCount);
    }

    [Fact]
    public void ManyCopiesCostOneDrawCall()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new();
        Geometry box = scene.CreateBoxGeometry(0.5f, 0.5f, 0.5f);
        Material material = scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 0f, 1f));
        Node node = scene.AddMesh(box, material);
        node.SetInstances(Row(64, 1.5f));

        Node camera = scene.AddCamera(Camera.Perspective(60f.ToRadians()), new Vector3(0f, 0f, 60f));
        camera.LookAt(Vector3.Zero);
        renderer.Render(scene, camera);

        Assert.Equal(1, renderer.Stats.DrawCalls);
        // The triangle count reports what was actually drawn, not what one copy
        // would have been.
        Assert.Equal(12 * 64, renderer.Stats.Triangles);
    }

    [Fact]
    public void InstancesLandWhereTheirTransformsPutThem()
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

        Node node = scene.AddMesh(
            scene.CreateBoxGeometry(1f, 1f, 1f),
            scene.CreateMaterial(MaterialOptions.Basic(Vector4.One)));

        // Two cubes, well apart on x, seen head on.
        node.SetInstances([
            Matrix4x4.CreateTranslation(new Vector3(-3f, 0f, 0f)),
            Matrix4x4.CreateTranslation(new Vector3(3f, 0f, 0f)),
        ]);

        Node camera = scene.AddCamera(Camera.Perspective(60f.ToRadians()), new Vector3(0f, 0f, 10f));
        camera.LookAt(Vector3.Zero);
        renderer.Render(scene, camera);

        byte[] pixels = renderer.ReadPixels();
        bool Lit(int x, int y)
        {
            int i = ((y * 96) + x) * 4;
            return pixels[i] > 40;
        }

        Assert.True(Lit(24, 48), "nothing drawn on the left");
        Assert.True(Lit(72, 48), "nothing drawn on the right");
        // Between them is background: one stretched cube would have filled it.
        Assert.False(Lit(48, 48), "the gap between the instances was filled in");
    }

    [Fact]
    public void CullingSeesWhereTheInstancesAreNotJustTheNode()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new();
        Node node = scene.AddMesh(
            scene.CreateBoxGeometry(1f, 1f, 1f),
            scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 0f, 1f)));

        // The node origin sits far off screen; the instances are in view. With
        // culling on, node bounds alone would throw the whole set away.
        node.Position = new Vector3(0f, 0f, -400f);
        node.SetInstances([
            Matrix4x4.CreateTranslation(new Vector3(0f, 0f, 395f)),
            Matrix4x4.CreateTranslation(new Vector3(2f, 0f, 395f)),
        ]);

        renderer.Options = renderer.Options with { FrustumCulling = true };
        Node camera = scene.AddCamera(Camera.Perspective(60f.ToRadians()), new Vector3(0f, 0f, 10f));
        camera.LookAt(new Vector3(0f, 0f, -5f));
        renderer.Render(scene, camera);

        Assert.True(renderer.Stats.Triangles > 0, "the instances were culled with the node origin");
    }

    [Fact]
    public void ARayHitsAnInstanceAndSaysWhichOne()
    {
        using Scene scene = new();
        Node node = scene.AddMesh(
            scene.CreateBoxGeometry(1f, 1f, 1f),
            scene.CreateMaterial(Vector4.One));
        node.SetInstances([
            Matrix4x4.CreateTranslation(new Vector3(-5f, 0f, 0f)),
            Matrix4x4.CreateTranslation(new Vector3(5f, 0f, 0f)),
        ]);

        // Aimed at the second copy, which is nowhere near the node origin.
        IReadOnlyList<RayHit> hits = scene.Raycast(new Ray(new Vector3(5f, 0f, 10f), new Vector3(0f, 0f, -1f)));
        Assert.NotEmpty(hits);
        Assert.Equal(1, hits[0].InstanceIndex);

        IReadOnlyList<RayHit> first = scene.Raycast(new Ray(new Vector3(-5f, 0f, 10f), new Vector3(0f, 0f, -1f)));
        Assert.NotEmpty(first);
        Assert.Equal(0, first[0].InstanceIndex);

        // And the middle, where the node origin is, hits nothing.
        Assert.Empty(scene.Raycast(new Ray(new Vector3(0f, 0f, 10f), new Vector3(0f, 0f, -1f))));
    }

    [Fact]
    public void MovingTheNodeMovesEveryInstance()
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

        Node node = scene.AddMesh(
            scene.CreateBoxGeometry(1f, 1f, 1f),
            scene.CreateMaterial(MaterialOptions.Basic(Vector4.One)));
        node.SetInstances([Matrix4x4.Identity]);

        Node camera = scene.AddCamera(Camera.Perspective(60f.ToRadians()), new Vector3(0f, 0f, 10f));
        camera.LookAt(Vector3.Zero);

        renderer.Render(scene, camera);
        byte[] centred = renderer.ReadPixels();

        // The instance transform is inside the node transform, so this should
        // carry the instance with it.
        node.Position = new Vector3(4f, 0f, 0f);
        renderer.Render(scene, camera);
        byte[] moved = renderer.ReadPixels();

        int Middle(byte[] pixels) => pixels[(((48 * 96) + 48) * 4)];
        Assert.True(Middle(centred) > 40, "the instance was not in the middle to begin with");
        Assert.True(Middle(moved) <= 40, "moving the node left the instance behind");
    }
}
