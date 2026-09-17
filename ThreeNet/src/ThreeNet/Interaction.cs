using System.Numerics;

namespace ThreeNet;

/// <summary>Kind of pointer interaction delivered to node handlers.</summary>
public enum NodeEventKind
{
    PointerEnter,
    PointerLeave,
    PointerDown,
    PointerUp,
    PointerMove,
    Click,
    DoubleClick,
    DragStart,
    Drag,
    DragEnd,
}

/// <summary>How a draggable node moves under the pointer.</summary>
public enum DragMode
{
    /// <summary>Handlers receive drag events but the node is not moved.</summary>
    EventsOnly,
    /// <summary>Moves in the plane facing the camera through the grab point.</summary>
    CameraPlane,
    /// <summary>Slides on the horizontal plane (constant world Y) through the grab point.</summary>
    GroundPlane,
}

/// <summary>A pointer event targeted at a node. Set <see cref="Handled"/> to stop bubbling to parents.</summary>
public sealed class NodeEvent
{
    internal NodeEvent(NodeEventKind kind, Node target, Node? hitNode, RayHit? hit, Ray ray, Vector2 screenPosition, MouseButton button)
    {
        Kind = kind;
        Target = target;
        HitNode = hitNode;
        Hit = hit;
        Ray = ray;
        ScreenPosition = screenPosition;
        Button = button;
    }

    public NodeEventKind Kind { get; }

    /// <summary>The node whose handler is running (an ancestor of <see cref="HitNode"/> while bubbling).</summary>
    public Node Target { get; internal set; }

    /// <summary>The mesh node under the pointer, if any.</summary>
    public Node? HitNode { get; }

    public RayHit? Hit { get; }

    /// <summary>The picking ray in world space.</summary>
    public Ray Ray { get; }

    /// <summary>Pointer position in viewport pixels.</summary>
    public Vector2 ScreenPosition { get; }

    public MouseButton Button { get; }

    /// <summary>Current point on the drag plane (drag events).</summary>
    public Vector3 DragPoint { get; internal set; }

    /// <summary>Movement on the drag plane since the previous drag event.</summary>
    public Vector3 DragDelta { get; internal set; }

    /// <summary>Stops the event from reaching handlers on ancestor nodes.</summary>
    public bool Handled { get; set; }
}

/// <summary>
/// Node level pointer events (enter / leave / down / up / move / click / double
/// click / drag) on top of raycasting. Feed it pointer input from any host
/// (<c>ThreeNetView</c> does so automatically, <see cref="HandleInput"/> covers
/// <see cref="AppWindow"/>); handlers registered on a node also receive events
/// for its descendants, so an imported model can be clicked as a whole.
/// </summary>
public sealed class InteractionManager
{
    private readonly Scene _scene;
    private readonly Dictionary<uint, Dictionary<NodeEventKind, List<Action<NodeEvent>>>> _handlers = [];
    private readonly Dictionary<uint, DragMode> _draggable = [];
    private readonly List<uint> _hoverChain = [];
    private Vector2 _viewport = new(1f, 1f);
    private Vector2 _lastPosition;

    private (List<uint> Chain, Vector2 Position, MouseButton Button)? _press;
    private DragState? _drag;
    private (uint Node, long Timestamp)? _lastClick;
    private readonly Dictionary<uint, (List<Action<OverlayElement>> Click, List<Action<OverlayElement>> Enter, List<Action<OverlayElement>> Leave)> _overlayHandlers = [];
    private uint _overlayPress;
    private uint _overlayHover;

    private sealed class DragState
    {
        public required uint Node;
        public required DragMode Mode;
        public required Vector3 PlanePoint;
        public required Vector3 PlaneNormal;
        public required Vector3 LastPoint;
        public required Vector3 GrabOffset;
        public required MouseButton Button;
        public bool Started;
    }

    public InteractionManager(Scene scene, Node? camera = null)
    {
        _scene = scene;
        Camera = camera;
    }

    /// <summary>Camera used to build picking rays; falls back to the scene's active camera.</summary>
    public Node? Camera { get; set; }

    public RaycastOptions RaycastOptions { get; set; } = RaycastOptions.Default;

    /// <summary>Pixels the pointer may travel between down and up and still count as a click (also the drag threshold).</summary>
    public float ClickTolerance { get; set; } = 4f;

