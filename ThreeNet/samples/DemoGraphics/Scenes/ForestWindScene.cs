using System.Numerics;
using DemoGraphics.Framework;
using ThreeNet;
using ThreeNet.Avalonia;
using ThreeNet.Interop;

namespace DemoGraphics.Scenes;

/// <summary>
/// A forest, to see what thousands of objects cost and what keeps them
/// affordable: one geometry per tree type shared by every instance, frustum
/// culling doing most of the work, a distance cut-off standing in for LOD, and
/// the wind moving all of it from a vertex shader.
/// </summary>
public sealed class ForestWindScene : DemoScene
{
    private readonly List<Node> _trees = [];
    private readonly List<float> _distances = [];
    private int _imported;
    /// <summary>Index in <see cref="_trees"/> where the imported models start.</summary>
    private int _importedFrom = int.MaxValue;
    private SkyDome _sky = null!;
    private Material _foliage = null!;
    private Material _grass = null!;
    private Material _ground = null!;
    private Node _sun = null!;
    private Node _skyLight = null!;
    private int _hidden;

    public ForestWindScene()
    {
        Declare(
            DemoParameter.Slider("trees", "Trees", 1500f, 100f, 12000f, "", 100f,
                "Each tree is one draw call, so this is the draw call dial.", requiresRebuild: true),
            DemoParameter.Slider("variants", "Tree variants", 8f, 1f, 16f, "", 1f,
                "Geometries shared between instances; also the number of sway phases.", requiresRebuild: true),
            DemoParameter.Slider("cutoff", "Visible distance", 220f, 30f, 400f, "m", 5f,
                "Trees past this distance are switched off, which is LOD at its bluntest."),
            DemoParameter.Slider("spread", "Forest radius", 170f, 40f, 300f, "m", 5f, requiresRebuild: true),
            DemoParameter.Toggle("grass", "Grass patches", true, requiresRebuild: true),
            DemoParameter.Toggle("models", "Imported trees", true,
                "Oak and pine GLBs, imported once and cloned; the read-out shows the cache.",
                requiresRebuild: true),
            DemoParameter.Toggle("shadows", "Trees cast shadows", true,
                "Shadow casters are gathered before culling, so this is a real cost."));

        AddPreset("Grove", "Clear", 9f, ("trees", 300f), ("spread", 70f), ("cutoff", 220f));
        AddPreset("Forest", "Partly cloudy", 10.5f, ("trees", 1500f), ("spread", 170f));
        AddPreset("Dense stand", "Overcast", 14f, ("trees", 5000f), ("spread", 220f), ("shadows", 0f));
        AddPreset("Stress: 12000", null, null, ("trees", 12000f), ("spread", 280f), ("shadows", 0f), ("grass", 0f));
        AddPreset("Storm", "Storm", 16f, ("trees", 1500f));
        AddPreset("Snowfall", "Snow", 11f, ("trees", 1500f));
    }

    public override string Id => "VEG";

    public override string Title => "Forest and wind";

    public override string Category => "World";

    public override string Summary =>
        "Thousands of trees from a handful of shared geometries, swaying in the shared wind.";

    public override IReadOnlyList<string> Features =>
        ["Frustum culling", "Shared geometry", "Distance cut-off", "Custom vertex shader", "Cascaded shadows"];

    public override RendererOptions ConfigureRenderer(RendererOptions options) => options with
    {
        Shadows = true,
        ShadowMapSize = 2048,
        ShadowCascades = 3,
        ShadowDistance = 90f,
        Ssao = false,
        Bloom = true,
        BloomIntensity = 0.35f,
    };

    public override void ConfigureCamera(OrbitController orbit)
    {
        orbit.Target = new Vector3(0f, 6f, 0f);
        orbit.Distance = 48f;
        orbit.Yaw = 1.2f;
        orbit.Pitch = 0.12f;
        orbit.MaxDistance = 420f;
    }

    public override IReadOnlyList<CameraKey> CameraPath =>
    [
        new(new Vector3(0f, 6f, 0f), 48f, 1.2f, 0.12f, 0f),
        new(new Vector3(20f, 3f, -20f), 18f, 2.4f, 0.04f, 4f),
        new(new Vector3(0f, 20f, 0f), 160f, 3.8f, 0.42f, 8f),
        new(new Vector3(0f, 6f, 0f), 48f, 5.2f, 0.12f, 12f),
    ];

    public override IEnumerable<(string Label, string Value)> Metrics()
    {
        yield return ("Trees placed", _trees.Count.ToString());
        yield return ("Beyond cut-off", _hidden.ToString());
        yield return ("Wind", $"{World.WindSpeed:F1} m/s");
        if (_imported > 0)
        {
            AssetStats assets = Scene.AssetStats;
            yield return ("Imported trees", $"{_imported} from {assets.CachedModels} models");
            yield return ("Model cache", $"{assets.CacheHits} hits, {assets.CacheMisses} imports");
        }
    }

