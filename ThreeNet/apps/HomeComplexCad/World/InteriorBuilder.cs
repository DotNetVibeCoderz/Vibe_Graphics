using System.Numerics;
using ThreeNet;

namespace HomeComplexCad.World;

/// <summary>
/// Furnishes rooms. Hero pieces (sofa, bed, kitchen island, trees, cars) come
/// from the Rodin generated models when present; everything else is built from
/// primitives with the shared palette.
/// </summary>
public sealed class InteriorBuilder(Assets assets, Palette palette, ModelLibrary models)
{
    // Native facing of the generated models, measured once in the viewer.
    private const float SofaYaw = 0f;
    private const float BedYaw = MathF.PI / 2f;
    private const float IslandYaw = 0f;

    private readonly Assets _assets = assets;
    private readonly Palette _palette = palette;
    private readonly ModelLibrary _models = models;
    private readonly Random _random = new(1207);

    public void Furnish(Node root, RoomSpec room, float floorY, HousePlan plan)
    {
        switch (room.Kind)
        {
            case RoomKind.Living:
                Living(root, room, floorY);
                break;
            case RoomKind.Family:
                Family(root, room, floorY);
                break;
            case RoomKind.Kitchen:
                Kitchen(root, room, floorY);
                break;
            case RoomKind.Bathroom:
                Bathroom(root, room, floorY);
                break;
            case RoomKind.MasterBedroom:
                Bedroom(root, room, floorY, master: true);
                break;
            case RoomKind.GuestRoom:
                Bedroom(root, room, floorY, master: false);
                break;
            case RoomKind.Playroom:
                Playroom(root, room, floorY);
                break;
            case RoomKind.Hall:
                Hall(root, room, floorY);
                break;
        }

        _ = plan;
    }

    // --------------------------------------------------------------- helpers

    private Node Box(Node root, float x, float y, float z, float w, float h, float d, Material material, string name = "furniture") =>
        _assets.AddBox(root, new Vector3(x, y, z), new Vector3(w, h, d), material, name);

    private void Sofa(Node root, Vector3 position, float yaw, float length)
    {
        if (_models.Place("sofa", root, position, yaw, length, SofaYaw) is not null)
        {
            return;
        }

        Node sofa = _assets.Scene.CreateNode(root, "sofa");
        sofa.Position = position;
        sofa.EulerAngles = new Vector3(0f, yaw, 0f);
        Box(sofa, 0f, 0.22f, 0f, length, 0.44f, 0.9f, _palette.Fabric, "sofa-base");
        Box(sofa, 0f, 0.6f, -0.35f, length, 0.5f, 0.2f, _palette.Fabric, "sofa-back");
        Box(sofa, -length * 0.5f + 0.1f, 0.45f, 0f, 0.2f, 0.5f, 0.9f, _palette.Fabric, "sofa-arm");
        Box(sofa, length * 0.5f - 0.1f, 0.45f, 0f, 0.2f, 0.5f, 0.9f, _palette.Fabric, "sofa-arm");
    }

    private void Plant(Node root, float x, float y, float z, float scale = 1f)
    {
        Box(root, x, y + (0.2f * scale), z, 0.35f * scale, 0.4f * scale, 0.35f * scale, _palette.Terracotta, "planter");
        Node leaves = _assets.Scene.AddMesh(_assets.Sphere(0.38f, 12), _palette.FoliageLight, root, "plant");
        leaves.Position = new Vector3(x, y + (0.75f * scale), z);
        leaves.Scale = new Vector3(scale, scale * 1.3f, scale);
    }

    private void Rug(Node root, RoomSpec room, float floorY, Vector2 size, Material material)
    {
        Node rug = _assets.AddFloor(root, new Vector3(room.Centre.X, floorY + 0.012f, room.Centre.Y), size, material, true, "rug");
        rug.ReceiveShadow = true;
    }

    private void Tv(Node root, float x, float floorY, float z, float yaw)
    {
        Node group = _assets.Scene.CreateNode(root, "tv-wall");
        group.Position = new Vector3(x, floorY, z);
        group.EulerAngles = new Vector3(0f, yaw, 0f);
        Box(group, 0f, 0.25f, 0f, 2.0f, 0.5f, 0.45f, _palette.DarkTimber, "tv-cabinet");
        Box(group, 0f, 1.25f, -0.05f, 1.6f, 0.92f, 0.05f, _palette.DarkMetal, "tv");
        Box(group, 0f, 1.25f, -0.02f, 1.52f, 0.85f, 0.01f, _palette.ScreenGlow, "tv-screen");
        Box(group, -0.75f, 0.62f, 0.05f, 0.14f, 0.25f, 0.14f, _palette.DarkMetal, "speaker");
        Box(group, 0.75f, 0.62f, 0.05f, 0.14f, 0.25f, 0.14f, _palette.DarkMetal, "speaker");
    }

