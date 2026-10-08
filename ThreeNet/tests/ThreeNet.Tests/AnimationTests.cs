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

    [Fact]
    public void MorphTargetsArriveFromGltfAndDeformTheMesh()
    {
        using Scene scene = new();
        ImportResult result = scene.LoadGltf(TestAsset("morph-cube.glb"));

        Node node = result.Root.Children.Count > 0 ? result.Root.Children[0] : result.Root;
        // The importer may place the mesh on a child, so find the node holding it.
        Node? mesh = node.MorphWeightCount > 0 ? node : FindMorphed(result.Root);
        Assert.NotNull(mesh);
        Assert.Equal(2, mesh!.MorphWeightCount);

        BoundingBox rest = scene.GetBounds(mesh);
        mesh.SetMorphWeight(0, 1f);        // 'stretch' lifts the top by one unit
        scene.UpdateAnimations(0f);
        BoundingBox stretched = scene.GetBounds(mesh);
        Assert.True(
            stretched.Max.Y > rest.Max.Y + 0.9f,
            $"the shape did not raise the cube: {rest.Max.Y} -> {stretched.Max.Y}");

        // Back to zero returns to the rest pose rather than drifting.
        mesh.SetMorphWeight(0, 0f);
        scene.UpdateAnimations(0f);
        Assert.Equal(rest.Max.Y, scene.GetBounds(mesh).Max.Y, 3);

        static Node? FindMorphed(Node node)
        {
            if (node.MorphWeightCount > 0)
            {
                return node;
            }

            foreach (Node child in node.Children)
            {
                if (FindMorphed(child) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }

    [Fact]
    public void MorphTargetsCanBeBuiltInCode()
    {
        using Scene scene = new();
        Geometry geometry = scene.CreateBoxGeometry(1f, 1f, 1f);
        Material material = scene.CreateMaterial(MaterialOptions.Pbr(Colors.White));
        Node node = scene.AddMesh(geometry, material, name: "box");

        (int vertices, _) = geometry.Counts;
        Vector3[] deltas = new Vector3[vertices];
        for (int i = 0; i < vertices; i++)
        {
            deltas[i] = new Vector3(0f, 2f, 0f);
        }

        int index = geometry.AddMorphTarget("rise", deltas);
        Assert.Equal(0, index);
        Assert.Equal(1, geometry.MorphTargetCount);
        Assert.Equal("rise", geometry.GetMorphTargetName(0));

        BoundingBox rest = scene.GetBounds(node);
        node.SetMorphWeight(0, 0.5f);
        scene.UpdateAnimations(0f);
        BoundingBox raised = scene.GetBounds(node);
        Assert.Equal(rest.Max.Y + 1f, raised.Max.Y, 3);

        Assert.Equal([0.5f], node.GetMorphWeights());
    }
}
