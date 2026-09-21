using System.Numerics;
using ThreeNet.Plugins;
using ThreeNet.Scenes;
using Xunit;

namespace ThreeNet.Tests;

public class SceneDocumentTests
{
    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"threenet-doc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void DocumentsRoundTripThroughJson()
    {
        SceneDocument document = SceneDocument.CreateDefault();
        document.Name = "Test scene";
        document.Nodes[1].Children.Add(new NodeDefinition
        {
            Id = "child-1",
            Name = "child",
            Position = new Vector3(0f, 1.5f, 0f),
            GeometryId = "box",
            MaterialId = "box",
            Scale = new Vector3(0.5f),
        });

        string json = document.ToJson();
        Assert.Contains("\"Kind\": \"Box\"", json);

        SceneDocument loaded = SceneDocument.FromJson(json);
        Assert.Equal("Test scene", loaded.Name);
        Assert.Equal(document.AllNodes().Count(), loaded.AllNodes().Count());
        NodeDefinition child = loaded.Find("child-1")!;
        Assert.Equal(new Vector3(0f, 1.5f, 0f), child.Position);
        Assert.Equal(new Vector3(0.5f), child.Scale);
        Assert.Equal("box-1", loaded.ParentOf(child)!.Id);
        Assert.Equal(LightType.Directional, loaded.Find("sun-1")!.Light!.Type);
        Assert.True(loaded.Find("camera-1")!.Camera!.Active);

        string directory = TempDirectory();
        string path = Path.Combine(directory, "scene.json");
        loaded.Save(path);
        SceneDocument fromDisk = SceneDocument.Load(path);
        Assert.Equal(directory, fromDisk.BaseDirectory);
        Assert.Equal(loaded.AllNodes().Count(), fromDisk.AllNodes().Count());
    }

    [Fact]
    public void BuildCreatesNodesMaterialsAndPhysics()
    {
        using Scene scene = new();
        SceneDocument document = SceneDocument.CreateDefault();
        SceneBuildResult result = document.Build(scene);

        Assert.Empty(result.Warnings);
        Assert.Equal(4, result.Nodes.Count);
        Assert.Equal(new Vector3(0f, 0.5f, 0f), result.Nodes["box-1"].Position);
        Assert.NotNull(result.ActiveCamera);
        Assert.Equal(result.Nodes["camera-1"], scene.ActiveCamera);
        Assert.Equal(LightType.Directional, result.Nodes["sun-1"].Light!.Value.Type);
        Assert.True(scene.Physics.HasBody(result.Nodes["box-1"]));
        Assert.False(scene.Physics.HasBody(result.Nodes["ground-1"]), "the ground is a collider only");

        // The box falls onto the ground collider.
        for (int i = 0; i < 90; i++)
        {
            scene.Physics.Step(1f / 60f);
        }

        Assert.Equal(0.5f, result.Nodes["box-1"].Position.Y, 1);
    }

    [Fact]
    public void MissingAssetsAreReportedNotThrown()
    {
        using Scene scene = new();
        SceneDocument document = new()
        {
            Textures = { new TextureDefinition { Id = "missing", Path = "no-such-texture.png" } },
            Geometries = { GeometryDefinition.Model("model", "no-such-model.glb"), GeometryDefinition.Box("box") },
            Materials = { new MaterialDefinition { Id = "m", BaseColorMap = "missing" } },
            Nodes =
            {
                new NodeDefinition { Id = "a", GeometryId = "model" },
                new NodeDefinition { Id = "b", GeometryId = "box", MaterialId = "m" },
            },
        };

        SceneBuildResult result = document.Build(scene);
        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains(result.Warnings, w => w.Contains("texture 'missing'"));
        Assert.Contains(result.Warnings, w => w.Contains("model 'model'"));
        Assert.Equal(2, result.Nodes.Count);
    }

    [Fact]
    public void PluginsLoadRunCommandsAndImportFiles()
    {
        string pluginPath = FindSamplePlugin();
        using PluginManager manager = new("ThreeNet.Tests");
        List<string> log = [];
        manager.Logged += log.Add;

        IReadOnlyList<LoadedPlugin> loaded = manager.Load(pluginPath);
        LoadedPlugin plugin = Assert.Single(loaded);
        Assert.Equal("Scatter tools", plugin.Instance.Name);
        Assert.Equal(2, plugin.Commands.Count);
        Assert.Contains(log, line => line.Contains("loaded plugin 'Scatter tools'"));

        SceneDocument document = SceneDocument.CreateDefault();
        int before = document.AllNodes().Count();
        int rebuilds = 0;
        PluginContext context = new(document) { Log = log.Add, RequestRebuild = () => rebuilds++ };

        PluginCommand scatter = manager.Commands.First(c => c.Name.Contains("Scatter"));
        Assert.True(manager.Run(scatter, context));
        Assert.Equal(before + 24, document.AllNodes().Count());
        Assert.Equal(1, rebuilds);

        Assert.True(manager.Run(manager.Commands.First(c => c.Name.Contains("Ring")), context));
        Assert.Equal(8, document.AllNodes().Count(n => n.Light?.Type == LightType.Point));

        // The importer handles a format the core knows nothing about.
        string points = Path.Combine(TempDirectory(), "cloud.points");
        File.WriteAllLines(points, ["0,1,0", "2,0.5,-1,0.25", "not a point"]);
        Assert.True(manager.Import(points, context));
        Assert.Equal(2, document.AllNodes().Count(n => n.Name.StartsWith("point ")));
        Assert.False(manager.Import("nothing.unknown", context));

        // Built documents keep working after the plugin edits.
        using Scene scene = new();
        Assert.Empty(document.Build(scene).Warnings);

        manager.Unload(plugin);
        Assert.Empty(manager.Plugins);
        Assert.Empty(manager.Commands);
    }

    [Fact]
    public void LoadingANonPluginAssemblyIsHarmless()
    {
        using PluginManager manager = new("ThreeNet.Tests");
        Assert.Empty(manager.Load(typeof(SceneDocumentTests).Assembly.Location));
        Assert.Empty(manager.LoadDirectory(Path.Combine(TempDirectory(), "does-not-exist")));
        Assert.Throws<InvalidOperationException>(() => manager.AddCommand(new PluginCommand("x", "y", _ => { })));
    }

    /// <summary>Finds the built sample plugin without referencing it (it must load in its own context).</summary>
    private static string FindSamplePlugin()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "samples", "ThreePlugin.Sample")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        string[] candidates = Directory.GetFiles(
            Path.Combine(directory.FullName, "samples", "ThreePlugin.Sample", "bin"),
            "ThreePlugin.Sample.dll",
            SearchOption.AllDirectories);
        Assert.NotEmpty(candidates);
        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }
}