    private void Chair(Node root, float x, float floorY, float z, float yaw, Material seat)
    {
        Node chair = _assets.Scene.CreateNode(root, "chair");
        chair.Position = new Vector3(x, floorY, z);
        chair.EulerAngles = new Vector3(0f, yaw, 0f);
        Box(chair, 0f, 0.45f, 0f, 0.45f, 0.06f, 0.45f, seat, "chair-seat");
        Box(chair, 0f, 0.75f, -0.2f, 0.45f, 0.55f, 0.05f, seat, "chair-back");
        foreach ((float lx, float lz) in new[] { (-0.19f, -0.19f), (0.19f, -0.19f), (-0.19f, 0.19f), (0.19f, 0.19f) })
        {
            Box(chair, lx, 0.21f, lz, 0.04f, 0.42f, 0.04f, _palette.DarkMetal, "chair-leg");
        }
    }

    public void Tree(Node? root, Vector3 position, float scale, bool detailed = false)
    {
        float yaw = (float)_random.NextDouble() * MathF.Tau;
        // The generated tree has ~20k vertices, so it is kept for the park where
        // it is seen up close; streets and yards use the light primitive tree.
        if (detailed && _models.Place("tree", root, position, yaw, 5.5f * scale) is not null)
        {
            return;
        }

        Node tree = _assets.Scene.CreateNode(root, "tree");
        tree.Position = position;
        tree.Scale = new Vector3(scale);
        Node trunk = _assets.Scene.AddMesh(_assets.Cylinder(0.18f, 3f, 8), _palette.Bark, tree, "trunk");
        trunk.Position = new Vector3(0f, 1.5f, 0f);
        foreach ((Vector3 offset, float radius) in new[]
        {
            (new Vector3(0f, 3.6f, 0f), 1.5f),
            (new Vector3(0.8f, 3.1f, 0.3f), 1.1f),
            (new Vector3(-0.7f, 3.2f, -0.4f), 1.15f),
            (new Vector3(0.1f, 4.4f, -0.2f), 0.95f),
        })
        {
            Node crown = _assets.Scene.AddMesh(_assets.Sphere(1f, 14), _random.NextDouble() > 0.4 ? _palette.Foliage : _palette.FoliageLight, tree, "canopy");
            crown.Position = offset;
            crown.Scale = new Vector3(radius, radius * 0.85f, radius);
        }
    }

    // ----------------------------------------------------------------- rooms

    private void Living(Node root, RoomSpec room, float y)
    {
        Rug(root, room, y, new Vector2(room.Width * 0.55f, room.Depth * 0.45f), _palette.FabricWarm);
        // Sofa faces the TV wall at the back of the room.
        Sofa(root, new Vector3(room.Centre.X, y, room.Centre.Y + (room.Depth * 0.18f)), MathF.PI, MathF.Min(2.4f, room.Width * 0.5f));
        Tv(root, room.Centre.X, y, room.Z0 + 0.3f, MathF.PI);
        Box(root, room.Centre.X, y + 0.2f, room.Centre.Y - 0.2f, 1.1f, 0.06f, 0.6f, _palette.Marble, "coffee-table");
        Box(root, room.Centre.X, y + 0.09f, room.Centre.Y - 0.2f, 0.9f, 0.18f, 0.4f, _palette.DarkMetal, "coffee-table-base");
        Plant(root, room.X0 + 0.4f, y, room.Z0 + 0.4f, 1.3f);
        Plant(root, room.X1 - 0.4f, y, room.Z1 - 0.5f, 1.0f);

        // Arc floor lamp and wall art.
        Box(root, room.X1 - 0.5f, y + 0.9f, room.Centre.Y + 0.6f, 0.04f, 1.8f, 0.04f, _palette.Chrome, "floor-lamp");
        Box(root, room.X1 - 0.5f, y + 1.8f, room.Centre.Y + 0.6f, 0.4f, 0.25f, 0.4f, _palette.LampGlow, "floor-lamp-shade");
        Box(root, room.X0 + 0.1f, y + 1.6f, room.Centre.Y, 0.03f, 0.8f, 1.4f, _palette.AccentWall, "wall-art");
    }

