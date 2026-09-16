using System.Numerics;
using Xunit;

namespace ThreeNet.Tests;

public class AnimationTests
{
    internal static string TestAsset(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "rust", "threenet-core", "tests", "assets")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new DirectoryNotFoundException("rust/threenet-core/tests/assets not found")
            : Path.Combine(directory.FullName, "rust", "threenet-core", "tests", "assets", name);
    }

    [Fact]
    public void ClipsBuiltInCodeAnimateNodes()
    {
        using Scene scene = new();
        Node box = scene.AddMesh(scene.CreateBoxGeometry(), scene.CreateMaterial(Vector4.One));

        AnimationClip clip = scene.CreateAnimation("slide")
            .AddTranslation(box, [0f, 2f], [Vector3.Zero, new Vector3(4f, 0f, 0f)])
            .AddRotation(box, [0f, 2f], [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f)]);

        Assert.Equal("slide", clip.Name);
        Assert.Equal(2f, clip.Duration, 3);
        Assert.Contains(scene.Animations, a => a.Id == clip.Id);

        AnimationPlayer player = clip.Play(loop: false);
        scene.UpdateAnimations(1f);
        Assert.Equal(2f, box.Position.X, 3);
        Assert.Equal(1f, player.Time, 3);

        player.Speed = 2f;
        scene.UpdateAnimations(5f);
        Assert.Equal(4f, box.Position.X, 3);
        Assert.False(player.IsPlaying);

        Assert.Throws<ThreeNetException>(() => clip.AddChannel(box, AnimationPath.Scale, Interpolation.Linear, [0f, 1f], [1f, 1f]));
    }

    [Fact]
    public void FbxImportsGeometryMaterialsAndAnimation()
    {
        using Scene scene = new();
        ImportResult box = scene.LoadModel(TestAsset("phong_cube.fbx"));
        Assert.True(box.GeometryCount > 0);
        Assert.True(box.MaterialCount > 0);

        ImportResult rig = scene.LoadModel(TestAsset("animation_with_skeleton.fbx"));
        Assert.True(rig.AnimationCount > 0);
        BoundingBox before = scene.GetBounds(rig.Root);
        scene.Animations[^1].Play();
        scene.UpdateAnimations(scene.Animations[^1].Duration * 0.5f);
        Assert.NotEqual(before, scene.GetBounds(rig.Root));

        Assert.Throws<NotSupportedException>(() => scene.LoadModel("model.3ds"));
    }

    [Fact]
    public void ImportedSkinsDeformWhenPlayed()
    {
        using Scene scene = new();
        ImportResult import = scene.LoadGltf(TestAsset("RiggedSimple.glb"));
        Assert.Equal(1, import.SkinCount);
        Assert.True(import.AnimationCount > 0);

        BoundingBox before = scene.GetBounds(import.Root);
        scene.Animations[0].Play();
        scene.UpdateAnimations(1.0f);
        BoundingBox after = scene.GetBounds(import.Root);
        Assert.NotEqual(before, after);
    }
}
