using System.Numerics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
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
using CornerRadius = Avalonia.CornerRadius;
using Thickness = Avalonia.Thickness;

namespace ThreeEditor.Views;

/// <summary>
/// The editor shell: hierarchy, viewport and inspector around an
/// <see cref="EditorSession"/>. Selection and dragging use the library's
/// <see cref="InteractionManager"/>, and the readout inside the viewport is
/// drawn with the library's own HUD overlay.
/// </summary>
public partial class MainWindow : Window
{
    private const float SnapStep = 0.25f;

    private readonly EditorSession _session = new();
    private readonly Dictionary<string, bool> _collapsed = [];
    private OrbitController? _orbit;
    private Node? _editorCamera;
    private Node? _gridNode;
    private TextBox? _nameBox;
    private int _frames;
    private double _statsTimer;
    private bool _syncingTree;

    // The viewport readout, drawn by the engine overlay.
    private OverlayElement? _hudName;
    private OverlayElement? _hudX;
    private OverlayElement? _hudY;
    private OverlayElement? _hudZ;
    private OverlayElement? _hudState;

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
        Viewport.RenderFailed += (_, message) => Status($"Renderer stopped: {message}");

        _session.Logged += Status;
        _session.SceneRebuilt += OnSceneRebuilt;
        _session.SelectionChanged += OnSelectionChanged;
        _session.DocumentChanged += UpdateHeader;

        _session.LoadPlugins();
        RefreshPluginsMenu();
        _session.Rebuild();
        RefreshInspector();
        UpdateHeader();