    private void Family(Node root, RoomSpec room, float y)
    {
        Rug(root, room, y, new Vector2(room.Width * 0.5f, room.Depth * 0.5f), _palette.Fabric);
        Sofa(root, new Vector3(room.Centre.X, y, room.Z1 - 1.2f), MathF.PI, MathF.Min(2.3f, room.Width * 0.5f));
        Sofa(root, new Vector3(room.X0 + 1.0f, y, room.Centre.Y), -MathF.PI / 2f, 1.9f);
        Tv(root, room.Centre.X, y, room.Z0 + 0.3f, MathF.PI);

        // Bookshelf with coloured book blocks.
        Box(root, room.X1 - 0.25f, y + 1.0f, room.Centre.Y, 0.4f, 2.0f, 1.6f, _palette.Timber, "bookshelf");
        Material[] covers = [_palette.SignBlue, _palette.FabricWarm, _palette.SignGreen, _palette.Linen];
        for (int shelf = 0; shelf < 4; shelf++)
        {
            for (int book = 0; book < 6; book++)
            {
                Box(root, room.X1 - 0.47f, y + 0.3f + (shelf * 0.45f), room.Centre.Y - 0.6f + (book * 0.22f), 0.05f, 0.3f, 0.16f,
                    covers[(shelf + book) % covers.Length], "book");
            }
        }

        Box(root, room.Centre.X, y + 0.22f, room.Centre.Y, 0.9f, 0.44f, 0.9f, _palette.DarkTimber, "ottoman");
        Plant(root, room.X1 - 0.4f, y, room.Z1 - 0.4f, 1.2f);
    }

    private void Kitchen(Node root, RoomSpec room, float y)
    {
        // Base cabinets and wall cabinets along the back wall.
        float counterLength = room.Width - 0.6f;
        float z = room.Z0 + 0.33f;
        Box(root, room.Centre.X, y + 0.43f, z, counterLength, 0.86f, 0.6f, _palette.DarkTimber, "base-cabinets");
        Box(root, room.Centre.X, y + 0.88f, z, counterLength + 0.04f, 0.05f, 0.64f, _palette.Marble, "countertop");
        Box(root, room.Centre.X, y + 1.9f, room.Z0 + 0.2f, counterLength, 0.75f, 0.35f, _palette.WhiteWall, "wall-cabinets");
        Box(root, room.Centre.X, y + 1.2f, room.Z0 + 0.09f, counterLength, 0.6f, 0.02f, _palette.Ceramic, "backsplash");

        // Sink, tap and induction hob.
        Box(root, room.Centre.X - (counterLength * 0.25f), y + 0.9f, z, 0.6f, 0.02f, 0.42f, _palette.Chrome, "sink");
        Box(root, room.Centre.X - (counterLength * 0.25f), y + 1.05f, z - 0.2f, 0.03f, 0.3f, 0.03f, _palette.Chrome, "tap");
        Box(root, room.Centre.X + (counterLength * 0.25f), y + 0.915f, z, 0.6f, 0.01f, 0.5f, _palette.DarkMetal, "hob");
        Box(root, room.Centre.X + (counterLength * 0.25f), y + 1.85f, room.Z0 + 0.3f, 0.7f, 0.35f, 0.5f, _palette.Metal, "hood");

        // Fridge in the corner.
        Box(root, room.X1 - 0.45f, y + 0.95f, room.Z0 + 0.4f, 0.75f, 1.9f, 0.7f, _palette.Metal, "fridge");

        // Island (generated model) or a primitive island with stools.
        Vector3 islandPosition = new(room.Centre.X, y, room.Centre.Y + 0.3f);
        if (_models.Place("kitchen-island", root, islandPosition, 0f, MathF.Min(2.2f, room.Width * 0.45f), IslandYaw) is null)
        {
            Box(root, islandPosition.X, y + 0.45f, islandPosition.Z, 1.8f, 0.9f, 0.9f, _palette.DarkTimber, "island");
            Box(root, islandPosition.X, y + 0.92f, islandPosition.Z, 1.9f, 0.05f, 1.0f, _palette.Marble, "island-top");
        }

        // Pendant lights over the island.
        for (int i = -1; i <= 1; i++)
        {
            Box(root, room.Centre.X + (i * 0.6f), y + 2.3f, room.Centre.Y + 0.3f, 0.2f, 0.25f, 0.2f, _palette.LampGlow, "pendant").CastShadow = false;
        }

        // Dining set towards the front of the room when there is space.
        if (room.Depth > 5f)
        {
            float dz = room.Z1 - 1.3f;
            Box(root, room.Centre.X, y + 0.74f, dz, 1.6f, 0.05f, 0.9f, _palette.Timber, "dining-table");
            Box(root, room.Centre.X, y + 0.36f, dz, 0.1f, 0.72f, 0.6f, _palette.DarkMetal, "dining-table-leg");
            for (int i = -1; i <= 1; i += 2)
            {
                Chair(root, room.Centre.X + (i * 0.45f), y, dz - 0.75f, 0f, _palette.Timber);
                Chair(root, room.Centre.X + (i * 0.45f), y, dz + 0.75f, MathF.PI, _palette.Timber);
            }
        }
    }

