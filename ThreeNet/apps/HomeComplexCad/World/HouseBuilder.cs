using System.Numerics;
using ThreeNet;

namespace HomeComplexCad.World;

public enum RoomKind
{
    Living,
    Family,
    Kitchen,
    Bathroom,
    MasterBedroom,
    GuestRoom,
    Playroom,
    Hall,
}

public enum RoofStyle
{
    Gable,
    Flat,
}

/// <summary>An axis aligned room in house coordinates (front of the house is +Z).</summary>
public sealed record RoomSpec(string Name, RoomKind Kind, float X0, float Z0, float X1, float Z1, int Floor = 0)
{
    public float Width => X1 - X0;

    public float Depth => Z1 - Z0;

    public Vector2 Centre => new((X0 + X1) * 0.5f, (Z0 + Z1) * 0.5f);
}

/// <summary>Everything needed to build one house on one lot.</summary>
public sealed class HousePlan
{
    public required PropertyInfo Info { get; init; }

    public required float Width { get; init; }

    public required float Depth { get; init; }

    public int Floors { get; init; } = 1;

    public float FloorHeight { get; init; } = 3.2f;

    public RoofStyle Roof { get; init; } = RoofStyle.Gable;

    public List<RoomSpec> Rooms { get; init; } = [];

    /// <summary>Builds furniture, room lights and ceilings (show units only).</summary>
    public bool Furnished { get; init; }

    public bool Pool { get; init; }

    public bool Carport { get; init; } = true;

    /// <summary>Façade finish: 0 = stucco + brick, 1 = stucco + timber, 2 = modern dark.</summary>
    public int Style { get; init; }
}

/// <summary>A built house: its root node plus the locations of its rooms.</summary>
public sealed record BuiltHouse(Node Root, HousePlan Plan, List<(RoomSpec Room, Vector3 Eye, Vector3 Target)> RoomViews);

/// <summary>
/// Turns a <see cref="HousePlan"/> into geometry: slabs, walls with window and
/// door openings, glazing, gable or flat roofs, stairs, porch, fence, carport,
/// garden and (for show units) the furnished interior.
/// </summary>
public sealed class HouseBuilder(Assets assets, Palette palette, ModelLibrary models, InteriorBuilder interior)
{
    private const float WallThickness = 0.16f;
    private const float WindowBottom = 0.9f;
    private const float WindowTop = 2.3f;
    private const float DoorTop = 2.2f;

    private readonly Assets _assets = assets;
    private readonly Palette _palette = palette;
    private readonly ModelLibrary _models = models;
    private readonly InteriorBuilder _interior = interior;

    /// <summary>Lights inside the house, switched on after sunset.</summary>
    public List<Node> InteriorLights { get; } = [];

    /// <summary>Porch and garden lights.</summary>
    public List<Node> OutdoorLights { get; } = [];

    public BuiltHouse Build(HousePlan plan, Vector3 lotCentre, float yaw)
    {
        Scene scene = _assets.Scene;
        Node root = scene.CreateNode(null, plan.Info.Id);
        root.Position = lotCentre;
        root.EulerAngles = new Vector3(0f, yaw, 0f);

        Material facade = plan.Style == 2 ? _palette.AccentWall : _palette.Stucco;
        Material interiorWall = plan.Style == 1 ? _palette.WarmWall : _palette.WhiteWall;

        // Plinth raises the ground floor above the garden.
        const float plinth = 0.3f;
        _assets.AddSlab(root, Vector3.Zero, new Vector2(plan.Width + 0.4f, plan.Depth + 0.4f), plinth, 0f, _palette.Concrete, "plinth");

        List<(RoomSpec, Vector3, Vector3)> views = [];
        for (int floor = 0; floor < plan.Floors; floor++)
        {
            float baseY = plinth + (floor * plan.FloorHeight);
            List<RoomSpec> rooms = plan.Rooms.Where(r => r.Floor == floor).ToList();
            BuildFloor(plan, root, rooms, floor, baseY, facade, interiorWall);

            foreach (RoomSpec room in rooms)
            {
                if (!plan.Furnished)
                {
                    continue;
                }

                _interior.Furnish(root, room, baseY, plan);
                if (room.Kind == RoomKind.Hall)
                {
                    continue;
                }

                // The renderer uploads at most 64 lights, so only lived-in rooms get one.
                Node light = _assets.Scene.AddLight(
                    Light.Point(new Vector3(1f, 0.86f, 0.66f), 7f, range: 9f) with { Enabled = false },
                    root,
                    $"{room.Name}-light");
                light.Position = new Vector3(room.Centre.X, baseY + plan.FloorHeight - 0.45f, room.Centre.Y);
                InteriorLights.Add(light);
                Node fixture = _assets.AddBox(root, light.Position + new Vector3(0f, 0.3f, 0f), new Vector3(0.5f, 0.05f, 0.5f), _palette.LampGlow, "ceiling-lamp");
                fixture.CastShadow = false;

                // View from the doorway corner towards the room centre.
                Vector3 eye = new(room.X0 + 0.7f, baseY + 1.62f, room.Z1 - 0.7f);
                Vector3 target = new(room.Centre.X, baseY + 1.1f, room.Centre.Y);
                views.Add((room, eye, target));
            }
        }

        float wallTop = plinth + (plan.Floors * plan.FloorHeight);
        BuildRoof(plan, root, wallTop, facade);
        if (plan.Floors > 1)
        {
            BuildStairs(plan, root, plinth);
        }

        BuildYard(plan, root, plinth);
        return new BuiltHouse(root, plan, views);
    }