    /// <summary>Maximum time between two clicks on the same node for a double click.</summary>
    public TimeSpan DoubleClickTime { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>The mesh node currently under the pointer.</summary>
    public Node? HoveredNode { get; private set; }

    /// <summary>True while a node is being dragged; hosts should suppress camera controls then.</summary>
    public bool IsDragging => _drag is { Started: true };

    /// <summary>True between a pointer down on a draggable node (or an overlay element) and the pointer up.</summary>
    public bool HasPointerCapture => _drag is not null || _overlayPress != 0;

    /// <summary>Raised for every event before node handlers run.</summary>
    public event Action<NodeEvent>? Event;

    /// <summary>
    /// Pixel size of the render target the overlay is laid out in. Pointer
    /// positions are scaled from the viewport to it; null means they match.
    /// </summary>
    public Vector2? OverlayTargetSize { get; set; }

    /// <summary>Interactive overlay element under the pointer (it blocks picking of 3D nodes).</summary>
    public OverlayElement? HoveredOverlayElement => _overlayHover == 0 ? null : new OverlayElement(_scene, _overlayHover);

    /// <summary>Raised when an interactive overlay element is clicked.</summary>
    public event Action<OverlayElement>? OverlayClicked;

    public InteractionManager OnClick(OverlayElement element, Action<OverlayElement> handler)
    {
        OverlayHandlers(element).Click.Add(handler);
        return this;
    }

    public InteractionManager OnPointerEnter(OverlayElement element, Action<OverlayElement> handler)
    {
        OverlayHandlers(element).Enter.Add(handler);
        return this;
    }

    public InteractionManager OnPointerLeave(OverlayElement element, Action<OverlayElement> handler)
    {
        OverlayHandlers(element).Leave.Add(handler);
        return this;
    }

    private (List<Action<OverlayElement>> Click, List<Action<OverlayElement>> Enter, List<Action<OverlayElement>> Leave) OverlayHandlers(OverlayElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!_overlayHandlers.TryGetValue(element.Id, out var handlers))
        {
            handlers = ([], [], []);
            _overlayHandlers[element.Id] = handlers;
        }

        return handlers;
    }

    private uint OverlayHit(Vector2 position)
    {
        Vector2 target = OverlayTargetSize ?? _viewport;
        Vector2 point = OverlayTargetSize is { } size && _viewport.X > 0f && _viewport.Y > 0f
            ? position * size / _viewport
            : position;
        return _scene.Overlay.HitTest(point, target)?.Id ?? 0;
    }

    /// <summary>Updates overlay hover; returns true when the pointer is over an interactive element.</summary>
    private bool UpdateOverlayHover(Vector2 position)
    {
        uint hit = OverlayHit(position);
        if (hit != _overlayHover)
        {
            if (_overlayHover != 0 && _overlayHandlers.TryGetValue(_overlayHover, out var old))
            {
                OverlayElement element = new(_scene, _overlayHover);
                old.Leave.ToArray().ToList().ForEach(h => h(element));
            }

            _overlayHover = hit;
            if (hit != 0 && _overlayHandlers.TryGetValue(hit, out var current))
            {
                OverlayElement element = new(_scene, hit);
                current.Enter.ToArray().ToList().ForEach(h => h(element));
            }
        }

        return hit != 0;
    }

    public InteractionManager On(Node node, NodeEventKind kind, Action<NodeEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryGetValue(node.Id, out Dictionary<NodeEventKind, List<Action<NodeEvent>>>? kinds))
        {
            _handlers[node.Id] = kinds = [];
        }

        if (!kinds.TryGetValue(kind, out List<Action<NodeEvent>>? list))
        {
            kinds[kind] = list = [];
        }

