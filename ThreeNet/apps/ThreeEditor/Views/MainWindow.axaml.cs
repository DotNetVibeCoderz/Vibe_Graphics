using System.Numerics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using ThreeEditor.Editor;
using ThreeNet;
using ThreeNet.Avalonia;
using ThreeNet.Plugins;
using ThreeNet.Scenes;
using Key = Avalonia.Input.Key;
using KeyModifiers = Avalonia.Input.KeyModifiers;
using MouseButton = ThreeNet.MouseButton;

namespace ThreeEditor.Views;

/// <summary>
/// The editor shell: scene tree, viewport and inspector around an
/// <see cref="EditorSession"/>. Selection and dragging use the library's
/// <see cref="InteractionManager"/>; everything else edits the scene document
/// and rebuilds the viewport.
/// </summary>
public partial class MainWindow : Window
{
    private readonly EditorSession _session = new();
    private OrbitController? _orbit;
    private Node? _editorCamera;
    private Node? _gridNode;
    private bool _showGrid = true;
    private bool _syncingTree;
    private int _frames;
    private double _statsTimer;

    public MainWindow()
    {
        InitializeComponent();

        Viewport.RendererOptions = RendererOptions.Default with
        {
            BgraOutput = true,
            VSync = false,
            MsaaSamples = 4,
            ToneMapping = ToneMapping.Aces,
            Shadows = true,
            ShadowMapSize = 2048,
            ShadowCascades = 3,
            Ssao = true,
            SsaoIntensity = 1.2f,
        };
        Viewport.Frame += OnFrameTick;
        Viewport.RenderFailed += (_, message) => Status($"renderer: {message}");

        _session.Logged += Status;
        _session.SceneRebuilt += OnSceneRebuilt;
        _session.SelectionChanged += OnSelectionChanged;
        _session.DocumentChanged += UpdateTitle;

        _session.LoadPlugins();
        RefreshPluginsMenu();
        _session.Rebuild();
        RefreshTree();
        UpdateTitle();

        KeyDown += OnWindowKeyDown;
        Closed += (_, _) => _session.Dispose();
    }

    // ------------------------------------------------------------- scene

    private void OnSceneRebuilt()
    {
        Scene scene = _session.Scene;

        // The editor camera lives outside the document.
        Vector3 position = _editorCamera?.Position ?? new Vector3(6f, 5f, 9f);
        _editorCamera = scene.AddCamera(Camera.Perspective(0.9f, 0.05f, 2000f), position, makeActive: false);
        _editorCamera.Name = "editor camera";

        Viewport.Scene = scene;
        Viewport.Camera = _editorCamera;

        Vector3 target = _orbit?.Target ?? Vector3.Zero;
        float distance = _orbit?.Distance ?? 12f;
        float yaw = _orbit?.Yaw ?? 0.6f;
        float pitch = _orbit?.Pitch ?? 0.35f;
        _orbit?.Dispose();
        _orbit = new OrbitController(Viewport, _editorCamera) { Target = target, Distance = distance, Yaw = yaw, Pitch = pitch };
        _orbit.Apply();

        ApplyGrid();
        HookInteraction();
        RefreshTree();
    }

    /// <summary>Click to select, drag to move on the ground plane.</summary>
    private void HookInteraction()
    {
        if (Viewport.Interaction is not { } interaction)
        {
            return;
        }

        interaction.Camera = _editorCamera;
        interaction.Event += e =>
        {
            if (e.Kind == NodeEventKind.Click && e.Button == MouseButton.Left)
            {
                _session.Select(_session.DefinitionFor(e.HitNode));
            }
        };

        // Every node of the built scene can be dragged; the document follows.
        if (_session.Build is { } build)
        {
            foreach (Node node in build.Nodes.Values)
            {
                interaction.MakeDraggable(node, DragMode.GroundPlane);
                interaction.On(node, NodeEventKind.DragStart, _ => _session.BeginChange());
                interaction.On(node, NodeEventKind.DragEnd, e => OnNodeDragged(e.Target));
            }
        }
    }

