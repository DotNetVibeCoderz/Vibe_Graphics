using System.Numerics;
using ThreeNet;
using ThreeNet.Plugins;
using ThreeNet.Scenes;

namespace ThreeEditor.Editor;

/// <summary>
/// The editor's state: the scene document being edited, the live
/// <see cref="Scene"/> built from it, selection, undo history and the plugin
/// manager. The UI layer only talks to this class.
/// </summary>
public sealed class EditorSession : IDisposable
{
    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];
    private string _lastSnapshot;

    public EditorSession()
    {
        Document = SceneDocument.CreateDefault();
        _lastSnapshot = Document.ToJson();
        Plugins = new PluginManager("ThreeEditor");
        Plugins.Logged += message => Log(message);
    }

    public SceneDocument Document { get; private set; }

    /// <summary>The live scene; rebuilt whenever the document changes structurally.</summary>
    public Scene Scene { get; private set; } = new();

    public SceneBuildResult? Build { get; private set; }

    public PluginManager Plugins { get; }

    /// <summary>File the document was loaded from or saved to.</summary>
    public string? FilePath { get; private set; }

    public bool IsModified { get; private set; }

    public NodeDefinition? Selection { get; private set; }

    /// <summary>Physics runs only in play mode.</summary>
    public bool IsPlaying { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public event Action? SceneRebuilt;

    public event Action? SelectionChanged;

    public event Action? DocumentChanged;

    public event Action<string>? Logged;

    public void Log(string message) => Logged?.Invoke(message);

    /// <summary>The live node of a document node, when the scene is built.</summary>
    public Node? NodeFor(NodeDefinition? definition) =>
        definition is not null && Build is { } build && build.Nodes.TryGetValue(definition.Id, out Node? node) ? node : null;

    /// <summary>The document node of a live node (walking up to the nearest known ancestor).</summary>
    public NodeDefinition? DefinitionFor(Node? node)
    {
        if (Build is not { } build)
        {
            return null;
        }

        for (Node? current = node; current is not null; current = current.Parent)
        {
            foreach ((string id, Node built) in build.Nodes)
            {
                if (built.Equals(current))
                {
                    return Document.Find(id);
                }
            }
        }

        return null;
    }

    public void Select(NodeDefinition? node)
    {
        if (ReferenceEquals(Selection, node))
        {
            return;
        }

        Selection = node;
        SelectionChanged?.Invoke();
    }

    /// <summary>Rebuilds the live scene from the document, keeping the selection.</summary>
    public void Rebuild()
    {
        string? selectedId = Selection?.Id;
        Scene previous = Scene;
        Scene = new Scene();
        Build = Document.Build(Scene);
        foreach (string warning in Build.Warnings)
        {
            Log(warning);
        }

        previous.Dispose();
        Selection = selectedId is null ? null : Document.Find(selectedId);
        SceneRebuilt?.Invoke();
        SelectionChanged?.Invoke();
    }

    /// <summary>Records the document state so the next change can be undone.</summary>
    public void BeginChange()
    {
        _lastSnapshot = Document.ToJson();
    }

    /// <summary>
    /// Commits a change: pushes the previous state on the undo stack and, when
    /// <paramref name="rebuild"/> is set, rebuilds the scene.
    /// </summary>
    public void CommitChange(bool rebuild = true)
    {
        _undo.Add(_lastSnapshot);
        if (_undo.Count > 100)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
        IsModified = true;
        _lastSnapshot = Document.ToJson();
        if (rebuild)
        {
            Rebuild();
        }

        DocumentChanged?.Invoke();
    }

    /// <summary>Runs an edit between <see cref="BeginChange"/> and <see cref="CommitChange"/>.</summary>
    public void Edit(Action change, bool rebuild = true)
    {
        BeginChange();
        change();
        CommitChange(rebuild);
    }

    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Add(Document.ToJson());
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Add(Document.ToJson());
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
    }

    private void Restore(string json)
    {
        string? baseDirectory = Document.BaseDirectory;
        Document = SceneDocument.FromJson(json);
        Document.BaseDirectory = baseDirectory;
        _lastSnapshot = json;
        IsModified = true;
        Rebuild();
        DocumentChanged?.Invoke();
    }

    // --------------------------------------------------------------- files

    public void New()
    {
        Document = SceneDocument.CreateDefault();
        FilePath = null;
        IsModified = false;
        _undo.Clear();
        _redo.Clear();
        _lastSnapshot = Document.ToJson();
        Rebuild();
        DocumentChanged?.Invoke();
    }

    public void Open(string path)
    {
        Document = SceneDocument.Load(path);
        FilePath = path;
        IsModified = false;
        _undo.Clear();
        _redo.Clear();
        _lastSnapshot = Document.ToJson();
        Rebuild();
        DocumentChanged?.Invoke();
        Log($"opened {Path.GetFileName(path)}");
    }

    public void Save(string path)
    {
        Document.Save(path);
        FilePath = path;
        IsModified = false;
        DocumentChanged?.Invoke();
        Log($"saved {Path.GetFileName(path)}");
    }

    // ------------------------------------------------------------ editing

    /// <summary>Adds a node under the selection (or at the root) and selects it.</summary>
    public NodeDefinition Add(NodeDefinition node, bool asChildOfSelection = true)
    {
        Edit(() =>
        {
            if (asChildOfSelection && Selection is { } parent)
            {
                parent.Children.Add(node);
            }
            else
            {
                Document.Nodes.Add(node);
            }
        });

        Select(Document.Find(node.Id));
        return node;
    }

    public NodeDefinition AddPrimitive(GeometryKind kind, string name)
    {
        string geometryId = EnsureGeometry(kind);
        string materialId = EnsureMaterial();
        return Add(new NodeDefinition
        {
            Id = Document.NextId(name.ToLowerInvariant()),
            Name = name,
            Position = SpawnPosition(),
            GeometryId = geometryId,
            MaterialId = materialId,
        }, asChildOfSelection: false);
    }

    public NodeDefinition AddLight(LightType type)
    {
        return Add(new NodeDefinition
        {
            Id = Document.NextId("light"),
            Name = $"{type} light",
            Position = type == LightType.Directional ? new Vector3(4f, 6f, 4f) : SpawnPosition() + new Vector3(0f, 2f, 0f),
            Rotation = type == LightType.Directional ? new Vector3(-0.9f, 0.7f, 0f) : Vector3.Zero,
            Light = new LightDefinition
            {
                Type = type,
                Intensity = type == LightType.Directional ? 3f : 12f,
                Range = type == LightType.Directional ? 0f : 10f,
                CastShadow = type != LightType.Point,
            },
        }, asChildOfSelection: false);
    }

    public NodeDefinition AddCamera() => Add(new NodeDefinition
    {
        Id = Document.NextId("camera"),
        Name = "camera",
        Position = new Vector3(0f, 2.5f, 7f),
        Camera = new CameraDefinition(),
    }, asChildOfSelection: false);

    /// <summary>Imports a model file as a node.</summary>
    public NodeDefinition AddModel(string path)
    {
        string id = Document.NextId("model");
        string relative = Relative(path);
        Document.Geometries.Add(GeometryDefinition.Model(id, relative));
        return Add(new NodeDefinition
        {
            Id = Document.NextId(Path.GetFileNameWithoutExtension(path).ToLowerInvariant()),
            Name = Path.GetFileNameWithoutExtension(path),
            Position = SpawnPosition(),
            GeometryId = id,
        }, asChildOfSelection: false);
    }

    public void Delete(NodeDefinition node)
    {
        Edit(() => Document.Remove(node));
        if (ReferenceEquals(Selection, node))
        {
            Select(null);
        }
    }

    public NodeDefinition? Duplicate(NodeDefinition node)
    {
        // Round tripping through JSON is the simplest deep copy of a subtree.
        NodeDefinition copy = SceneDocument.FromJson($"{{\"Nodes\":[{System.Text.Json.JsonSerializer.Serialize(node, SerializerOptions)}]}}").Nodes[0];
        Renumber(copy);
        copy.Name += " copy";
        copy.Position += new Vector3(1f, 0f, 0f);
        NodeDefinition? parent = Document.ParentOf(node);
        Edit(() =>
        {
            if (parent is null)
            {
                Document.Nodes.Add(copy);
            }
            else
            {
                parent.Children.Add(copy);
            }
        });
        Select(Document.Find(copy.Id));
        return copy;
    }

    private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new() { IncludeFields = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private void Renumber(NodeDefinition node)
    {
        node.Id = Document.NextId(node.Id.Split('-')[0]);
        foreach (NodeDefinition child in node.Children)
        {
            Renumber(child);
        }
    }

    /// <summary>Moves a node to a new parent (null for the root).</summary>
    public void Reparent(NodeDefinition node, NodeDefinition? parent)
    {
        if (parent is not null && node.Flatten().Contains(parent))
        {
            Log("a node cannot become a child of itself");
            return;
        }

        Edit(() =>
        {
            Document.Remove(node);
            if (parent is null)
            {
                Document.Nodes.Add(node);
            }
            else
            {
                parent.Children.Add(node);
            }
        });
    }

    public string EnsureGeometry(GeometryKind kind)
    {
        string id = kind.ToString().ToLowerInvariant();
        if (Document.Geometries.All(g => g.Id != id))
        {
            Document.Geometries.Add(kind switch
            {
                GeometryKind.Sphere => GeometryDefinition.Sphere(id, 0.5f),
                GeometryKind.Plane => GeometryDefinition.Plane(id, 10f, 10f),
                GeometryKind.Cylinder => new GeometryDefinition { Id = id, Kind = kind, A = 0.5f, B = 0.5f, C = 1f },
                GeometryKind.Cone => new GeometryDefinition { Id = id, Kind = kind, A = 0.5f, C = 1f },
                GeometryKind.Torus => new GeometryDefinition { Id = id, Kind = kind, A = 0.6f, B = 0.2f },
                GeometryKind.Grid => new GeometryDefinition { Id = id, Kind = kind, A = 20f, Segments = 20 },
                _ => GeometryDefinition.Box(id),
            });
        }

        return id;
    }

    public string EnsureMaterial()
    {
        if (Document.Materials.Count == 0)
        {
            Document.Materials.Add(new MaterialDefinition { Id = "default", BaseColor = new Vector4(0.8f, 0.8f, 0.82f, 1f) });
        }

        return Document.Materials[0].Id;
    }

    /// <summary>A spot in front of the editor camera, on the ground.</summary>
    public Vector3 SpawnPosition() => new(0f, 0.5f, 0f);

    private string Relative(string path)
    {
        string? baseDirectory = Document.BaseDirectory;
        return baseDirectory is null ? path : Path.GetRelativePath(baseDirectory, path);
    }

    // --------------------------------------------------------------- play

    public void SetPlaying(bool playing)
    {
        if (IsPlaying == playing)
        {
            return;
        }

        IsPlaying = playing;
        if (playing)
        {
            // Start from the saved state so stopping can restore it.
            _lastSnapshot = Document.ToJson();
        }
        else
        {
            Restore(_lastSnapshot);
        }

        Log(playing ? "play" : "stopped, scene restored");
    }

    /// <summary>Advances physics while playing.</summary>
    public void Update(float deltaSeconds)
    {
        if (IsPlaying)
        {
            Scene.Physics.Step(deltaSeconds);
        }
    }

    /// <summary>Loads plugins from the default folder.</summary>
    public void LoadPlugins()
    {
        foreach (LoadedPlugin plugin in Plugins.Plugins.ToArray())
        {
            Plugins.Unload(plugin);
        }

        string directory = PluginManager.DefaultDirectory;
        Directory.CreateDirectory(directory);
        IReadOnlyList<LoadedPlugin> loaded = Plugins.LoadDirectory(directory);
        Log(loaded.Count == 0 ? $"no plugins in {directory}" : $"{loaded.Count} plugin(s) loaded");
    }

    public PluginContext PluginContext() => new(Document)
    {
        Selection = Selection,
        Scene = Scene,
        Log = Log,
        RequestRebuild = () =>
        {
            IsModified = true;
            Rebuild();
            DocumentChanged?.Invoke();
        },
    };

    public void Dispose()
    {
        Plugins.Dispose();
        Scene.Dispose();
    }
}