    private void Bathroom(Node root, RoomSpec room, float y)
    {
        // Toilet.
        float tx = room.X0 + 0.45f;
        float tz = room.Z0 + 0.45f;
        Box(root, tx, y + 0.2f, tz + 0.1f, 0.38f, 0.4f, 0.5f, _palette.WhiteWall, "toilet-bowl");
        Box(root, tx, y + 0.62f, tz - 0.15f, 0.42f, 0.45f, 0.18f, _palette.WhiteWall, "toilet-tank");
        Box(root, tx, y + 0.41f, tz + 0.12f, 0.4f, 0.03f, 0.48f, _palette.Linen, "toilet-lid");

        // Floating vanity, basin, mirror.
        float vx = room.Centre.X + 0.2f;
        Box(root, vx, y + 0.65f, room.Z0 + 0.28f, 1.0f, 0.3f, 0.5f, _palette.Timber, "vanity");
        Box(root, vx, y + 0.83f, room.Z0 + 0.28f, 0.55f, 0.08f, 0.4f, _palette.WhiteWall, "basin");
        Box(root, vx, y + 0.95f, room.Z0 + 0.1f, 0.03f, 0.2f, 0.03f, _palette.Chrome, "basin-tap");
        Box(root, vx, y + 1.55f, room.Z0 + 0.09f, 0.9f, 0.8f, 0.02f, _palette.Chrome, "mirror");

        // Walk-in shower in the far corner.
        float sx = room.X1 - 0.6f;
        float sz = room.Z1 - 0.6f;
        Box(root, sx, y + 0.03f, sz, 1.0f, 0.06f, 1.0f, _palette.Ceramic, "shower-tray");
        Box(root, sx - 0.5f, y + 1.0f, sz, 0.02f, 2.0f, 1.0f, _palette.Glass, "shower-glass").CastShadow = false;
        Box(root, sx + 0.3f, y + 2.0f, sz, 0.25f, 0.02f, 0.25f, _palette.Chrome, "rain-shower");
        Box(root, room.X1 - 0.08f, y + 1.3f, room.Centre.Y - 0.3f, 0.06f, 0.5f, 0.5f, _palette.Linen, "towel");
    }

    private void Bedroom(Node root, RoomSpec room, float y, bool master)
    {
        float bedLength = master ? 2.15f : 2.0f;
        Vector3 bedPosition = new(room.Centre.X, y, room.Z0 + (bedLength * 0.5f) + 0.1f);
        if (_models.Place("bed", root, bedPosition, 0f, bedLength, BedYaw) is null)
        {
            float width = master ? 1.8f : 1.2f;
            Box(root, bedPosition.X, y + 0.25f, bedPosition.Z, width, 0.5f, 2.0f, _palette.DarkTimber, "bed-frame");
            Box(root, bedPosition.X, y + 0.55f, bedPosition.Z + 0.1f, width - 0.05f, 0.2f, 1.8f, _palette.Linen, "mattress");
            Box(root, bedPosition.X, y + 0.9f, room.Z0 + 0.15f, width + 0.1f, 1.0f, 0.1f, _palette.Fabric, "headboard");
        }

        // Nightstands with lamps either side of the bed.
        float offset = master ? 1.25f : 1.0f;
        for (int side = -1; side <= 1; side += 2)
        {
            float x = room.Centre.X + (side * offset);
            Box(root, x, y + 0.25f, room.Z0 + 0.35f, 0.45f, 0.5f, 0.4f, _palette.Timber, "nightstand");
            Box(root, x, y + 0.72f, room.Z0 + 0.35f, 0.22f, 0.35f, 0.22f, _palette.LampGlow, "bedside-lamp").CastShadow = false;
        }

        // Wardrobe on the side wall.
        Box(root, room.X1 - 0.33f, y + 1.1f, room.Centre.Y + 0.4f, 0.6f, 2.2f, MathF.Min(2.2f, room.Depth * 0.4f), master ? _palette.Timber : _palette.WhiteWall, "wardrobe");

        if (master)
        {
            Box(root, room.X0 + 0.35f, y + 0.75f, room.Z1 - 1.2f, 0.6f, 0.04f, 1.2f, _palette.Timber, "desk");
            Chair(root, room.X0 + 0.85f, y, room.Z1 - 1.2f, -MathF.PI / 2f, _palette.Fabric);
            Rug(root, room, y, new Vector2(2.4f, 1.6f), _palette.Linen);
        }

        Plant(root, room.X0 + 0.35f, y, room.Z0 + 0.35f, 0.9f);
    }

