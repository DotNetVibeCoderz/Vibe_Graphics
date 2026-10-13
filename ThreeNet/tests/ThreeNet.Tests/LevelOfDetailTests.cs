using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Levels of detail swap a node's geometry by camera distance. The counts are
/// read off the frame stats, so these need a GPU and skip without one.
/// </summary>
public class LevelOfDetailTests
{
    private static Renderer? TryCreateRenderer()
    {
        try
        {
            return Renderer.CreateOffscreen(RendererOptions.Default with
            {
                Width = 64,
                Height = 64,
                MsaaSamples = 1,
                // Culling would hide the effect being measured.
                FrustumCulling = false,
            });
        }
        catch (ThreeNetException)
        {
            return null;
        }
    }

    [Fact]
    public void TheLevelCountIsWhatWasSet()
    {
        using Scene scene = new();
        Geometry near = scene.CreateSphereGeometry(1f, 32, 24);
        Geometry far = scene.CreateSphereGeometry(1f, 8, 6);
        Node node = scene.AddMesh(near, scene.CreateMaterial(Vector4.One));

        Assert.Equal(0, node.LevelOfDetailCount);

        node.SetLevelsOfDetail((near, 10f), (far, float.PositiveInfinity));
        Assert.Equal(2, node.LevelOfDetailCount);

        node.ClearLevelsOfDetail();
        Assert.Equal(0, node.LevelOfDetailCount);
    }

    [Fact]
    public void TheTriangleCountFallsAsTheCameraRetreats()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new();
        Geometry near = scene.CreateSphereGeometry(1f, 48, 32);
        Geometry far = scene.CreateSphereGeometry(1f, 8, 6);
        Node node = scene.AddMesh(near, scene.CreateMaterial(Vector4.One));
        node.SetLevelsOfDetail((near, 10f), (far, float.PositiveInfinity));

        Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 5f));
        camera.LookAt(Vector3.Zero);

        renderer.Render(scene, camera);
        int close = renderer.Stats.Triangles;

        camera.Position = new Vector3(0f, 0f, 40f);
        renderer.Render(scene, camera);
        int distant = renderer.Stats.Triangles;

        Assert.True(close > distant, $"detail should drop with distance: {close} then {distant}");
        Assert.True(distant > 0, "the far level should still draw something");
    }

    [Fact]
    public void ANullLevelMakesTheNodeDisappear()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new();
        Geometry sphere = scene.CreateSphereGeometry(1f, 16, 12);
        Node node = scene.AddMesh(sphere, scene.CreateMaterial(Vector4.One));
        node.SetLevelsOfDetail((sphere, 10f), (null, float.PositiveInfinity));

        Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 5f));
        camera.LookAt(Vector3.Zero);

        renderer.Render(scene, camera);
        Assert.True(renderer.Stats.Triangles > 0, "the node should draw up close");

        camera.Position = new Vector3(0f, 0f, 60f);
        renderer.Render(scene, camera);
        Assert.Equal(0, renderer.Stats.Triangles);
    }

    [Fact]
    public void LevelsAreSortedHoweverTheyAreGiven()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new();
        Geometry near = scene.CreateSphereGeometry(1f, 48, 32);
        Geometry far = scene.CreateSphereGeometry(1f, 8, 6);
        Node node = scene.AddMesh(near, scene.CreateMaterial(Vector4.One));
        // Far level first: the lookup takes the first range the distance falls
        // in, so an unsorted list would answer with the far level up close.
        node.SetLevelsOfDetail((far, float.PositiveInfinity), (near, 10f));

        Node camera = scene.AddCamera(Camera.Perspective(50f.ToRadians()), new Vector3(0f, 0f, 5f));
        camera.LookAt(Vector3.Zero);
        renderer.Render(scene, camera);
        int close = renderer.Stats.Triangles;

        camera.Position = new Vector3(0f, 0f, 40f);
        renderer.Render(scene, camera);
        Assert.True(close > renderer.Stats.Triangles, "the levels were not sorted into range order");
    }
}