    protected override void OnBuild()
    {
        _sky = SkyDome.Add(Scene);

        Geometry terrain = Procedural.Ground(Scene, 700f, 160, GroundHeight, 12f);
        Shader terrainShader = Scene.CreateShader(ShaderHooks.Terrain, ShaderLanguage.Wgsl, "terrain");
        _ground = Scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.92f) with { Shader = terrainShader });
        Scene.AddMesh(terrain, _ground, name: "ground");

        Shader foliageShader = Scene.CreateShader(ShaderHooks.Foliage, ShaderLanguage.Wgsl, "foliage");
        _foliage = Scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.82f) with { Shader = foliageShader });
        // Grass blades are single sided quads, so neither face may be culled.
        _grass = Scene.CreateMaterial(MaterialOptions.Pbr(Colors.White, 0f, 0.82f) with
        {
            Shader = foliageShader,
            CullMode = CullMode.None,
        });

        int variants = Math.Max(1, (int)P("variants"));
        Geometry[] geometries = new Geometry[variants];
        for (int i = 0; i < variants; i++)
        {
            geometries[i] = Procedural.Tree(Scene, 6f + (i * 0.6f), Noise.Hash(i, 53), conifer: i % 3 != 0);
        }

        float spread = P("spread");
        int count = (int)P("trees");
        for (int i = 0; i < count; i++)
        {
            // A golden angle spiral gives an even, deterministic scatter without
            // the clumping a plain random pair produces.
            float t = (i + 0.5f) / count;
            float radius = MathF.Sqrt(t) * spread;
            float angle = i * 2.399963f;
            float jitter = (Noise.Hash(i, 601) - 0.5f) * 6f;
            float x = (MathF.Sin(angle) * radius) + jitter;
            float z = (MathF.Cos(angle) * radius) + jitter;
            float y = GroundHeight(x, z);

            Node tree = Scene.AddMesh(geometries[i % variants], _foliage, name: $"tree-{i}");
            tree.Position = new Vector3(x, y, z);
            float scale = 0.7f + (Noise.Hash(i, 607) * 1.1f);
            tree.Scale = new Vector3(scale);
            tree.EulerAngles = new Vector3(0f, Noise.Hash(i, 613) * MathF.Tau, 0f);
            _trees.Add(tree);
            _distances.Add(MathF.Sqrt((x * x) + (z * z)));
        }

        if (B("models"))
        {
            _importedFrom = _trees.Count;
            PlaceImportedTrees(spread);
        }

        if (B("grass"))
        {
            BuildGrass(spread);
        }

        _sun = Scene.AddLight(Light.Directional(Vector3.One, 4f) with { CastShadow = true, ShadowNormalBias = 2.6f }, name: "sun");
        _skyLight = Scene.AddLight(Light.Ambient(new Vector3(0.52f, 0.62f, 0.85f), 0.45f), name: "sky light");
    }

    protected override void OnApplyEnvironment()
    {
        _sky.Apply(World);

        _sun.Position = World.SunPosition * 140f;
        _sun.LookAt(Vector3.Zero);
        Light sun = _sun.Light!.Value;
        sun.Color = World.SunColor;
        sun.Intensity = World.SunIntensity;
        _sun.Light = sun;

        (Vector3 color, float intensity) = World.SkyLight;
        Light ambient = _skyLight.Light!.Value;
        ambient.Color = color;
        ambient.Intensity = intensity;
        _skyLight.Light = ambient;

        Vector3 horizon = Vector3.Lerp(new Vector3(0.42f, 0.56f, 0.80f), new Vector3(0.022f, 0.035f, 0.085f), World.NightFactor);
        Scene.Environment = Scene.Environment with
        {
            Background = new Vector4(horizon * 0.35f, 1f),
            FogColor = horizon,
            FogDensity = MathF.Max(World.FogDensity, 0.004f),
            FogStart = 70f,
            AmbientIntensity = 0.02f,
        };

        _ground.Update(options => options with
        {
            // Sand only in the hollows, grass everywhere else, rock on the steep.
            Custom0 = new Vector4(-6f, -1f, 0.5f, World.SnowAmount),
            Custom1 = new Vector4(World.Wetness, 0f, 0f, 0f),
        });

        Vector4 bark = new(Palette.Rgb(0x3B2E22), World.WindDirection.ToRadians());
        Vector4 leaf = new(
            Vector3.Lerp(Palette.Rgb(0x27431F), Palette.Rgb(0x5E6B3A), World.SnowAmount * 0.4f),
            World.WindStrength);
        _foliage.Update(options => options with { Custom0 = bark, Custom1 = leaf });
        _grass.Update(options => options with
        {
            Custom0 = bark,
            // Grass leans harder than a trunk in the same wind.
            Custom1 = new Vector4(leaf.X, leaf.Y, leaf.Z, MathF.Min(1f, (World.WindStrength * 1.8f) + 0.12f)),
        });
    }

    protected override void OnApplyParameters()
    {
        OnApplyEnvironment();

        float cutoff = P("cutoff");
        bool shadows = B("shadows");
        _hidden = 0;
        for (int i = 0; i < _trees.Count; i++)
        {
            bool visible = _distances[i] <= cutoff;
            _trees[i].Visible = visible;
            if (i >= _importedFrom)
            {
                // An imported root carries no mesh of its own; its children do.
                _trees[i].SetShadowsRecursive(shadows, receive: true);
            }
            else
            {
                _trees[i].CastShadow = shadows;
            }

            if (!visible)
            {
                _hidden++;
            }
        }
    }

    public override void Update(float deltaSeconds, double totalSeconds) => _sky.Follow(Viewer);

    protected override void OnUnload()
    {
        _trees.Clear();
        _distances.Clear();
        _imported = 0;
        _importedFrom = int.MaxValue;
    }

    /// <summary>
    /// Drops in the modelled trees the repository ships. `LoadModelCached`
    /// imports each file once and clones it after that, so ninety trees cost two
    /// imports - the read-out shows the hits. Missing files are skipped, because
    /// the procedural forest is already standing.
    /// </summary>
    private void PlaceImportedTrees(float spread)
    {
        string[] models =
        [
            Path.Combine(AppContext.BaseDirectory, "Assets", "pine-tree.glb"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "oak-tree.glb"),
        ];
        if (!models.Any(File.Exists))
        {
            return;
        }

        for (int i = 0; i < 90; i++)
        {
            string path = models[i % models.Length];
            if (!File.Exists(path))
            {
                continue;
            }

            float angle = i * 2.399963f;
            float radius = 14f + (MathF.Sqrt((i + 0.5f) / 90f) * spread * 0.55f);
            float x = MathF.Sin(angle) * radius;
            float z = MathF.Cos(angle) * radius;

            ImportResult imported = Scene.LoadModelCached(path);
            Node root = imported.Root;
            root.Position = new Vector3(x, GroundHeight(x, z) - 0.05f, z);
            float scale = 0.9f + (Noise.Hash(i, 811) * 0.7f);
            root.Scale = new Vector3(scale);
            root.EulerAngles = new Vector3(0f, Noise.Hash(i, 821) * MathF.Tau, 0f);
            root.SetShadowsRecursive(cast: true, receive: true);
            _trees.Add(root);
            _distances.Add(radius);
            _imported++;
        }
    }

    private static float GroundHeight(float x, float z) =>
        (Noise.Fbm((x * 0.004f) + 9f, (z * 0.004f) + 4f, 5) - 0.5f) * 26f
        + ((Noise.Fbm(x * 0.03f, z * 0.03f, 3) - 0.5f) * 1.6f);

    /// <summary>
    /// Grass as a few big merged meshes rather than thousands of nodes: the same
    /// sway shader, but a handful of draw calls for the whole ground cover.
    /// </summary>
    private void BuildGrass(float spread)
    {
        const int patches = 16;
        for (int patch = 0; patch < patches; patch++)
        {
            MeshBuilder builder = new();
            float angle = MathF.Tau * patch / patches;
            Vector3 centre = new(MathF.Sin(angle) * spread * 0.45f, 0f, MathF.Cos(angle) * spread * 0.45f);
            for (int blade = 0; blade < 700; blade++)
            {
                float bx = centre.X + ((Noise.Hash(patch * 977 + blade, 11) - 0.5f) * spread * 0.5f);
                float bz = centre.Z + ((Noise.Hash(patch * 977 + blade, 13) - 0.5f) * spread * 0.5f);
                float by = GroundHeight(bx, bz);
                float height = 0.35f + (Noise.Hash(blade, patch) * 0.5f);
                float width = 0.05f;
                // Two crossed quads read as a tuft from any angle.
                builder.AddQuad(
                    new Vertex(new Vector3(bx - width, by, bz), Vector3.UnitY, new Vector2(1f, Noise.Hash(blade, 17))),
                    new Vertex(new Vector3(bx + width, by, bz), Vector3.UnitY, new Vector2(1f, Noise.Hash(blade, 17))),
                    new Vertex(new Vector3(bx + width, by + height, bz), Vector3.UnitY, new Vector2(1f, Noise.Hash(blade, 17))),
                    new Vertex(new Vector3(bx - width, by + height, bz), Vector3.UnitY, new Vector2(1f, Noise.Hash(blade, 17))));
                builder.AddQuad(
                    new Vertex(new Vector3(bx, by, bz - width), Vector3.UnitY, new Vector2(1f, Noise.Hash(blade, 19))),
                    new Vertex(new Vector3(bx, by, bz + width), Vector3.UnitY, new Vector2(1f, Noise.Hash(blade, 19))),
                    new Vertex(new Vector3(bx, by + height, bz + width), Vector3.UnitY, new Vector2(1f, Noise.Hash(blade, 19))),
                    new Vertex(new Vector3(bx, by + height, bz - width), Vector3.UnitY, new Vector2(1f, Noise.Hash(blade, 19))));
            }

            Node node = Scene.AddMesh(builder.Build(Scene), _grass, name: $"grass-{patch}");
            node.CastShadow = false;
        }
    }
}