    // ------------------------------------------------------------------ floors

    private void BuildFloor(HousePlan plan, Node root, List<RoomSpec> rooms, int floor, float baseY, Material facade, Material interiorWall)
    {
        float hw = plan.Width * 0.5f;
        float hd = plan.Depth * 0.5f;
        float height = plan.FloorHeight;

        foreach (RoomSpec room in rooms)
        {
            Material floorMaterial = room.Kind switch
            {
                RoomKind.Bathroom => _palette.Ceramic,
                RoomKind.Kitchen => _palette.Marble,
                RoomKind.Living or RoomKind.Hall => _palette.Marble,
                _ => _palette.Parquet,
            };

            // Upper floor slabs leave the stairwell open.
            if (floor > 0 && room.Kind == RoomKind.Hall)
            {
                continue;
            }

            Node slab = _assets.AddSlab(root, new Vector3(room.Centre.X, 0f, room.Centre.Y), new Vector2(room.Width, room.Depth), 0.12f, baseY - 0.12f, floorMaterial, $"{room.Name}-floor");
            slab.CastShadow = floor > 0;

            if (plan.Furnished)
            {
                Node ceiling = _assets.AddFloor(root, new Vector3(room.Centre.X, baseY + height - 0.02f, room.Centre.Y), new Vector2(room.Width, room.Depth), _palette.WhiteWall, faceUp: false, "ceiling");
                ceiling.ReceiveShadow = false;
            }
        }

        // Exterior walls: one segment per room edge on the perimeter.
        HashSet<string> done = [];
        foreach (RoomSpec room in rooms)
        {
            bool frontDoor = floor == 0 && room.Kind == RoomKind.Living;
            AddExteriorEdge(root, room, room.X0, room.Z1, room.X1, room.Z1, hd, baseY, height, facade, frontDoor, plan, done, isFront: true);
            AddExteriorEdge(root, room, room.X0, room.Z0, room.X1, room.Z0, hd, baseY, height, facade, false, plan, done, isFront: false);
            AddExteriorEdge(root, room, room.X0, room.Z0, room.X0, room.Z1, hw, baseY, height, facade, false, plan, done, isFront: false);
            AddExteriorEdge(root, room, room.X1, room.Z0, room.X1, room.Z1, hw, baseY, height, facade, false, plan, done, isFront: false);
        }

        // Interior partitions with a doorway in each shared edge.
        if (!plan.Furnished)
        {
            return;
        }

        foreach (RoomSpec a in rooms)
        {
            foreach (RoomSpec b in rooms)
            {
                if (ReferenceEquals(a, b))
                {
                    continue;
                }

                // Shared edge along X (a is behind b).
                if (MathF.Abs(a.Z1 - b.Z0) < 0.01f)
                {
                    float from = MathF.Max(a.X0, b.X0);
                    float to = MathF.Min(a.X1, b.X1);
                    if (to - from > 0.5f && done.Add($"ix:{a.Z1:0.00}:{from:0.00}:{to:0.00}"))
                    {
                        Wall(root, new Vector3(from, baseY, a.Z1), new Vector3(to, baseY, a.Z1), height, interiorWall,
                            to - from > 1.6f ? [Opening.Door((to - from) * 0.5f, 0.95f)] : []);
                    }
                }

                // Shared edge along Z (a is left of b).
                if (MathF.Abs(a.X1 - b.X0) < 0.01f)
                {
                    float from = MathF.Max(a.Z0, b.Z0);
                    float to = MathF.Min(a.Z1, b.Z1);
                    if (to - from > 0.5f && done.Add($"iz:{a.X1:0.00}:{from:0.00}:{to:0.00}"))
                    {
                        Wall(root, new Vector3(a.X1, baseY, from), new Vector3(a.X1, baseY, to), height, interiorWall,
                            to - from > 1.6f ? [Opening.Door((to - from) * 0.5f, 0.95f)] : []);
                    }
                }
            }
        }
    }