    private void Playroom(Node root, RoomSpec room, float y)
    {
        // Soft play mat in bright tiles.
        Material[] colours =
        [
            _assets.Scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.9f, 0.3f, 0.2f, 1f), 0f, 0.8f)),
            _assets.Scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.2f, 0.55f, 0.9f, 1f), 0f, 0.8f)),
            _assets.Scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.95f, 0.8f, 0.15f, 1f), 0f, 0.8f)),
            _assets.Scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.3f, 0.75f, 0.35f, 1f), 0f, 0.8f)),
        ];

        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                Node tile = _assets.AddFloor(root, new Vector3(room.Centre.X - 1.2f + (i * 0.8f), y + 0.015f, room.Centre.Y - 1.2f + (j * 0.8f)), new Vector2(0.78f, 0.78f), colours[(i + j) % 4], true, "play-mat");
                tile.ReceiveShadow = true;
            }
        }

        // Ball pit, toy chest, a small table with chairs and a teepee.
        Box(root, room.X0 + 0.9f, y + 0.2f, room.Z0 + 0.9f, 1.4f, 0.4f, 1.4f, colours[1], "ball-pit");
        for (int i = 0; i < 18; i++)
        {
            Node ball = _assets.Scene.AddMesh(_assets.Sphere(0.08f, 10), colours[i % 4], root, "ball");
            ball.Position = new Vector3(room.X0 + 0.4f + ((i % 6) * 0.2f), y + 0.42f, room.Z0 + 0.5f + ((i / 6) * 0.3f));
        }

        Box(root, room.X1 - 0.5f, y + 0.25f, room.Z0 + 0.4f, 0.8f, 0.5f, 0.5f, colours[2], "toy-chest");
        Box(root, room.Centre.X + 0.8f, y + 0.4f, room.Centre.Y + 0.8f, 0.8f, 0.04f, 0.8f, _palette.WhiteWall, "kids-table");
        for (int i = 0; i < 2; i++)
        {
            Box(root, room.Centre.X + 0.3f + (i * 1.0f), y + 0.2f, room.Centre.Y + 0.8f, 0.3f, 0.4f, 0.3f, colours[i], "kids-stool");
        }

        Node teepee = _assets.Scene.AddMesh(_assets.Cone(0.8f, 1.8f, 6), _palette.Linen, root, "teepee");
        teepee.Position = new Vector3(room.X1 - 0.9f, y + 0.9f, room.Z1 - 0.9f);

        // Shelf of stacking blocks and a rocking horse.
        Box(root, room.X0 + 0.2f, y + 0.9f, room.Z1 - 1.2f, 0.3f, 1.8f, 1.2f, _palette.WhiteWall, "toy-shelf");
        for (int i = 0; i < 9; i++)
        {
            Box(root, room.X0 + 0.25f, y + 0.25f + ((i / 3) * 0.55f), room.Z1 - 1.6f + ((i % 3) * 0.35f), 0.2f, 0.2f, 0.2f, colours[i % 4], "block");
        }

        Box(root, room.Centre.X - 0.9f, y + 0.55f, room.Centre.Y + 0.9f, 0.25f, 0.35f, 0.8f, colours[0], "rocking-horse");
        Node rocker = _assets.Scene.AddMesh(_assets.Cylinder(0.45f, 0.05f, 16), _palette.Timber, root, "rocker");
        rocker.Position = new Vector3(room.Centre.X - 0.9f, y + 0.2f, room.Centre.Y + 0.9f);
        rocker.EulerAngles = new Vector3(0f, 0f, MathF.PI / 2f);
        rocker.Scale = new Vector3(1f, 1f, 0.4f);
    }

    private void Hall(Node root, RoomSpec room, float y)
    {
        Box(root, room.X0 + 0.25f, y + 0.8f, room.Centre.Y, 0.35f, 0.05f, 1.2f, _palette.DarkTimber, "console");
        Box(root, room.X0 + 0.1f, y + 1.6f, room.Centre.Y, 0.02f, 0.9f, 0.7f, _palette.Chrome, "hall-mirror");
        Plant(root, room.X0 + 0.35f, y, room.Z1 - 0.4f, 1.1f);
    }
}