    private void OnNodeDragged(Node node)
    {
        if (_session.DefinitionFor(node) is not { } definition)
        {
            return;
        }

        definition.Position = node.Position;
        _session.Select(definition);
        _session.CommitChange(rebuild: false);
        RefreshInspector();
        Status($"moved {definition.Name} to {definition.Position.X:0.00}, {definition.Position.Y:0.00}, {definition.Position.Z:0.00}");
    }

    private void ApplyGrid()
    {
        _gridNode = null;
        if (!_showGrid)
        {
            return;
        }

        Scene scene = _session.Scene;
        Material material = scene.CreateMaterial(MaterialOptions.Basic(new System.Numerics.Vector4(0.35f, 0.4f, 0.5f, 1f)));
        _gridNode = scene.AddMesh(scene.CreateGridGeometry(40f, 40), material, name: "editor grid");
        _gridNode.CastShadow = false;
        _gridNode.ReceiveShadow = false;
    }

    private void OnFrameTick(object? sender, FrameEventArgs e)
    {
        _session.Update(e.DeltaSeconds);

        _frames++;
        _statsTimer += e.DeltaSeconds;
        if (_statsTimer >= 0.5)
        {
            FrameStats stats = Viewport.Stats;
            StatsText.Text = $"{_frames / _statsTimer:0} fps · {stats.DrawCalls} draws · {stats.Triangles / 1000} k tris · {_session.Document.AllNodes().Count()} nodes";
            _frames = 0;
            _statsTimer = 0;
        }
    }

    // -------------------------------------------------------------- tree

    private void RefreshTree()
    {
        _syncingTree = true;
        List<TreeViewItem> roots = [.. _session.Document.Nodes.Select(CreateTreeItem)];
        SceneTree.ItemsSource = roots;
        SelectInTree(roots, _session.Selection);
        _syncingTree = false;
    }

    private TreeViewItem CreateTreeItem(NodeDefinition node)
    {
        string icon = node.Light is not null ? "◈" : node.Camera is not null ? "▣" : node.GeometryId is not null ? "▢" : "·";
        TreeViewItem item = new()
        {
            Header = $"{icon}  {node.Name}",
            Tag = node,
            IsExpanded = true,
            ItemsSource = node.Children.Select(CreateTreeItem).ToList(),
        };
        return item;
    }

    private void SelectInTree(IEnumerable<TreeViewItem> items, NodeDefinition? node)
    {
        foreach (TreeViewItem item in items)
        {
            if (ReferenceEquals(item.Tag, node))
            {
                item.IsSelected = true;
                return;
            }

            SelectInTree(item.ItemsSource?.OfType<TreeViewItem>() ?? [], node);
        }
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingTree)
        {
            return;
        }

