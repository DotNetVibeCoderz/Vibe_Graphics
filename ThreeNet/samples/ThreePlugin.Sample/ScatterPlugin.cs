using System.Globalization;
using System.Numerics;
using ThreeNet;
using ThreeNet.Plugins;
using ThreeNet.Scenes;

namespace ThreePlugin.Sample;

/// <summary>
/// Example plugin: commands that generate scene content and an importer for a
/// simple point cloud text format. Build it and copy the DLL into the editor's
/// <c>plugins</c> folder.
/// </summary>
public sealed class ScatterPlugin : IThreeNetPlugin
{
    public string Name => "Scatter tools";

    public string Description => "Generates props and lights, and imports .points files.";

    public string Version => "1.0";

    public void Initialize(IPluginHost host)
    {
        host.AddCommand(new PluginCommand("Scatter 24 boxes", "Generate", ScatterBoxes)
        {
            Description = "Adds two dozen boxes with random positions and sizes.",
        });
        host.AddCommand(new PluginCommand("Ring of lights", "Generate", RingOfLights)
        {
            Description = "Adds eight coloured point lights in a circle.",
        });
        host.AddImporter(new PluginImporter("Point cloud (.points)", [".points"], ImportPoints));
    }

    private static MaterialDefinition EnsureMaterial(SceneDocument document, string id, Vector4 color)
    {
        MaterialDefinition? material = document.Materials.FirstOrDefault(m => m.Id == id);
        if (material is null)
        {
            material = new MaterialDefinition { Id = id, BaseColor = color, Roughness = 0.6f };
            document.Materials.Add(material);
        }

        return material;
    }

    private static GeometryDefinition EnsureGeometry(SceneDocument document, GeometryDefinition definition)
    {
        GeometryDefinition? existing = document.Geometries.FirstOrDefault(g => g.Id == definition.Id);
        if (existing is not null)
        {
            return existing;
        }

        document.Geometries.Add(definition);
        return definition;
    }

    private static void ScatterBoxes(PluginContext context)
    {
        SceneDocument document = context.Document;
        EnsureGeometry(document, GeometryDefinition.Box("plugin-box"));
        EnsureMaterial(document, "plugin-box", new Vector4(0.7f, 0.7f, 0.75f, 1f));
        Random random = new();
        for (int i = 0; i < 24; i++)
        {
            float size = 0.3f + (random.NextSingle() * 0.9f);
            document.Nodes.Add(new NodeDefinition
            {
                Id = document.NextId("scatter"),
                Name = $"scattered box {i + 1}",
                Position = new Vector3((random.NextSingle() - 0.5f) * 16f, size * 0.5f, (random.NextSingle() - 0.5f) * 16f),
                Rotation = new Vector3(0f, random.NextSingle() * MathF.Tau, 0f),
                Scale = new Vector3(size),
                GeometryId = "plugin-box",
                MaterialId = "plugin-box",
                Physics = new PhysicsDefinition { HalfExtents = new Vector3(size * 0.5f) },
            });
        }

        context.Report("scattered 24 boxes");
        context.RequestRebuild?.Invoke();
    }

    private static void RingOfLights(PluginContext context)
    {
        SceneDocument document = context.Document;
        for (int i = 0; i < 8; i++)
        {
            float angle = i / 8f * MathF.Tau;
            float hue = i / 8f;
            document.Nodes.Add(new NodeDefinition
            {
                Id = document.NextId("plugin-light"),
                Name = $"ring light {i + 1}",
                Position = new Vector3(MathF.Cos(angle) * 6f, 2.5f, MathF.Sin(angle) * 6f),
                Light = new LightDefinition
                {
                    Type = LightType.Point,
                    Intensity = 12f,
                    Range = 9f,
                    Color = new Vector3(
                        Math.Clamp(MathF.Abs((hue * 6f) - 3f) - 1f, 0f, 1f),
                        Math.Clamp(2f - MathF.Abs((hue * 6f) - 2f), 0f, 1f),
                        Math.Clamp(2f - MathF.Abs((hue * 6f) - 4f), 0f, 1f)),
                },
            });
        }

        context.Report("added a ring of 8 lights");
        context.RequestRebuild?.Invoke();
    }

    /// <summary>Reads "x,y,z[,radius]" lines and adds a sphere per point.</summary>
    private static void ImportPoints(PluginContext context, string path)
    {
        SceneDocument document = context.Document;
        EnsureGeometry(document, GeometryDefinition.Sphere("plugin-point", 1f));
        EnsureMaterial(document, "plugin-point", new Vector4(0.2f, 0.7f, 0.9f, 1f));
        int count = 0;
        foreach (string line in File.ReadLines(path))
        {
            string[] parts = line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !float.TryParse(parts[0], CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(parts[1], CultureInfo.InvariantCulture, out float y)
                || !float.TryParse(parts[2], CultureInfo.InvariantCulture, out float z))
            {
                continue;
            }

            float radius = parts.Length > 3 && float.TryParse(parts[3], CultureInfo.InvariantCulture, out float r) ? r : 0.15f;
            document.Nodes.Add(new NodeDefinition
            {
                Id = document.NextId("point"),
                Name = $"point {++count}",
                Position = new Vector3(x, y, z),
                Scale = new Vector3(radius),
                GeometryId = "plugin-point",
                MaterialId = "plugin-point",
            });
        }

        context.Report($"imported {count} points from {Path.GetFileName(path)}");
        context.RequestRebuild?.Invoke();
    }
}