    private void AddExteriorEdge(
        Node root, RoomSpec room, float x0, float z0, float x1, float z1, float halfExtent, float baseY, float height,
        Material facade, bool frontDoor, HousePlan plan, HashSet<string> done, bool isFront)
    {
        bool alongX = MathF.Abs(z0 - z1) < 0.01f;
        float fixedCoordinate = alongX ? z0 : x0;
        if (MathF.Abs(MathF.Abs(fixedCoordinate) - halfExtent) > 0.01f)
        {
            return; // Not on the perimeter.
        }

        if (!done.Add($"ex:{x0:0.00}:{z0:0.00}:{x1:0.00}:{z1:0.00}"))
        {
            return;
        }

        float length = alongX ? x1 - x0 : z1 - z0;
        List<Opening> openings = [];
        if (frontDoor)
        {
            openings.Add(Opening.Door(length * 0.3f, 1.1f));
            openings.Add(Opening.Window(length * 0.72f, MathF.Min(2.2f, length * 0.4f)));
        }
        else if (room.Kind == RoomKind.Bathroom)
        {
            openings.Add(new Opening(length * 0.5f, 0.8f, 1.6f, 2.2f, false));
        }
        else if (length > 1.8f)
        {
            // Wide rooms get a pair of windows, narrow ones a single one.
            if (length > 5f)
            {
                openings.Add(Opening.Window(length * 0.28f, 1.5f));
                openings.Add(Opening.Window(length * 0.72f, 1.5f));
            }
            else
            {
                openings.Add(Opening.Window(length * 0.5f, MathF.Min(1.8f, length - 1f)));
            }
        }

        Vector3 start = new(x0, baseY, z0);
        Vector3 end = new(x1, baseY, z1);
        Wall(root, start, end, height, facade, openings, glaze: true);

        // Façade detailing on the street side: brick or timber cladding below the windows.
        if (isFront && plan.Style != 2)
        {
            Material cladding = plan.Style == 0 ? _palette.Brick : _palette.Timber;
            Vector3 centre = (start + end) * 0.5f;
            Node strip = _assets.AddBox(root, new Vector3(centre.X, baseY + 0.45f, centre.Z + (WallThickness * 0.5f) + 0.02f),
                new Vector3(length, 0.9f, 0.04f), cladding, "cladding");
            strip.CastShadow = false;
        }
    }

    /// <summary>An opening in a wall: offset of its centre along the wall, width, sill and head.</summary>
    private readonly record struct Opening(float Centre, float Width, float Bottom, float Top, bool IsDoor)
    {
        public static Opening Door(float centre, float width) => new(centre, width, 0f, DoorTop, true);

        public static Opening Window(float centre, float width) => new(centre, width, WindowBottom, WindowTop, false);
    }

    /// <summary>
    /// A straight wall from <paramref name="start"/> to <paramref name="end"/> split
    /// into solid pieces around its openings, with glass in the windows.
    /// </summary>
    private void Wall(Node root, Vector3 start, Vector3 end, float height, Material material, IReadOnlyList<Opening> openings, bool glaze = false)
    {
        Vector3 direction = end - start;
        float length = direction.Length();
        if (length < 0.05f)
        {
            return;
        }

        direction /= length;
        float yaw = MathF.Atan2(direction.X, direction.Z) - (MathF.PI / 2f);
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);

        void Piece(float from, float to, float bottom, float top, Material pieceMaterial, float thickness, string name)
        {
            if (to - from < 0.02f || top - bottom < 0.02f)
            {
                return;
            }

            float middle = (from + to) * 0.5f;
            Vector3 centre = start + (direction * middle) + new Vector3(0f, (bottom + top) * 0.5f, 0f);
            Node node = _assets.Scene.AddMesh(_assets.Box(to - from, top - bottom, thickness), pieceMaterial, root, name);
            node.Position = centre;
            node.Rotation = rotation;
        }