        KeyDown += OnWindowKeyDown;
        Closed += (_, _) => _session.Dispose();
    }

    private bool Snapping => SnapToggle.IsChecked == true;

    // -------------------------------------------------------------- scene

    private void OnSceneRebuilt()
    {
        Scene scene = _session.Scene;

        Vector3 position = _editorCamera?.Position ?? new Vector3(7f, 5f, 9f);
        _editorCamera = scene.AddCamera(Camera.Perspective(0.9f, 0.05f, 2000f), position, makeActive: false);
        _editorCamera.Name = "editor camera";

        Viewport.Scene = scene;
        Viewport.Camera = _editorCamera;

        Vector3 target = _orbit?.Target ?? Vector3.Zero;
        float distance = _orbit?.Distance ?? 13f;
        float yaw = _orbit?.Yaw ?? 0.7f;
        float pitch = _orbit?.Pitch ?? 0.35f;
        _orbit?.Dispose();
        _orbit = new OrbitController(Viewport, _editorCamera) { Target = target, Distance = distance, Yaw = yaw, Pitch = pitch };
        _orbit.Apply();

        ApplyGrid();
        BuildViewportReadout();
        HookInteraction();
        RefreshTree();
    }

    /// <summary>Click selects, dragging moves the node on the ground plane.</summary>
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

        if (_session.Build is { } build)
        {
            foreach (Node node in build.Nodes.Values)
            {
                interaction.MakeDraggable(node, DragMode.GroundPlane);
                interaction.On(node, NodeEventKind.DragStart, _ => _session.BeginChange());
                interaction.On(node, NodeEventKind.Drag, e => OnNodeDragging(e.Target));
                interaction.On(node, NodeEventKind.DragEnd, e => OnNodeDropped(e.Target));
            }
        }
    }

    private void OnNodeDragging(Node node)
    {
        if (Snapping)
        {
            Vector3 position = node.Position;
            node.Position = new Vector3(
                MathF.Round(position.X / SnapStep) * SnapStep,
                position.Y,
                MathF.Round(position.Z / SnapStep) * SnapStep);
        }

        UpdateViewportReadout(_session.DefinitionFor(node), node.Position);
    }

    private void OnNodeDropped(Node node)
    {
        if (_session.DefinitionFor(node) is not { } definition)
        {
            return;
        }

        definition.Position = node.Position;
        _session.Select(definition);
        _session.CommitChange(rebuild: false);
        RefreshInspector();
        Status($"Moved {definition.Name} to {definition.Position.X:0.00}, {definition.Position.Y:0.00}, {definition.Position.Z:0.00}");
    }

    private void ApplyGrid()
    {
        _gridNode = null;
        if (GridToggle.IsChecked != true)
        {
            return;
        }

        Scene scene = _session.Scene;
        // Dim enough to sit under the scene rather than compete with it.
        Material material = scene.CreateMaterial(MaterialOptions.Basic(new Vector4(0.12f, 0.13f, 0.15f, 1f)));
        _gridNode = scene.AddMesh(scene.CreateGridGeometry(24f, 24), material, name: "editor grid");
        _gridNode.CastShadow = false;
        _gridNode.ReceiveShadow = false;
        BuildAxisTriad(scene, _gridNode);
    }

    /// <summary>
    /// A metre of each axis at the origin, in the axis colours used by the
    /// transform fields and the readout: the same vocabulary in 3D.
    /// </summary>
    private static void BuildAxisTriad(Scene scene, Node parent)
    {
        (Vector3 Size, Vector3 Offset, Vector4 Colour)[] axes =
        [
            (new Vector3(0.75f, 0.01f, 0.01f), new Vector3(0.375f, 0.005f, 0f), new Vector4(0.87f, 0.42f, 0.35f, 1f)),
            (new Vector3(0.01f, 0.75f, 0.01f), new Vector3(0f, 0.375f, 0f), new Vector4(0.49f, 0.69f, 0.37f, 1f)),
            (new Vector3(0.01f, 0.01f, 0.75f), new Vector3(0f, 0.005f, 0.375f), new Vector4(0.29f, 0.55f, 0.80f, 1f)),
        ];
        foreach ((Vector3 size, Vector3 offset, Vector4 colour) in axes)
        {
            Material material = scene.CreateMaterial(MaterialOptions.Basic(colour) with { Emissive = new Vector3(colour.X, colour.Y, colour.Z), EmissiveIntensity = 0.6f });
            Node axis = scene.AddMesh(scene.CreateBoxGeometry(size.X, size.Y, size.Z), material, parent, "axis");
            axis.Position = offset;
            axis.CastShadow = false;
            axis.ReceiveShadow = false;
        }
    }

    /// <summary>
    /// The readout in the viewport corner: the selection's world position in
    /// axis colours, drawn with the engine <see cref="Overlay"/> rather than
    /// Avalonia, so it belongs to the rendered frame (and to screenshots).
    /// </summary>
    private void BuildViewportReadout()
    {
        Overlay hud = _session.Scene.Overlay;
        Vector4 muted = new(0.61f, 0.60f, 0.58f, 1f);
        OverlayElement panel = hud.Add(new OverlayElementOptions
        {
            Anchor = OverlayAnchor.BottomLeft,
            Offset = new Vector2(16f, -16f),
            Size = new Vector2(196f, 100f),
            Color = new Vector4(0.08f, 0.085f, 0.1f, 0.82f),
            BorderColor = new Vector4(0.19f, 0.2f, 0.23f, 1f),
            BorderWidth = 1f,
            CornerRadius = 4f,
        });
        hud.AddText("SELECTION", new Vector2(12f, 10f), 10f, muted, parent: panel);
        _hudName = hud.AddText("none", new Vector2(12f, 27f), 13f, new Vector4(0.93f, 0.92f, 0.89f, 1f), parent: panel);

        // One row per axis in its axis colour, matching the transform fields.
        (string Label, Vector4 Colour, float Y)[] axes =
        [
            ("X", new Vector4(0.87f, 0.42f, 0.35f, 1f), 50f),
            ("Y", new Vector4(0.49f, 0.69f, 0.37f, 1f), 66f),
            ("Z", new Vector4(0.29f, 0.55f, 0.80f, 1f), 82f),
        ];
        OverlayElement[] values = new OverlayElement[3];
        for (int i = 0; i < axes.Length; i++)
        {
            hud.AddText(axes[i].Label, new Vector2(12f, axes[i].Y), 11f, axes[i].Colour, parent: panel);
            values[i] = hud.AddText("—", new Vector2(32f, axes[i].Y), 11f, muted, parent: panel);
        }

        _hudX = values[0];
        _hudY = values[1];
        _hudZ = values[2];

        _hudState = hud.Add(new OverlayElementOptions
        {
            Kind = OverlayElementKind.Text,
            Text = "PHYSICS RUNNING",
            Anchor = OverlayAnchor.TopRight,
            Offset = new Vector2(-16f, 14f),
            FontSize = 11f,
            Color = new Vector4(0.87f, 0.64f, 0.24f, 1f),
            Visible = _session.IsPlaying,
        });

        UpdateViewportReadout(_session.Selection, null);
    }

    private void UpdateViewportReadout(NodeDefinition? selection, Vector3? livePosition)
    {
        if (_hudName is null || _hudX is null || _hudY is null || _hudZ is null)
        {
            return;
        }

        Vector3? position = livePosition ?? selection?.Position;
        _hudName.Text = selection?.Name ?? "none";
        _hudX.Text = position is { } x ? $"{x.X,7:0.00}" : "—";
        _hudY.Text = position is { } y ? $"{y.Y,7:0.00}" : "—";
        _hudZ.Text = position is { } z ? $"{z.Z,7:0.00}" : "—";
    }

    private void OnFrameTick(object? sender, FrameEventArgs e)
    {
        _session.Update(e.DeltaSeconds);
        if (_session.IsPlaying && _session.Selection is { } selection && _session.NodeFor(selection) is { } live)
        {
            UpdateViewportReadout(selection, live.Position);
        }

        _frames++;
        _statsTimer += e.DeltaSeconds;
        if (_statsTimer >= 0.5)
        {
            FrameStats stats = Viewport.Stats;
            StatsText.Text = $"{_frames / _statsTimer,3:0} fps · {stats.DrawCalls,3} draws · {stats.Triangles / 1000,4} k tris";
            _frames = 0;
            _statsTimer = 0;
        }
    }

    // ---------------------------------------------------------- hierarchy

    private void RefreshTree()
    {
        _syncingTree = true;
        string filter = SearchBox.Text?.Trim() ?? string.Empty;
        List<TreeViewItem> roots = [.. _session.Document.Nodes.Select(node => CreateTreeItem(node, filter)).OfType<TreeViewItem>()];
        SceneTree.ItemsSource = roots;
        SelectInTree(roots, _session.Selection);
        NodeCountText.Text = $"{_session.Document.AllNodes().Count()} nodes";
        _syncingTree = false;
    }

    /// <summary>A row per node: type mark, name and a visibility toggle. A filtered row stays when a child matches.</summary>
    private TreeViewItem? CreateTreeItem(NodeDefinition node, string filter)
    {
        List<TreeViewItem> children = [.. node.Children.Select(child => CreateTreeItem(child, filter)).OfType<TreeViewItem>()];
        bool matches = filter.Length == 0 || node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        if (!matches && children.Count == 0)
        {
            return null;
        }

        (string Mark, IBrush Brush) glyph = node.Light is not null
            ? ("◈", Brush("Amber"))
            : node.Camera is not null
                ? ("▣", Brush("AxisZ"))
                : node.GeometryId is not null
                    ? ("▢", Brush("TextMuted"))
                    : ("·", Brush("TextMuted"));

        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("16,*,22") };
        header.Children.Add(new TextBlock { Text = glyph.Mark, Foreground = glyph.Brush, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        TextBlock name = new()
        {
            Text = node.Name,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(node.Visible ? "TextPrimary" : "TextMuted"),
            Opacity = node.Visible ? 1 : 0.55,
        };
        Grid.SetColumn(name, 1);
        header.Children.Add(name);

        Button eye = new()
        {
            Content = node.Visible ? "◉" : "○",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 0),
            FontSize = 11,
            Foreground = Brush(node.Visible ? "TextMuted" : "Hairline"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(eye, node.Visible ? "Hide" : "Show");
        eye.Click += (_, e) =>
        {
            e.Handled = true;
            ToggleVisibility(node);
        };
        Grid.SetColumn(eye, 2);
        header.Children.Add(eye);

        return new TreeViewItem
        {
            Header = header,
            Tag = node,
            IsExpanded = true,
            ItemsSource = children,
            ContextMenu = BuildNodeMenu(node),
        };
    }

    private ContextMenu BuildNodeMenu(NodeDefinition node)
    {
        ContextMenu menu = new();
        void Add(string header, Action action)
        {
            MenuItem item = new() { Header = header };
            item.Click += (_, _) =>
            {
                _session.Select(node);
                action();
            };
            menu.Items.Add(item);
        }

        Add("Rename", FocusNameField);
        Add("Duplicate", () => _session.Duplicate(node));
        Add("Frame in viewport", () => OnFrame(this, new RoutedEventArgs()));
        Add("Move to root", () => _session.Reparent(node, null));
        menu.Items.Add(new Separator());
        Add("Delete", () => _session.Delete(node));
        return menu;
    }

    private void ToggleVisibility(NodeDefinition node)
    {
        node.Visible = !node.Visible;
        if (_session.NodeFor(node) is { } live)
        {
            live.Visible = node.Visible;
        }

        _session.CommitChange(rebuild: false);
        RefreshTree();
        RefreshInspector();
        Status($"{(node.Visible ? "Showing" : "Hiding")} {node.Name}");
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
        if (!_syncingTree && SceneTree.SelectedItem is TreeViewItem { Tag: NodeDefinition node })
        {
            _session.Select(node);
        }
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => RefreshTree();

    private void OnSelectionChanged()
    {
        RefreshInspector();
        UpdateViewportReadout(_session.Selection, null);
        SelectionKindText.Text = _session.Selection switch
        {
            null => string.Empty,
            { Light: not null } => "light",
            { Camera: not null } => "camera",
            { GeometryId: not null } => "mesh",
            _ => "group",
        };

        if (!_syncingTree)
        {
            _syncingTree = true;
            SelectInTree(SceneTree.ItemsSource?.OfType<TreeViewItem>() ?? [], _session.Selection);
            _syncingTree = false;
        }
    }

    // ---------------------------------------------------------- inspector

    private void RefreshInspector()
    {
        InspectorPanel.Children.Clear();
        _nameBox = null;
        if (_session.Selection is not { } node)
        {
            InspectorPanel.Children.Add(new TextBlock
            {
                Text = "NOTHING SELECTED",
                Classes = { "label" },
                Margin = new Thickness(0, 18, 0, 8),
            });
            InspectorPanel.Children.Add(new TextBlock
            {
                Text = "Pick a node in the hierarchy or click one in the viewport. Add a box from the toolbar to start a scene.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                Foreground = Brush("TextMuted"),
            });
            return;
        }

        Section("NODE", panel =>
        {
            _nameBox = TextFieldBox(node.Name, value =>
            {
                node.Name = value;
                if (_session.NodeFor(node) is { } live)
                {
                    live.Name = value;
                }

                _session.CommitChange(rebuild: false);
                RefreshTree();
            });
            panel.Children.Add(Row("Name", _nameBox));
            panel.Children.Add(AxisRow("Position", node.Position, value =>
            {
                node.Position = value;
                if (_session.NodeFor(node) is { } live)
                {
                    live.Position = value;
                }

                UpdateViewportReadout(node, value);
                _session.CommitChange(rebuild: false);
            }));
            panel.Children.Add(AxisRow("Rotation", node.Rotation * (180f / MathF.PI), value =>
            {
                node.Rotation = value * (MathF.PI / 180f);
                if (_session.NodeFor(node) is { } live)
                {
                    live.EulerAngles = node.Rotation;
                }

                _session.CommitChange(rebuild: false);
            }, "degrees"));
            panel.Children.Add(AxisRow("Scale", node.Scale, value =>
            {
                node.Scale = value;
                if (_session.NodeFor(node) is { } live)
                {
                    live.Scale = value;
                }

                _session.CommitChange(rebuild: false);
            }));
            panel.Children.Add(Row("Visible", CheckBoxFor(node.Visible, _ => ToggleVisibility(node))));
            panel.Children.Add(Row("Casts shadow", CheckBoxFor(node.CastShadow, value =>
            {
                node.CastShadow = value;
                _session.CommitChange();
            })));
        });

        if (node.GeometryId is { } geometryId && _session.Document.Geometries.FirstOrDefault(g => g.Id == geometryId) is { } geometry)
        {
            Section($"GEOMETRY · {geometry.Kind.ToString().ToUpperInvariant()}", panel =>
            {
                if (geometry.Kind == GeometryKind.Model)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = geometry.ModelPath,
                        Foreground = Brush("TextMuted"),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                    });
                    return;
                }

                (string Label, Func<float> Get, Action<float> Set)[] sizes = geometry.Kind switch
                {
                    GeometryKind.Sphere => [("Radius", () => geometry.A, v => geometry.A = v)],
                    GeometryKind.Plane => [("Width", () => geometry.A, v => geometry.A = v), ("Depth", () => geometry.B, v => geometry.B = v)],
                    GeometryKind.Cylinder => [("Top radius", () => geometry.A, v => geometry.A = v), ("Bottom radius", () => geometry.B, v => geometry.B = v), ("Height", () => geometry.C, v => geometry.C = v)],
                    GeometryKind.Cone => [("Radius", () => geometry.A, v => geometry.A = v), ("Height", () => geometry.C, v => geometry.C = v)],
                    GeometryKind.Torus => [("Radius", () => geometry.A, v => geometry.A = v), ("Tube", () => geometry.B, v => geometry.B = v)],
                    GeometryKind.Grid => [("Size", () => geometry.A, v => geometry.A = v)],
                    _ => [("Width", () => geometry.A, v => geometry.A = v), ("Height", () => geometry.B, v => geometry.B = v), ("Depth", () => geometry.C, v => geometry.C = v)],
                };
                foreach ((string label, Func<float> get, Action<float> set) in sizes)
                {
                    panel.Children.Add(Row(label, NumberBox(get(), value =>
                    {
                        set(value);
                        _session.CommitChange();
                    }, 0.01f, 1000f)));
                }
            });
        }

        if (node.MaterialId is { } materialId && _session.Document.Materials.FirstOrDefault(m => m.Id == materialId) is { } material)
        {
            Section("MATERIAL", panel =>
            {
                panel.Children.Add(Row("Base colour", ColorBox(material.BaseColor, value =>
                {
                    material.BaseColor = value;
                    _session.CommitChange();
                })));
                panel.Children.Add(Row("Metallic", NumberBox(material.Metallic, value => { material.Metallic = value; _session.CommitChange(); }, 0f, 1f, 0.05m)));
                panel.Children.Add(Row("Roughness", NumberBox(material.Roughness, value => { material.Roughness = value; _session.CommitChange(); }, 0f, 1f, 0.05m)));
                panel.Children.Add(Row("Emissive", NumberBox(material.EmissiveIntensity, value => { material.EmissiveIntensity = value; _session.CommitChange(); }, 0f, 20f)));

                Button pick = new()
                {
                    Content = material.BaseColorMap is null ? "Choose texture" : $"Texture · {material.BaseColorMap}",
                    Classes = { "tool" },
                    Margin = new Thickness(0, 6, 0, 0),
                };
                pick.Click += async (_, _) =>
                {
                    if (await PickFile("Texture", "*.png", "*.jpg", "*.jpeg", "*.ktx2", "*.basis") is { } file)
                    {
                        string id = _session.Document.NextId("texture");
                        _session.Edit(() =>
                        {
                            _session.Document.Textures.Add(new TextureDefinition { Id = id, Path = file });
                            material.BaseColorMap = id;
                        });
                        RefreshInspector();
                        Status($"Applied {Path.GetFileName(file)}");
                    }
                };
                panel.Children.Add(pick);

                if (material.BaseColorMap is not null)
                {
                    Button clear = new() { Content = "Remove texture", Classes = { "tool" }, Margin = new Thickness(0, 4, 0, 0) };
                    clear.Click += (_, _) =>
                    {
                        _session.Edit(() => material.BaseColorMap = null);
                        RefreshInspector();
                        Status("Removed the texture");
                    };
                    panel.Children.Add(clear);
                }
            });
        }

        if (node.Light is { } light)
        {
            Section("LIGHT", panel =>
            {
                panel.Children.Add(Row("Type", EnumBox(light.Type, value => { light.Type = value; _session.CommitChange(); })));
                panel.Children.Add(Row("Intensity", NumberBox(light.Intensity, value => { light.Intensity = value; _session.CommitChange(); }, 0f, 5000f)));
                panel.Children.Add(Row("Range", NumberBox(light.Range, value => { light.Range = value; _session.CommitChange(); }, 0f, 500f)));
                panel.Children.Add(Row("Colour", ColorBox(new Vector4(light.Color, 1f), value =>
                {
                    light.Color = new Vector3(value.X, value.Y, value.Z);
                    _session.CommitChange();
                })));
                panel.Children.Add(Row("Casts shadow", CheckBoxFor(light.CastShadow, value => { light.CastShadow = value; _session.CommitChange(); })));
            });
        }

        if (node.Camera is { } camera)
        {
            Section("CAMERA", panel =>
            {
                panel.Children.Add(Row("Field of view", NumberBox(camera.FieldOfView * (180f / MathF.PI), value =>
                {
                    camera.FieldOfView = value * (MathF.PI / 180f);
                    _session.CommitChange();
                }, 10f, 170f), "degrees"));
                panel.Children.Add(Row("Near", NumberBox(camera.Near, value => { camera.Near = value; _session.CommitChange(); }, 0.001f, 100f)));
                panel.Children.Add(Row("Far", NumberBox(camera.Far, value => { camera.Far = value; _session.CommitChange(); }, 1f, 100000f)));
                panel.Children.Add(Row("Renders the scene", CheckBoxFor(camera.Active, value =>
                {
                    foreach (NodeDefinition other in _session.Document.AllNodes().Where(n => n.Camera is not null))
                    {
                        other.Camera!.Active = false;
                    }

                    camera.Active = value;
                    _session.CommitChange();
                })));
            });
        }

        Section("PHYSICS", panel =>
        {
            panel.Children.Add(Row("Simulated", CheckBoxFor(node.Physics is not null, value =>
            {
                node.Physics = value ? new PhysicsDefinition() : null;
                _session.CommitChange();
                RefreshInspector();
            })));
            if (node.Physics is not { } physics)
            {
                return;
            }

            panel.Children.Add(Row("Body", EnumBox(physics.Body, value => { physics.Body = value; _session.CommitChange(); })));
            panel.Children.Add(Row("Shape", EnumBox(physics.Shape, value => { physics.Shape = value; _session.CommitChange(); })));
            panel.Children.Add(Row("Static collider", CheckBoxFor(physics.ColliderOnly, value => { physics.ColliderOnly = value; _session.CommitChange(); })));
            panel.Children.Add(AxisRow("Half extents", physics.HalfExtents, value => { physics.HalfExtents = value; _session.CommitChange(); }));
            panel.Children.Add(Row("Radius", NumberBox(physics.Radius, value => { physics.Radius = value; _session.CommitChange(); }, 0.01f, 100f)));
            panel.Children.Add(Row("Friction", NumberBox(physics.Friction, value => { physics.Friction = value; _session.CommitChange(); }, 0f, 2f, 0.05m)));
            panel.Children.Add(Row("Bounciness", NumberBox(physics.Restitution, value => { physics.Restitution = value; _session.CommitChange(); }, 0f, 1f, 0.05m)));
        });
    }

    /// <summary>A collapsible block; the open state is remembered while the editor runs.</summary>
    private void Section(string title, Action<StackPanel> build)
    {
        StackPanel content = new() { Spacing = 0, Margin = new Thickness(0, 0, 0, 2) };
        build(content);

        bool collapsed = _collapsed.GetValueOrDefault(title);
        content.IsVisible = !collapsed;

        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        TextBlock chevron = new()
        {
            Text = collapsed ? "▸" : "▾",
            Foreground = Brush("TextMuted"),
            FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        TextBlock label = new() { Text = title, Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        Border rule = new() { Height = 1, Background = Brush("Hairline"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        Grid.SetColumn(rule, 2);
        header.Children.Add(chevron);
        header.Children.Add(label);
        header.Children.Add(rule);

        ToggleButton toggle = new() { Classes = { "section" }, Content = header, IsChecked = !collapsed };
        toggle.IsCheckedChanged += (_, _) =>
        {
            bool open = toggle.IsChecked == true;
            _collapsed[title] = !open;
            content.IsVisible = open;
            chevron.Text = open ? "▾" : "▸";
        };

        InspectorPanel.Children.Add(toggle);
        InspectorPanel.Children.Add(content);
    }

    // ------------------------------------------------------ field helpers

    private IBrush Brush(string key) => this.FindResource(key) as IBrush ?? Brushes.Gray;

    private Control Row(string label, Control editor, string? unit = null)
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("112,*"), Margin = new Thickness(0, 3) };
        StackPanel caption = new() { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Foreground = Brush("TextPrimary") });
        if (unit is not null)
        {
            caption.Children.Add(new TextBlock { Text = unit, FontSize = 10, Foreground = Brush("TextMuted") });
        }

        Grid.SetColumn(editor, 1);
        grid.Children.Add(caption);
        grid.Children.Add(editor);
        return grid;
    }

    /// <summary>Three numbers with axis coloured caps, matching the viewport readout.</summary>
    private Control AxisRow(string label, Vector3 value, Action<Vector3> changed, string? unit = null)
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        Vector3 current = value;
        string[] axes = ["X", "Y", "Z"];
        string[] brushes = ["AxisX", "AxisY", "AxisZ"];
        for (int axis = 0; axis < 3; axis++)
        {
            int index = axis;
            Grid cell = new() { ColumnDefinitions = new ColumnDefinitions("12,*"), Margin = new Thickness(0, 0, axis == 2 ? 0 : 4, 0) };
            cell.Children.Add(new TextBlock
            {
                Text = axes[axis],
                Foreground = Brush(brushes[axis]),
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });

            NumericUpDown box = new()
            {
                Value = (decimal)(axis == 0 ? value.X : axis == 1 ? value.Y : value.Z),
                Increment = 0.1m,
                FormatString = "0.###",
                Minimum = -100000m,
                Maximum = 100000m,
                ShowButtonSpinner = false,
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
            Grid.SetColumn(box, 1);
            cell.Children.Add(box);
            Grid.SetColumn(cell, axis);
            grid.Children.Add(cell);
        }

        return Row(label, grid, unit);
    }

    private static TextBox TextFieldBox(string value, Action<string> changed)
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
        return box;
    }

    private static NumericUpDown NumberBox(float value, Action<float> changed, float minimum = -100000f, float maximum = 100000f, decimal increment = 0.1m)
    {
        NumericUpDown box = new()
        {
            Value = (decimal)value,
            Minimum = (decimal)minimum,
            Maximum = (decimal)maximum,
            Increment = increment,
            FormatString = "0.###",
        };
        box.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } newValue)
            {
                changed((float)newValue);
            }
        };
        return box;
    }

    private static CheckBox CheckBoxFor(bool value, Action<bool> changed)
    {
        CheckBox box = new() { IsChecked = value, MinHeight = 24 };
        box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    private static ComboBox EnumBox<T>(T value, Action<T> changed)
        where T : struct, Enum
    {
        ComboBox box = new() { ItemsSource = Enum.GetValues<T>().ToList(), SelectedItem = value, HorizontalAlignment = HorizontalAlignment.Stretch };
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is T selected)
            {
                changed(selected);
            }
        };
        return box;
    }

    private static Control ColorBox(Vector4 value, Action<Vector4> changed)
    {
        static string ToHex(Vector4 colour) =>
            $"#{(int)(Math.Clamp(colour.X, 0f, 1f) * 255):X2}{(int)(Math.Clamp(colour.Y, 0f, 1f) * 255):X2}{(int)(Math.Clamp(colour.Z, 0f, 1f) * 255):X2}";

        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*,30") };
        TextBox box = new() { Text = ToHex(value), FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace") };
        Border swatch = new()
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(6, 0, 0, 0),
            Background = new SolidColorBrush(Color.Parse(ToHex(value))),
        };
        void Apply()
        {
            if (Color.TryParse(box.Text, out Color colour))
            {
                swatch.Background = new SolidColorBrush(colour);
                changed(new Vector4(colour.R / 255f, colour.G / 255f, colour.B / 255f, value.W));
            }
            else
            {
                box.Text = ToHex(value);
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
        return grid;
    }

    // ------------------------------------------------------------ actions

    private void Status(string message) => StatusText.Text = message;

    private void UpdateHeader()
    {
        string file = _session.FilePath is { } path ? Path.GetFileName(path) : "untitled";
        FileText.Text = file;
        ModifiedDot.IsVisible = _session.IsModified;
        Title = $"Three.Net Editor — {file}{(_session.IsModified ? " (unsaved)" : string.Empty)}";
        NodeCountText.Text = $"{_session.Document.AllNodes().Count()} nodes";
    }

    private void FocusNameField()
    {
        _nameBox?.Focus();
        _nameBox?.SelectAll();
    }

    private async Task<string?> PickFile(string name, params string[] patterns)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Open {name.ToLowerInvariant()}",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(name) { Patterns = patterns }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickSaveFile(string name, string extension)
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Save {name.ToLowerInvariant()}",
            DefaultExtension = extension,
            SuggestedFileName = $"scene.{extension}",
        });
        return file?.TryGetLocalPath();
    }

    private void OnNew(object? sender, RoutedEventArgs e)
    {
        _session.New();
        Status("Started a new scene");
    }

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (await PickFile("Scene", "*.json") is not { } path)
        {
            return;
        }

        try
        {
            _session.Open(path);
        }
        catch (Exception exception)
        {
            Status($"{Path.GetFileName(path)} could not be opened: {exception.Message}");
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_session.FilePath is { } path)
        {
            _session.Save(path);
            UpdateHeader();
        }
        else
        {
            OnSaveAs(sender, e);
        }
    }

    private async void OnSaveAs(object? sender, RoutedEventArgs e)
    {
        if (await PickSaveFile("Scene", "json") is { } path)
        {
            _session.Save(path);
            UpdateHeader();
        }
    }

    private async void OnImportModel(object? sender, RoutedEventArgs e)
    {
        if (await PickFile("Model", "*.glb", "*.gltf", "*.fbx", "*.obj") is { } path)
        {
            _session.AddModel(path);
            Status($"Imported {Path.GetFileName(path)}");
        }
    }

    private async void OnImportPlugin(object? sender, RoutedEventArgs e)
    {
        string[] patterns = [.. _session.Plugins.Importers.SelectMany(i => i.Extensions).Select(extension => $"*{extension}")];
        if (patterns.Length == 0)
        {
            Status("No plugin importers are loaded. Put a plugin DLL in the plugins folder, then choose Plugins ▸ Reload plugins.");
            return;
        }

        if (await PickFile("Plugin format", patterns) is { } path && !_session.Plugins.Import(path, _session.PluginContext()))
        {
            Status($"No importer handles {Path.GetExtension(path)} files");
        }
    }

    private async void OnScreenshot(object? sender, RoutedEventArgs e)
    {
        if (Viewport.CaptureFrame() is not WriteableBitmap bitmap)
        {
            Status("The viewport has not rendered a frame yet");
            return;
        }

        if (await PickSaveFile("Screenshot", "png") is { } path)
        {
            bitmap.Save(path);
            Status($"Saved {Path.GetFileName(path)}");
        }
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnUndo(object? sender, RoutedEventArgs e)
    {
        if (!_session.CanUndo)
        {
            Status("Nothing to undo");
            return;
        }

        _session.Undo();
        RefreshTree();
        Status("Undone");
    }

    private void OnRedo(object? sender, RoutedEventArgs e)
    {
        if (!_session.CanRedo)
        {
            Status("Nothing to redo");
            return;
        }

        _session.Redo();
        RefreshTree();
        Status("Redone");
    }

    private void OnDuplicate(object? sender, RoutedEventArgs e)
    {
        if (_session.Selection is { } node)
        {
            _session.Duplicate(node);
            Status($"Duplicated {node.Name}");
        }
    }

    private void OnRename(object? sender, RoutedEventArgs e) => FocusNameField();

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_session.Selection is { } node)
        {
            _session.Delete(node);
            Status($"Deleted {node.Name}");
        }
    }

    private void OnMoveToRoot(object? sender, RoutedEventArgs e)
    {
        if (_session.Selection is { } node)
        {
            _session.Reparent(node, null);
        }
    }

    private void OnAddBox(object? sender, RoutedEventArgs e) => Added(_session.AddPrimitive(GeometryKind.Box, "Box"));

    private void OnAddSphere(object? sender, RoutedEventArgs e) => Added(_session.AddPrimitive(GeometryKind.Sphere, "Sphere"));

    private void OnAddPlane(object? sender, RoutedEventArgs e) => Added(_session.AddPrimitive(GeometryKind.Plane, "Plane"));

    private void OnAddCylinder(object? sender, RoutedEventArgs e) => Added(_session.AddPrimitive(GeometryKind.Cylinder, "Cylinder"));

    private void OnAddCone(object? sender, RoutedEventArgs e) => Added(_session.AddPrimitive(GeometryKind.Cone, "Cone"));

    private void OnAddTorus(object? sender, RoutedEventArgs e) => Added(_session.AddPrimitive(GeometryKind.Torus, "Torus"));

    private void OnAddDirectional(object? sender, RoutedEventArgs e) => Added(_session.AddLight(LightType.Directional));

    private void OnAddPoint(object? sender, RoutedEventArgs e) => Added(_session.AddLight(LightType.Point));

    private void OnAddSpot(object? sender, RoutedEventArgs e) => Added(_session.AddLight(LightType.Spot));

    private void OnAddCamera(object? sender, RoutedEventArgs e) => Added(_session.AddCamera());

    private void OnAddEmpty(object? sender, RoutedEventArgs e) =>
        Added(_session.Add(new NodeDefinition { Id = _session.Document.NextId("node"), Name = "empty" }, asChildOfSelection: true));

    private void Added(NodeDefinition node) => Status($"Added {node.Name}");

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
            _orbit.Distance = 13f;
        }

        _orbit.Apply();
    }

    private void OnToggleShadows(object? sender, RoutedEventArgs e)
    {
        if (Viewport is not null)
        {
            Viewport.RendererOptions = Viewport.RendererOptions with { Shadows = ShadowsToggle.IsChecked == true };
        }
    }

    private void OnToggleSsao(object? sender, RoutedEventArgs e)
    {
        if (Viewport is not null)
        {
            Viewport.RendererOptions = Viewport.RendererOptions with { Ssao = SsaoToggle.IsChecked == true };
        }
    }

    private void OnToggleGrid(object? sender, RoutedEventArgs e)
    {
        if (_gridNode is { } grid)
        {
            grid.Visible = GridToggle.IsChecked == true;
        }
        else if (GridToggle.IsChecked == true && _session.Build is not null)
        {
            ApplyGrid();
        }
    }

    private void OnToggleSnap(object? sender, RoutedEventArgs e) =>
        Status(Snapping ? "Dragging snaps to 0.25 m" : "Dragging moves freely");

    private void OnPlayToggled(object? sender, RoutedEventArgs e)
    {
        bool playing = PlayToggle.IsChecked == true;
        _session.SetPlaying(playing);
        PlayToggle.Content = playing ? "Stop physics" : "Play physics";
        if (_hudState is not null)
        {
            _hudState.Visible = playing;
        }

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
                ToolTip.SetTip(item, command.Description);
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
            PluginsMenu.Items.Add(new MenuItem { Header = "No plugins loaded", IsEnabled = false });
        }
    }

    private void OnAbout(object? sender, RoutedEventArgs e) =>
        Status("Left click selects · left drag moves on the ground · right drag orbits · wheel zooms · F frames · Ctrl+D duplicates · Delete removes");

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
            case Key.F when e.KeyModifiers == KeyModifiers.None:
                OnFrame(sender, e);
                break;
            case Key.F2:
                FocusNameField();
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
            case Key.S when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                OnSave(sender, e);
                break;
        }
    }
}