        list.Add(handler);
        return this;
    }

    public InteractionManager OnClick(Node node, Action<NodeEvent> handler) => On(node, NodeEventKind.Click, handler);

    public InteractionManager OnDoubleClick(Node node, Action<NodeEvent> handler) => On(node, NodeEventKind.DoubleClick, handler);

    public InteractionManager OnPointerEnter(Node node, Action<NodeEvent> handler) => On(node, NodeEventKind.PointerEnter, handler);

    public InteractionManager OnPointerLeave(Node node, Action<NodeEvent> handler) => On(node, NodeEventKind.PointerLeave, handler);

    public InteractionManager OnPointerDown(Node node, Action<NodeEvent> handler) => On(node, NodeEventKind.PointerDown, handler);

    public InteractionManager OnPointerUp(Node node, Action<NodeEvent> handler) => On(node, NodeEventKind.PointerUp, handler);

    /// <summary>Makes a node (and its subtree) draggable, optionally moving it automatically.</summary>
    public InteractionManager MakeDraggable(Node node, DragMode mode = DragMode.CameraPlane, Action<NodeEvent>? onDrag = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        _draggable[node.Id] = mode;
        if (onDrag is not null)
        {
            On(node, NodeEventKind.Drag, onDrag);
        }

        return this;
    }

    /// <summary>Removes every handler and drag registration of a node.</summary>
    public void Remove(Node node)
    {
        _handlers.Remove(node.Id);
        _draggable.Remove(node.Id);
        _hoverChain.Remove(node.Id);
    }

    public void Clear()
    {
        _handlers.Clear();
        _overlayHandlers.Clear();
        _overlayPress = 0;
        _overlayHover = 0;
        _draggable.Clear();
        _hoverChain.Clear();
        _press = null;
        _drag = null;
        HoveredNode = null;
    }

    /// <summary>Pointer moved to <paramref name="position"/> (pixels, origin top left) in a viewport of <paramref name="viewportSize"/>.</summary>
    /// <returns>True when the move was consumed by a drag.</returns>
    public bool PointerMove(Vector2 position, Vector2 viewportSize)
    {
        _viewport = viewportSize;
        _lastPosition = position;
        bool overOverlay = _drag is null && UpdateOverlayHover(position);
        if (!TryPick(position, out Ray ray, out RayHit? hit))
        {
            return false;
        }

        if (_drag is { } drag)
        {
            UpdateDrag(drag, ray, position, hit);
            return drag.Started;
        }

        if (overOverlay)
        {
            // The HUD covers the scene: nothing below it is hovered.
            UpdateHover(ray, position, null);
            return false;
        }

        UpdateHover(ray, position, hit);
        if (hit is { } h)
        {
            Dispatch(NodeEventKind.PointerMove, Chain(h.Node.Id), h.Node, hit, ray, position, MouseButton.Left);
        }

        return false;
    }

    /// <summary>A button went down. Returns true when a draggable node captured the pointer.</summary>
    public bool PointerDown(Vector2 position, Vector2 viewportSize, MouseButton button = MouseButton.Left)
    {
        _viewport = viewportSize;
        _lastPosition = position;
        if (UpdateOverlayHover(position))
        {
            _overlayPress = _overlayHover;
            return true;
        }

        if (!TryPick(position, out Ray ray, out RayHit? hit))
        {
            return false;
        }

        UpdateHover(ray, position, hit);
        if (hit is not { } h)
        {
            _press = ([], position, button);
            return false;
        }

        List<uint> chain = Chain(h.Node.Id);
        _press = (chain, position, button);
        Dispatch(NodeEventKind.PointerDown, chain, h.Node, hit, ray, position, button);

        if (button == MouseButton.Left && chain.FirstOrDefault(_draggable.ContainsKey) is var owner && owner != 0)
        {
            DragMode mode = _draggable[owner];
            Vector3 normal = mode == DragMode.GroundPlane ? Vector3.UnitY : CameraForward();
            Vector3 origin = new Node(_scene, owner).WorldPosition;
            _drag = new DragState
            {
                Node = owner,
                Mode = mode,
                PlanePoint = h.Point,
                PlaneNormal = normal,
                LastPoint = h.Point,
                GrabOffset = origin - h.Point,
                Button = button,
            };
            return true;
        }

        return false;
    }

    /// <summary>A button was released. Returns true when it ended a drag.</summary>
    public bool PointerUp(Vector2 position, Vector2 viewportSize, MouseButton button = MouseButton.Left)
    {
        _viewport = viewportSize;
        _lastPosition = position;
        if (_overlayPress != 0)
        {
            uint pressed = _overlayPress;
            _overlayPress = 0;
            if (OverlayHit(position) == pressed)
            {
                OverlayElement element = new(_scene, pressed);
                OverlayClicked?.Invoke(element);
                if (_overlayHandlers.TryGetValue(pressed, out var handlers))
                {
                    foreach (Action<OverlayElement> handler in handlers.Click.ToArray())
                    {
                        handler(element);
                    }
                }
            }

            return true;
        }

        TryPick(position, out Ray ray, out RayHit? hit);

        bool endedDrag = false;
        if (_drag is { } drag)
        {
            _drag = null;
            if (drag.Started)
            {
                endedDrag = true;
                NodeEvent end = new(NodeEventKind.DragEnd, new Node(_scene, drag.Node), hit?.Node, hit, ray, position, button)
                {
                    DragPoint = drag.LastPoint,
                };
                Dispatch(end, Chain(drag.Node));
            }
        }

        List<uint> chain = hit is { } h ? Chain(h.Node.Id) : [];
        if (hit is { } upHit)
        {
            Dispatch(NodeEventKind.PointerUp, chain, upHit.Node, hit, ray, position, button);
        }

        if (!endedDrag && _press is { } press && press.Button == button && hit is { } clickHit
            && Vector2.Distance(press.Position, position) <= ClickTolerance)
        {
            // Only nodes pressed and released on receive the click.
            List<uint> common = chain.Where(press.Chain.Contains).ToList();
            Dispatch(NodeEventKind.Click, common, clickHit.Node, hit, ray, position, button);

            long now = Environment.TickCount64;
            if (_lastClick is { } last && last.Node == clickHit.Node.Id && now - last.Timestamp <= DoubleClickTime.TotalMilliseconds)
            {
                Dispatch(NodeEventKind.DoubleClick, common, clickHit.Node, hit, ray, position, button);
                _lastClick = null;
            }
            else
            {
                _lastClick = (clickHit.Node.Id, now);
            }
        }

        _press = null;
        UpdateHover(ray, position, hit);
        return endedDrag;
    }

    /// <summary>The pointer left the viewport: clears hover state (a drag keeps going until the button is released).</summary>
    public void PointerExit()
    {
        if (_drag is not null)
        {
            return;
        }

        Ray ray = new(Vector3.Zero, -Vector3.UnitZ);
        foreach (uint id in _hoverChain.ToArray())
        {
            Dispatch(new NodeEvent(NodeEventKind.PointerLeave, new Node(_scene, id), null, null, ray, _lastPosition, MouseButton.Left), [id]);
        }

        _hoverChain.Clear();
        HoveredNode = null;
    }

    /// <summary>
    /// Feeds an <see cref="AppWindow"/> input event. Returns true when it was
    /// consumed (a drag), so camera controls can ignore it.
    /// </summary>
    public bool HandleInput(InputEvent input, Vector2 viewportSize) => input.Kind switch
    {
        InputEventKind.MouseMove => PointerMove(input.Position, viewportSize),
        InputEventKind.MouseDown => PointerDown(input.Position, viewportSize, input.Button),
        InputEventKind.MouseUp => PointerUp(input.Position, viewportSize, input.Button),
        InputEventKind.TouchBegin => PointerDown(input.Position, viewportSize),
        InputEventKind.TouchMove => PointerMove(input.Position, viewportSize),
        InputEventKind.TouchEnd => PointerUp(input.Position, viewportSize),
        _ => false,
    };

    private void UpdateDrag(DragState drag, Ray ray, Vector2 position, RayHit? hit)
    {
        if (!drag.Started)
        {
            if (_press is not { } press || Vector2.Distance(press.Position, position) <= ClickTolerance)
            {
                return;
            }

            drag.Started = true;
            NodeEvent start = new(NodeEventKind.DragStart, new Node(_scene, drag.Node), hit?.Node, hit, ray, position, drag.Button)
            {
                DragPoint = drag.LastPoint,
            };
            Dispatch(start, Chain(drag.Node));
        }

        float denominator = Vector3.Dot(ray.Direction, drag.PlaneNormal);
        if (MathF.Abs(denominator) < 1e-5f)
        {
            return;
        }

        float distance = Vector3.Dot(drag.PlanePoint - ray.Origin, drag.PlaneNormal) / denominator;
        if (distance < 0f)
        {
            return;
        }

        Vector3 point = ray.At(distance);
        Vector3 delta = point - drag.LastPoint;
        drag.LastPoint = point;

        Node node = new(_scene, drag.Node);
        if (drag.Mode != DragMode.EventsOnly)
        {
            Vector3 world = point + drag.GrabOffset;
            Node? parent = node.Parent;
            if (parent is not null && Matrix4x4.Invert(parent.WorldMatrix, out Matrix4x4 inverse))
            {
                world = Vector3.Transform(world, inverse);
            }

            node.Position = world;
        }

        NodeEvent move = new(NodeEventKind.Drag, node, hit?.Node, hit, ray, position, drag.Button)
        {
            DragPoint = point,
            DragDelta = delta,
        };
        Dispatch(move, Chain(drag.Node));
    }

    private void UpdateHover(Ray ray, Vector2 position, RayHit? hit)
    {
        HoveredNode = hit?.Node;
        List<uint> chain = hit is { } h ? Chain(h.Node.Id) : [];
        List<uint> newHover = chain.Where(HasHoverHandlers).ToList();

        foreach (uint left in _hoverChain.Where(id => !newHover.Contains(id)).ToArray())
        {
            Dispatch(new NodeEvent(NodeEventKind.PointerLeave, new Node(_scene, left), hit?.Node, hit, ray, position, MouseButton.Left), [left]);
        }

        foreach (uint entered in newHover.Where(id => !_hoverChain.Contains(id)).ToArray())
        {
            Dispatch(new NodeEvent(NodeEventKind.PointerEnter, new Node(_scene, entered), hit?.Node, hit, ray, position, MouseButton.Left), [entered]);
        }

        _hoverChain.Clear();
        _hoverChain.AddRange(newHover);
    }

    private bool HasHoverHandlers(uint id) =>
        _handlers.TryGetValue(id, out Dictionary<NodeEventKind, List<Action<NodeEvent>>>? kinds)
        && (kinds.ContainsKey(NodeEventKind.PointerEnter) || kinds.ContainsKey(NodeEventKind.PointerLeave));

    private void Dispatch(NodeEventKind kind, List<uint> chain, Node hitNode, RayHit? hit, Ray ray, Vector2 position, MouseButton button) =>
        Dispatch(new NodeEvent(kind, hitNode, hitNode, hit, ray, position, button), chain);

    /// <summary>Runs the global event then handlers from the hit node up through its ancestors.</summary>
    private void Dispatch(NodeEvent e, List<uint> chain)
    {
        Event?.Invoke(e);
        foreach (uint id in chain)
        {
            if (e.Handled)
            {
                break;
            }

            if (!_handlers.TryGetValue(id, out Dictionary<NodeEventKind, List<Action<NodeEvent>>>? kinds)
                || !kinds.TryGetValue(e.Kind, out List<Action<NodeEvent>>? list))
            {
                continue;
            }

            e.Target = new Node(_scene, id);
            foreach (Action<NodeEvent> handler in list.ToArray())
            {
                handler(e);
            }
        }
    }

    /// <summary>The node and its ancestors, nearest first (the scene root excluded).</summary>
    private List<uint> Chain(uint id)
    {
        List<uint> chain = [];
        uint root = _scene.Root.Id;
        Node? current = new(_scene, id);
        while (current is not null && current.Id != 0 && current.Id != root)
        {
            chain.Add(current.Id);
            current = current.Parent;
        }

        return chain;
    }

    private bool TryPick(Vector2 position, out Ray ray, out RayHit? hit)
    {
        hit = null;
        ray = default;
        Node? camera = Camera ?? _scene.ActiveCamera;
        if (camera is null || _viewport.X <= 0f || _viewport.Y <= 0f)
        {
            return false;
        }

        float ndcX = (position.X / _viewport.X * 2f) - 1f;
        float ndcY = 1f - (position.Y / _viewport.Y * 2f);
        ray = _scene.CreateCameraRay(camera, ndcX, ndcY, _viewport.X / _viewport.Y);
        IReadOnlyList<RayHit> hits = _scene.Raycast(ray, RaycastOptions, 1);
        if (hits.Count > 0)
        {
            hit = hits[0];
        }

        return true;
    }

    private Vector3 CameraForward()
    {
        Node? camera = Camera ?? _scene.ActiveCamera;
        if (camera is null)
        {
            return Vector3.UnitZ;
        }

        Matrix4x4 world = camera.WorldMatrix;
        return Vector3.Normalize(new Vector3(world.M31, world.M32, world.M33));
    }
}