        float cursor = 0f;
        foreach (Opening opening in openings.OrderBy(o => o.Centre))
        {
            float left = Math.Clamp(opening.Centre - (opening.Width * 0.5f), 0f, length);
            float right = Math.Clamp(opening.Centre + (opening.Width * 0.5f), 0f, length);
            Piece(cursor, left, 0f, height, material, WallThickness, "wall");
            Piece(left, right, 0f, opening.Bottom, material, WallThickness, "wall-sill");
            Piece(left, right, opening.Top, height, material, WallThickness, "wall-head");

            if (!opening.IsDoor && glaze)
            {
                Piece(left, right, opening.Bottom, opening.Top, _palette.WindowGlow, 0.03f, "window");
                // Sill and a mullion give the window some depth.
                Piece(left - 0.05f, right + 0.05f, opening.Bottom - 0.06f, opening.Bottom, _palette.WhiteWall, WallThickness + 0.1f, "window-sill");
                float mid = (left + right) * 0.5f;
                Piece(mid - 0.025f, mid + 0.025f, opening.Bottom, opening.Top, _palette.DarkMetal, 0.06f, "mullion");
            }

            cursor = right;
        }

        Piece(cursor, length, 0f, height, material, WallThickness, "wall");
    }

    // ------------------------------------------------------------------- roofs

    private void BuildRoof(HousePlan plan, Node root, float wallTop, Material facade)
    {
        Scene scene = _assets.Scene;
        if (plan.Roof == RoofStyle.Gable)
        {
            (Geometry slopes, Geometry gables) = ShapeBuilder.GableRoof(scene, plan.Width, plan.Depth, rise: plan.Depth * 0.32f, overhang: 0.6f);
            Node roof = scene.AddMesh(slopes, _palette.RoofTile, root, "roof");
            roof.Position = new Vector3(0f, wallTop, 0f);
            Node ends = scene.AddMesh(gables, facade, root, "gable-ends");
            ends.Position = new Vector3(0f, wallTop, 0f);
            // Fascia boards along the eaves.
            _assets.AddBox(root, new Vector3(0f, wallTop - 0.05f, (plan.Depth * 0.5f) + 0.6f), new Vector3(plan.Width + 1.2f, 0.18f, 0.05f), _palette.WhiteWall, "fascia");
            _assets.AddBox(root, new Vector3(0f, wallTop - 0.05f, -(plan.Depth * 0.5f) - 0.6f), new Vector3(plan.Width + 1.2f, 0.18f, 0.05f), _palette.WhiteWall, "fascia");
        }
        else
        {
            // Modern flat roof: slab with a generous overhang and a parapet.
            _assets.AddSlab(root, Vector3.Zero, new Vector2(plan.Width + 1.2f, plan.Depth + 1.2f), 0.25f, wallTop, _palette.WhiteWall, "roof-slab");
            _assets.AddBox(root, new Vector3(0f, wallTop + 0.45f, (plan.Depth * 0.5f) + 0.55f), new Vector3(plan.Width + 1.2f, 0.4f, 0.1f), facade, "parapet");
            _assets.AddBox(root, new Vector3(0f, wallTop + 0.45f, -(plan.Depth * 0.5f) - 0.55f), new Vector3(plan.Width + 1.2f, 0.4f, 0.1f), facade, "parapet");
            _assets.AddBox(root, new Vector3((plan.Width * 0.5f) + 0.55f, wallTop + 0.45f, 0f), new Vector3(0.1f, 0.4f, plan.Depth + 1.2f), facade, "parapet");
            _assets.AddBox(root, new Vector3(-(plan.Width * 0.5f) - 0.55f, wallTop + 0.45f, 0f), new Vector3(0.1f, 0.4f, plan.Depth + 1.2f), facade, "parapet");
            // Solar panels on the roof of the modern units.
            for (int i = 0; i < 4; i++)
            {
                Node panel = _assets.AddBox(root, new Vector3(-plan.Width * 0.3f + (i * 1.15f), wallTop + 0.45f, -plan.Depth * 0.15f),
                    new Vector3(1f, 0.05f, 1.7f), _palette.SignBlue, "solar-panel");
                panel.EulerAngles = new Vector3(-0.25f, 0f, 0f);
            }
        }

        // Upper floor slab edge for two storey houses.
        if (plan.Floors > 1)
        {
            _assets.AddBox(root, new Vector3(0f, 0.3f + plan.FloorHeight, (plan.Depth * 0.5f) + 0.12f), new Vector3(plan.Width + 0.1f, 0.25f, 0.1f), _palette.WhiteWall, "floor-band");
        }
    }

    private void BuildStairs(HousePlan plan, Node root, float plinth)
    {
        if (plan.Rooms.FirstOrDefault(r => r.Kind == RoomKind.Hall && r.Floor == 0) is not { } hall)
        {
            return;
        }

        const int steps = 17;
        float rise = plan.FloorHeight / steps;
        float run = MathF.Min(0.26f, (hall.Depth - 0.6f) / steps);
        float x = hall.X1 - 0.65f;
        for (int i = 0; i < steps; i++)
        {
            float z = hall.Z1 - 0.4f - (i * run);
            _assets.AddBox(root, new Vector3(x, plinth + (rise * (i + 0.5f)), z), new Vector3(1.0f, rise, run), _palette.DarkTimber, "step");
        }

        // Glass balustrade along the open side.
        float length = steps * run;
        Node rail = _assets.AddBox(root, new Vector3(x - 0.52f, plinth + (plan.FloorHeight * 0.5f) + 0.45f, hall.Z1 - 0.4f - (length * 0.5f)), new Vector3(0.03f, 0.9f, length), _palette.Glass, "balustrade");
        rail.EulerAngles = new Vector3(MathF.Atan2(plan.FloorHeight, length), 0f, 0f);
        rail.CastShadow = false;
    }

    // -------------------------------------------------------------------- yard

    private void BuildYard(HousePlan plan, Node root, float plinth)
    {
        Scene scene = _assets.Scene;
        Vector2 lot = plan.Info.LotSize;
        float front = plan.Depth * 0.5f;
        float lotFront = lot.Y * 0.5f;

        // Terrace and front steps.
        _assets.AddSlab(root, new Vector3(-plan.Width * 0.2f, 0f, front + 1.1f), new Vector2(plan.Width * 0.55f, 2.2f), plinth, 0f, _palette.Paving, "terrace");
        _assets.AddBox(root, new Vector3(-plan.Width * 0.2f, plinth + 2.6f, front + 0.4f), new Vector3(0.18f, 0.25f, 0.12f), _palette.LampGlow, "porch-lamp").CastShadow = false;
        // Only the show units get a real porch light (the renderer keeps 64 lights).
        if (plan.Furnished)
        {
            Node porchLight = scene.AddLight(Light.Point(new Vector3(1f, 0.82f, 0.55f), 3.5f, range: 7f) with { Enabled = false }, root, "porch-light");
            porchLight.Position = new Vector3(-plan.Width * 0.2f, plinth + 2.6f, front + 0.4f);
            OutdoorLights.Add(porchLight);
        }

        // Walkway from the gate to the door.
        _assets.AddSlab(root, new Vector3(-plan.Width * 0.2f, 0f, (front + lotFront) * 0.5f + 1.1f), new Vector2(1.2f, lotFront - front - 2.2f), 0.04f, 0f, _palette.Paving, "walkway");

        // Driveway and carport with a parked car.
        if (plan.Carport)
        {
            float cx = (plan.Width * 0.5f) - 1.6f;
            Vector3 carportCentre = new(cx, 0f, front + 3f);
            _assets.AddSlab(root, new Vector3(cx, 0f, (front + lotFront) * 0.5f + 1f), new Vector2(3.2f, lotFront - front), 0.05f, 0f, _palette.Paving, "driveway");
            foreach (float px in new[] { cx - 1.5f, cx + 1.5f })
            {
                _assets.AddBox(root, new Vector3(px, 1.35f, front + 4.9f), new Vector3(0.12f, 2.7f, 0.12f), _palette.DarkMetal, "carport-post");
            }

            _assets.AddBox(root, new Vector3(cx, 2.75f, front + 3f), new Vector3(3.4f, 0.08f, 4.2f), _palette.Glass, "carport-roof").CastShadow = true;
            if (_models.Place("car", root, carportCentre + new Vector3(0f, 0.05f, 0.3f), MathF.PI, 4.4f) is null)
            {
                _assets.AddBox(root, carportCentre + new Vector3(0f, 0.75f, 0.3f), new Vector3(1.8f, 1.1f, 4.2f), _palette.WhiteWall, "car-placeholder");
            }
        }

        // Front fence with a gate gap, lawn edges and shrubs.
        float fenceZ = lotFront - 0.2f;
        for (float x = -lot.X * 0.5f; x < lot.X * 0.5f; x += 1.2f)
        {
            bool gate = MathF.Abs(x - (-plan.Width * 0.2f)) < 1f || (plan.Carport && MathF.Abs(x - ((plan.Width * 0.5f) - 1.6f)) < 1.8f);
            if (gate)
            {
                continue;
            }

            _assets.AddBox(root, new Vector3(x + 0.6f, 0.45f, fenceZ), new Vector3(1.2f, 0.9f, 0.06f), _palette.DarkMetal, "fence").CastShadow = false;
        }

        Material hedge = _palette.Foliage;
        for (int i = 0; i < 5; i++)
        {
            Node shrub = _assets.AddBox(root, new Vector3((-plan.Width * 0.5f) + 0.8f + (i * 0.9f), 0.45f, front + 0.6f), new Vector3(0.8f, 0.9f, 0.7f), hedge, "shrub");
            shrub.Scale = new Vector3(1f, 0.8f + (i % 2 * 0.3f), 1f);
        }

        // House number plate by the gate.
        Texture number = TextTextures.Create(scene, plan.Info.Block, Avalonia.Media.Color.FromRgb(20, 26, 34), Avalonia.Media.Color.FromRgb(236, 240, 244), 256, 128, 64);
        Material numberMaterial = scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 0.3f, 0.4f) with
        {
            BaseColorMap = number,
            EmissiveMap = number,
            Emissive = new Vector3(0.4f),
            EmissiveIntensity = 0.6f,
        });
        Node plate = scene.AddMesh(_assets.Plane(0.6f, 0.3f), numberMaterial, root, "house-number");
        plate.Position = new Vector3(-plan.Width * 0.2f - 1.1f, 1.2f, fenceZ + 0.05f);
        _assets.AddBox(root, new Vector3(-plan.Width * 0.2f - 1.1f, 0.6f, fenceZ), new Vector3(0.7f, 1.2f, 0.25f), _palette.Brick, "gate-pillar");

        // A garden tree in the back yard.
        _interior.Tree(root, new Vector3(-plan.Width * 0.25f, 0f, -front - 3f), 1.1f);

        if (plan.Pool)
        {
            BuildPool(root, new Vector3(plan.Width * 0.15f, 0f, -front - 4f), new Vector2(7f, 3.5f));
        }
    }

    public void BuildPool(Node root, Vector3 centre, Vector2 size)
    {
        // Coping, water surface slightly below it, tiled floor and deck chairs.
        _assets.AddSlab(root, centre, new Vector2(size.X + 1.6f, size.Y + 1.6f), 0.08f, 0f, _palette.Paving, "pool-deck");
        foreach ((Vector3 offset, Vector3 dims) in new[]
        {
            (new Vector3(0f, 0f, (size.Y * 0.5f) + 0.2f), new Vector3(size.X + 0.4f, 0.2f, 0.4f)),
            (new Vector3(0f, 0f, -(size.Y * 0.5f) - 0.2f), new Vector3(size.X + 0.4f, 0.2f, 0.4f)),
            (new Vector3((size.X * 0.5f) + 0.2f, 0f, 0f), new Vector3(0.4f, 0.2f, size.Y)),
            (new Vector3(-(size.X * 0.5f) - 0.2f, 0f, 0f), new Vector3(0.4f, 0.2f, size.Y)),
        })
        {
            _assets.AddBox(root, centre + offset + new Vector3(0f, 0.18f, 0f), dims, _palette.Marble, "pool-coping");
        }

        _assets.AddFloor(root, centre + new Vector3(0f, 0.02f, 0f), size, _palette.Ceramic, true, "pool-floor").ReceiveShadow = true;
        Node water = _assets.AddFloor(root, centre + new Vector3(0f, 0.16f, 0f), size, _palette.Water, true, "pool-water");
        water.ReceiveShadow = false;

        for (int i = 0; i < 2; i++)
        {
            Vector3 chair = centre + new Vector3((-size.X * 0.25f) + (i * 1.6f), 0f, (size.Y * 0.5f) + 1.4f);
            Node lounger = _assets.AddBox(root, chair + new Vector3(0f, 0.35f, 0f), new Vector3(0.7f, 0.12f, 1.9f), _palette.Linen, "lounger");
            lounger.EulerAngles = new Vector3(0f, 0f, 0f);
            _assets.AddBox(root, chair + new Vector3(0f, 0.6f, -0.75f), new Vector3(0.7f, 0.5f, 0.12f), _palette.Linen, "lounger-back").EulerAngles = new Vector3(0.6f, 0f, 0f);
        }
    }
}