        if (SceneTree.SelectedItem is TreeViewItem { Tag: NodeDefinition node })
        {
            _session.Select(node);
        }
    }

    private void OnSelectionChanged()
    {
        RefreshInspector();
        if (!_syncingTree)
        {
            _syncingTree = true;
            SelectInTree(SceneTree.ItemsSource?.OfType<TreeViewItem>() ?? [], _session.Selection);
            _syncingTree = false;
        }
    }

    // --------------------------------------------------------- inspector

    private void RefreshInspector()
    {
        InspectorPanel.Children.Clear();
        if (_session.Selection is not { } node)
        {
            InspectorPanel.Children.Add(new TextBlock { Text = "Select a node in the tree or click one in the viewport.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Opacity = 0.7 });
            return;
        }

        InspectorPanel.Children.Add(Section("NODE"));
        InspectorPanel.Children.Add(TextField("Name", node.Name, value =>
        {
            node.Name = value;
            _session.CommitChange(rebuild: false);
            if (_session.NodeFor(node) is { } live)
            {
                live.Name = value;
            }

            RefreshTree();
        }));
        InspectorPanel.Children.Add(VectorField("Position", node.Position, value =>
        {
            node.Position = value;
            if (_session.NodeFor(node) is { } live)
            {
                live.Position = value;
            }

            _session.CommitChange(rebuild: false);
        }));
        InspectorPanel.Children.Add(VectorField("Rotation°", node.Rotation * (180f / MathF.PI), value =>
        {
            node.Rotation = value * (MathF.PI / 180f);
            if (_session.NodeFor(node) is { } live)
            {
                live.EulerAngles = node.Rotation;
            }

            _session.CommitChange(rebuild: false);
        }));
        InspectorPanel.Children.Add(VectorField("Scale", node.Scale, value =>
        {
            node.Scale = value;
            if (_session.NodeFor(node) is { } live)
            {
                live.Scale = value;
            }

            _session.CommitChange(rebuild: false);
        }));
        InspectorPanel.Children.Add(CheckField("Visible", node.Visible, value =>
        {
            node.Visible = value;
            if (_session.NodeFor(node) is { } live)
            {
                live.Visible = value;
            }

            _session.CommitChange(rebuild: false);
        }));
        InspectorPanel.Children.Add(CheckField("Cast shadow", node.CastShadow, value =>
        {
            node.CastShadow = value;
            _session.CommitChange();
        }));

        if (node.GeometryId is { } geometryId && _session.Document.Geometries.FirstOrDefault(g => g.Id == geometryId) is { } geometry)
        {
            InspectorPanel.Children.Add(Section($"GEOMETRY ({geometry.Kind})"));
            if (geometry.Kind == GeometryKind.Model)
            {
                InspectorPanel.Children.Add(new TextBlock { Text = geometry.ModelPath, Opacity = 0.7, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap });
            }
            else
            {
                InspectorPanel.Children.Add(NumberField("Size A", geometry.A, value => { geometry.A = value; _session.CommitChange(); }));
                InspectorPanel.Children.Add(NumberField("Size B", geometry.B, value => { geometry.B = value; _session.CommitChange(); }));
                InspectorPanel.Children.Add(NumberField("Size C", geometry.C, value => { geometry.C = value; _session.CommitChange(); }));
            }
        }

        if (node.MaterialId is { } materialId && _session.Document.Materials.FirstOrDefault(m => m.Id == materialId) is { } material)
        {
            InspectorPanel.Children.Add(Section("MATERIAL"));
            InspectorPanel.Children.Add(ColorField("Base colour", material.BaseColor, value => { material.BaseColor = value; _session.CommitChange(); }));
            InspectorPanel.Children.Add(NumberField("Metallic", material.Metallic, value => { material.Metallic = value; _session.CommitChange(); }, 0f, 1f));
            InspectorPanel.Children.Add(NumberField("Roughness", material.Roughness, value => { material.Roughness = value; _session.CommitChange(); }, 0f, 1f));
            InspectorPanel.Children.Add(NumberField("Emissive", material.EmissiveIntensity, value => { material.EmissiveIntensity = value; _session.CommitChange(); }, 0f, 20f));
            InspectorPanel.Children.Add(TextField("Texture", material.BaseColorMap ?? string.Empty, value =>
            {
                material.BaseColorMap = value.Length == 0 ? null : value;
                _session.CommitChange();
            }));
            InspectorPanel.Children.Add(new Button { Content = "Pick texture file...", Margin = new global::Avalonia.Thickness(0, 4, 0, 0) }.OnClick(async _ =>
            {
                if (await PickFile("Texture", "*.png", "*.jpg", "*.ktx2", "*.basis") is { } file)
                {
                    string id = _session.Document.NextId("tex");
                    _session.Edit(() =>
                    {
                        _session.Document.Textures.Add(new TextureDefinition { Id = id, Path = file });
                        material.BaseColorMap = id;
                    });
                    RefreshInspector();
                }
            }));
        }

        if (node.Light is { } light)
        {
            InspectorPanel.Children.Add(Section("LIGHT"));
            InspectorPanel.Children.Add(EnumField("Type", light.Type, value => { light.Type = value; _session.CommitChange(); }));
            InspectorPanel.Children.Add(NumberField("Intensity", light.Intensity, value => { light.Intensity = value; _session.CommitChange(); }, 0f, 5000f));
            InspectorPanel.Children.Add(NumberField("Range", light.Range, value => { light.Range = value; _session.CommitChange(); }, 0f, 500f));
            InspectorPanel.Children.Add(ColorField("Colour", new System.Numerics.Vector4(light.Color, 1f), value => { light.Color = new Vector3(value.X, value.Y, value.Z); _session.CommitChange(); }));
            InspectorPanel.Children.Add(CheckField("Cast shadow", light.CastShadow, value => { light.CastShadow = value; _session.CommitChange(); }));
        }

        if (node.Camera is { } camera)
        {
            InspectorPanel.Children.Add(Section("CAMERA"));
            InspectorPanel.Children.Add(NumberField("Field of view°", camera.FieldOfView * (180f / MathF.PI), value => { camera.FieldOfView = value * (MathF.PI / 180f); _session.CommitChange(); }, 10f, 170f));
            InspectorPanel.Children.Add(CheckField("Active camera", camera.Active, value =>
            {
                foreach (NodeDefinition other in _session.Document.AllNodes().Where(n => n.Camera is not null))
                {
                    other.Camera!.Active = false;
                }

                camera.Active = value;
                _session.CommitChange();
            }));
        }

        InspectorPanel.Children.Add(Section("PHYSICS"));
        InspectorPanel.Children.Add(CheckField("Body / collider", node.Physics is not null, value =>
        {
            node.Physics = value ? new PhysicsDefinition() : null;
            _session.CommitChange();
            RefreshInspector();
        }));
        if (node.Physics is { } physics)
        {
            InspectorPanel.Children.Add(EnumField("Body", physics.Body, value => { physics.Body = value; _session.CommitChange(); }));
            InspectorPanel.Children.Add(EnumField("Shape", physics.Shape, value => { physics.Shape = value; _session.CommitChange(); }));
            InspectorPanel.Children.Add(CheckField("Collider only (static)", physics.ColliderOnly, value => { physics.ColliderOnly = value; _session.CommitChange(); }));
            InspectorPanel.Children.Add(VectorField("Half extents", physics.HalfExtents, value => { physics.HalfExtents = value; _session.CommitChange(); }));
            InspectorPanel.Children.Add(NumberField("Radius", physics.Radius, value => { physics.Radius = value; _session.CommitChange(); }, 0.01f, 100f));
            InspectorPanel.Children.Add(NumberField("Friction", physics.Friction, value => { physics.Friction = value; _session.CommitChange(); }, 0f, 2f));
            InspectorPanel.Children.Add(NumberField("Restitution", physics.Restitution, value => { physics.Restitution = value; _session.CommitChange(); }, 0f, 1f));
        }
    }

    // ------------------------------------------------------ field helpers

    private static TextBlock Section(string title) => new() { Text = title, Classes = { "section" } };

    private static Control Row(string label, Control editor)
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("104,*"), Margin = new global::Avalonia.Thickness(0, 2) };
        TextBlock text = new() { Text = label, Classes = { "field" } };
        Grid.SetColumn(editor, 1);
        grid.Children.Add(text);
        grid.Children.Add(editor);
        return grid;
    }

    private static Control TextField(string label, string value, Action<string> changed)
    {
        TextBox box = new() { Text = value };
        box.LostFocus += (_, _) => changed(box.Text ?? string.Empty);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                changed(box.Text ?? string.Empty);
            }
        };
        return Row(label, box);
    }

    private static Control NumberField(string label, float value, Action<float> changed, float minimum = -100000f, float maximum = 100000f)
    {
        NumericUpDown box = new()
        {
            Value = (decimal)value,
            Minimum = (decimal)minimum,
            Maximum = (decimal)maximum,
            Increment = 0.1m,
            FormatString = "0.###",
        };
        box.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } newValue)
            {
                changed((float)newValue);
            }
        };
        return Row(label, box);
    }

    private static Control VectorField(string label, Vector3 value, Action<Vector3> changed)
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*"), Margin = new global::Avalonia.Thickness(0, 2) };
        Vector3 current = value;
        for (int axis = 0; axis < 3; axis++)
        {
            int index = axis;
            NumericUpDown box = new()
            {
                Value = (decimal)(axis == 0 ? value.X : axis == 1 ? value.Y : value.Z),
                Increment = 0.1m,
                FormatString = "0.###",
                Minimum = -100000m,
                Maximum = 100000m,
                // Three fields share the row, so the spinner buttons are dropped.
                ShowButtonSpinner = false,
                Margin = new global::Avalonia.Thickness(0, 0, 3, 0),
            };
            box.ValueChanged += (_, e) =>
            {
                if (e.NewValue is not { } newValue)
                {
                    return;
                }

                float component = (float)newValue;
                current = index switch
                {
                    0 => current with { X = component },
                    1 => current with { Y = component },
                    _ => current with { Z = component },
                };
                changed(current);
            };
            Grid.SetColumn(box, axis);
            grid.Children.Add(box);
        }

        return Row(label, grid);
    }

    private static Control CheckField(string label, bool value, Action<bool> changed)
    {
        CheckBox box = new() { IsChecked = value };
        box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
        return Row(label, box);
    }

    private static Control EnumField<T>(string label, T value, Action<T> changed)
        where T : struct, Enum
    {
        ComboBox box = new() { ItemsSource = Enum.GetValues<T>().ToList(), SelectedItem = value, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch };
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is T selected)
            {
                changed(selected);
            }
        };
        return Row(label, box);
    }

    /// <summary>Colour as a hex field plus a preview swatch.</summary>
    private static Control ColorField(string label, System.Numerics.Vector4 value, Action<System.Numerics.Vector4> changed)
    {
        static string ToHex(System.Numerics.Vector4 color) =>
            $"#{(int)(Math.Clamp(color.X, 0f, 1f) * 255):X2}{(int)(Math.Clamp(color.Y, 0f, 1f) * 255):X2}{(int)(Math.Clamp(color.Z, 0f, 1f) * 255):X2}";

        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*,28") };
        TextBox box = new() { Text = ToHex(value) };
        Border swatch = new()
        {
            Width = 22,
            Height = 22,
            Margin = new global::Avalonia.Thickness(4, 0, 0, 0),
            Background = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse(ToHex(value))),
        };
        void Apply()
        {
            if (global::Avalonia.Media.Color.TryParse(box.Text, out global::Avalonia.Media.Color color))
            {
                swatch.Background = new global::Avalonia.Media.SolidColorBrush(color);
                changed(new System.Numerics.Vector4(color.R / 255f, color.G / 255f, color.B / 255f, value.W));
            }
        }

        box.LostFocus += (_, _) => Apply();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Apply();
            }
        };
        Grid.SetColumn(swatch, 1);
        grid.Children.Add(box);
        grid.Children.Add(swatch);
        return Row(label, grid);
    }

    // ------------------------------------------------------------ actions

    private void Status(string message) => StatusText.Text = message;

    private void UpdateTitle()
    {
        string file = _session.FilePath is { } path ? Path.GetFileName(path) : "untitled";
        Title = $"Three.Net Editor - {file}{(_session.IsModified ? " *" : string.Empty)}";
    }

    private async Task<string?> PickFile(string name, params string[] patterns)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Open {name}",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(name) { Patterns = patterns }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickSaveFile(string name, string extension)
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Save {name}",
            DefaultExtension = extension,
            SuggestedFileName = $"scene.{extension}",
        });
        return file?.TryGetLocalPath();
    }

    private void OnNew(object? sender, RoutedEventArgs e) => _session.New();

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (await PickFile("Scene", "*.json") is { } path)
        {
            try
            {
                _session.Open(path);
            }
            catch (Exception exception)
            {
                Status($"could not open: {exception.Message}");
            }
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_session.FilePath is { } path)
        {
            _session.Save(path);
            UpdateTitle();
        }
        else
        {
            OnSaveAs(sender, e);
            await Task.CompletedTask;
        }
    }

    private async void OnSaveAs(object? sender, RoutedEventArgs e)
    {
        if (await PickSaveFile("Scene", "json") is { } path)
        {
            _session.Save(path);
            UpdateTitle();
        }
    }

    private async void OnImportModel(object? sender, RoutedEventArgs e)
    {
        if (await PickFile("Model", "*.glb", "*.gltf", "*.fbx", "*.obj") is { } path)
        {
            _session.AddModel(path);
        }
    }

    private async void OnImportPlugin(object? sender, RoutedEventArgs e)
    {
        string[] patterns = [.. _session.Plugins.Importers.SelectMany(i => i.Extensions).Select(extension => $"*{extension}")];
        if (patterns.Length == 0)
        {
            Status("no plugin importers are loaded");
            return;
        }

        if (await PickFile("Plugin format", patterns) is { } path && !_session.Plugins.Import(path, _session.PluginContext()))
        {
            Status($"no importer handled {Path.GetFileName(path)}");
        }
    }

    private async void OnScreenshot(object? sender, RoutedEventArgs e)
    {
        if (Viewport.CaptureFrame() is not WriteableBitmap bitmap)
        {
            Status("nothing rendered yet");
            return;
        }

        if (await PickSaveFile("Screenshot", "png") is { } path)
        {
            bitmap.Save(path);
            Status($"saved {Path.GetFileName(path)}");
        }
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnUndo(object? sender, RoutedEventArgs e)
    {
        _session.Undo();
        RefreshTree();
    }

    private void OnRedo(object? sender, RoutedEventArgs e)
    {
        _session.Redo();
        RefreshTree();
    }

    private void OnDuplicate(object? sender, RoutedEventArgs e)
    {
        if (_session.Selection is { } node)
        {
            _session.Duplicate(node);
        }
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_session.Selection is { } node)
        {
            _session.Delete(node);
        }
    }

    private void OnMoveToRoot(object? sender, RoutedEventArgs e)
    {
        if (_session.Selection is { } node)
        {
            _session.Reparent(node, null);
        }
    }

    private void OnAddBox(object? sender, RoutedEventArgs e) => _session.AddPrimitive(GeometryKind.Box, "Box");

    private void OnAddSphere(object? sender, RoutedEventArgs e) => _session.AddPrimitive(GeometryKind.Sphere, "Sphere");

    private void OnAddPlane(object? sender, RoutedEventArgs e) => _session.AddPrimitive(GeometryKind.Plane, "Plane");

    private void OnAddCylinder(object? sender, RoutedEventArgs e) => _session.AddPrimitive(GeometryKind.Cylinder, "Cylinder");

    private void OnAddCone(object? sender, RoutedEventArgs e) => _session.AddPrimitive(GeometryKind.Cone, "Cone");

    private void OnAddTorus(object? sender, RoutedEventArgs e) => _session.AddPrimitive(GeometryKind.Torus, "Torus");

    private void OnAddDirectional(object? sender, RoutedEventArgs e) => _session.AddLight(LightType.Directional);

    private void OnAddPoint(object? sender, RoutedEventArgs e) => _session.AddLight(LightType.Point);

    private void OnAddSpot(object? sender, RoutedEventArgs e) => _session.AddLight(LightType.Spot);

    private void OnAddCamera(object? sender, RoutedEventArgs e) => _session.AddCamera();

    private void OnAddEmpty(object? sender, RoutedEventArgs e) =>
        _session.Add(new NodeDefinition { Id = _session.Document.NextId("node"), Name = "empty" }, asChildOfSelection: true);

    private void OnFrame(object? sender, RoutedEventArgs e)
    {
        if (_orbit is null)
        {
            return;
        }

        if (_session.NodeFor(_session.Selection) is { } node)
        {
            BoundingBox bounds = _session.Scene.GetBounds(node);
            _orbit.Target = bounds.IsEmpty ? node.WorldPosition : bounds.Center;
            _orbit.Distance = bounds.IsEmpty ? 6f : Math.Max(2f, bounds.Radius * 3f);
        }
        else
        {
            _orbit.Target = Vector3.Zero;
            _orbit.Distance = 12f;
        }

        _orbit.Apply();
    }

    private void OnToggleShadows(object? sender, RoutedEventArgs e)
    {
        Viewport.RendererOptions = Viewport.RendererOptions with { Shadows = !Viewport.RendererOptions.Shadows };
        Status($"shadows {(Viewport.RendererOptions.Shadows ? "on" : "off")}");
    }

    private void OnToggleSsao(object? sender, RoutedEventArgs e)
    {
        Viewport.RendererOptions = Viewport.RendererOptions with { Ssao = !Viewport.RendererOptions.Ssao };
        Status($"SSAO {(Viewport.RendererOptions.Ssao ? "on" : "off")}");
    }

    private void OnToggleGrid(object? sender, RoutedEventArgs e)
    {
        _showGrid = !_showGrid;
        if (_gridNode is { } grid && !_showGrid)
        {
            grid.Visible = false;
        }
        else
        {
            _session.Rebuild();
        }
    }

    private void OnPlayToggled(object? sender, RoutedEventArgs e)
    {
        bool playing = PlayToggle.IsChecked == true;
        _session.SetPlaying(playing);
        PlayToggle.Content = playing ? "Stop" : "Play";
        RefreshTree();
    }

    private void OnReloadPlugins(object? sender, RoutedEventArgs e)
    {
        _session.LoadPlugins();
        RefreshPluginsMenu();
    }

    private void RefreshPluginsMenu()
    {
        while (PluginsMenu.Items.Count > 2)
        {
            PluginsMenu.Items.RemoveAt(PluginsMenu.Items.Count - 1);
        }

        foreach (IGrouping<string, PluginCommand> group in _session.Plugins.Commands.GroupBy(c => c.Category))
        {
            MenuItem category = new() { Header = group.Key };
            foreach (PluginCommand command in group)
            {
                MenuItem item = new() { Header = command.Name };
                item.Click += (_, _) =>
                {
                    _session.BeginChange();
                    if (_session.Plugins.Run(command, _session.PluginContext()))
                    {
                        _session.CommitChange();
                        RefreshTree();
                    }
                };
                category.Items.Add(item);
            }

            PluginsMenu.Items.Add(category);
        }

        if (_session.Plugins.Plugins.Count == 0)
        {
            PluginsMenu.Items.Add(new MenuItem { Header = "(drop plugin DLLs in the plugins folder)", IsEnabled = false });
        }
    }

    private void OnAbout(object? sender, RoutedEventArgs e) =>
        Status("Three.Net Editor - Gravicode Studios, led by Kang Fadhil");

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox or NumericUpDown)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Delete:
                OnDelete(sender, e);
                break;
            case Key.F:
                OnFrame(sender, e);
                break;
            case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                OnUndo(sender, e);
                break;
            case Key.Y when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                OnRedo(sender, e);
                break;
            case Key.D when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                OnDuplicate(sender, e);
                break;
        }
    }
}

/// <summary>Small helper so buttons can be built inline with their handler.</summary>
internal static class ControlExtensions
{
    public static Button OnClick(this Button button, Func<RoutedEventArgs, Task> handler)
    {
        button.Click += async (_, e) => await handler(e);
        return button;
    }
}
