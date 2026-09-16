using System.Numerics;
using Avalonia.Media;
using ThreeNet;

namespace HomeComplexCad.World;

/// <summary>
/// Lays out "Griya Nusantara Residence": the entrance gate, a tree lined
/// boulevard, two residential streets with twelve houses (three furnished show
/// units), a row of shops, the clubhouse with its swimming pool, a park with a
/// playground and pond, a sports court, street lights and signage.
/// </summary>
public sealed class EstateBuilder
{
    public const string EstateName = "Griya Nusantara Residence";

    private const float StreetA = 30f;
    private const float StreetB = -32f;
    private const float BoulevardHalfWidth = 7f;
    private const float StreetHalfWidth = 4f;

    private readonly Assets _assets;
    private readonly Palette _palette;
    private readonly ModelLibrary _models;
    private readonly InteriorBuilder _interior;
    private readonly HouseBuilder _houses;
    private readonly Scene _scene;

    public EstateBuilder(Scene scene)
    {
        _scene = scene;
        _assets = new Assets(scene);
        _palette = new Palette(scene);
        _models = new ModelLibrary(scene);
        _interior = new InteriorBuilder(_assets, _palette, _models);
        _houses = new HouseBuilder(_assets, _palette, _models, _interior);
    }

    public Palette Palette => _palette;

    public List<PropertyInfo> Properties { get; } = [];

    /// <summary>Node id of every building root, mapped to its property record.</summary>
    public Dictionary<uint, PropertyInfo> BuildingRoots { get; } = [];

    public List<Location> Locations { get; } = [];

    public List<Node> StreetLights { get; } = [];

    public List<Node> InteriorLights => _houses.InteriorLights;

    public List<Node> OutdoorLights => _houses.OutdoorLights;

    /// <summary>Shop signs and other emissive materials that brighten at night.</summary>
    public List<Material> NightEmissives { get; } = [];

    public void Build()
    {
        BuildGround();
        BuildRoads();
        BuildGate();
        BuildHouses();
        BuildShops();
        BuildClubhouse();
        BuildPark();
        BuildSportsCourt();
        BuildStreetFurniture();
        AddNeighbourhoodLocations();
    }

    // ---------------------------------------------------------------- ground

    private void BuildGround()
    {
        Node ground = _assets.AddFloor(null, new Vector3(0f, -0.01f, 0f), new Vector2(400f, 400f), _palette.Lawn, true, "ground");
        ground.ReceiveShadow = true;
    }

    private void Road(Vector3 centre, Vector2 size, bool alongX)
    {
        Node road = _assets.AddSlab(null, centre, size, 0.06f, 0f, _palette.Asphalt, "road");
        road.CastShadow = false;

        // Dashed centre line.
        float length = alongX ? size.X : size.Y;
        for (float t = -length * 0.5f + 2f; t < length * 0.5f - 2f; t += 6f)
        {
            Vector3 position = centre + (alongX ? new Vector3(t, 0.065f, 0f) : new Vector3(0f, 0.065f, t));
            Node dash = _assets.AddBox(null, position, alongX ? new Vector3(3f, 0.01f, 0.15f) : new Vector3(0.15f, 0.01f, 3f), _palette.RoadLine, "lane-marking");
            dash.CastShadow = false;
        }

        // Sidewalks with kerbs on both sides.
        for (int side = -1; side <= 1; side += 2)
        {
            float half = (alongX ? size.Y : size.X) * 0.5f;
            Vector3 offset = alongX ? new Vector3(0f, 0f, side * (half + 1.1f)) : new Vector3(side * (half + 1.1f), 0f, 0f);
            Vector2 walkSize = alongX ? new Vector2(size.X, 2.2f) : new Vector2(2.2f, size.Y);
            _assets.AddSlab(null, centre + offset, walkSize, 0.15f, 0f, _palette.Paving, "sidewalk").CastShadow = false;
            Vector3 kerbOffset = alongX ? new Vector3(0f, 0.09f, side * (half + 0.05f)) : new Vector3(side * (half + 0.05f), 0.09f, 0f);
            _assets.AddBox(null, centre + kerbOffset, alongX ? new Vector3(size.X, 0.18f, 0.12f) : new Vector3(0.12f, 0.18f, size.Y), _palette.Concrete, "kerb").CastShadow = false;
        }
    }

    private void BuildRoads()
    {
        // Boulevard: two carriageways either side of a planted median.
        Road(new Vector3(-4f, 0f, 5f), new Vector2(6f, 170f), alongX: false);
        Road(new Vector3(4f, 0f, 5f), new Vector2(6f, 170f), alongX: false);
        _assets.AddSlab(null, new Vector3(0f, 0f, 5f), new Vector2(1.6f, 170f), 0.25f, 0f, _palette.Lawn, "median").CastShadow = false;
        for (float z = -70f; z <= 80f; z += 12f)
        {
            _interior.Tree(null, new Vector3(0f, 0.25f, z), 0.8f);
        }

        Road(new Vector3(0f, 0f, StreetA), new Vector2(130f, StreetHalfWidth * 2f), alongX: true);
        Road(new Vector3(0f, 0f, StreetB), new Vector2(130f, StreetHalfWidth * 2f), alongX: true);
    }

    // ------------------------------------------------------------------ gate

    private void BuildGate()
    {
        const float z = 88f;
        foreach (float x in new[] { -11f, 11f })
        {
            _assets.AddBox(null, new Vector3(x, 2.5f, z), new Vector3(2.2f, 5f, 2.2f), _palette.Brick, "gate-tower");
            _assets.AddBox(null, new Vector3(x, 5.1f, z), new Vector3(2.6f, 0.25f, 2.6f), _palette.Concrete, "gate-cap");
            Node lamp = _assets.AddBox(null, new Vector3(x, 5.5f, z), new Vector3(0.5f, 0.6f, 0.5f), _palette.LampGlow, "gate-lamp");
            lamp.CastShadow = false;
        }

        Texture sign = TextTextures.Create(_scene, EstateName, Color.FromRgb(24, 30, 26), Color.FromRgb(226, 196, 120), 1024, 192, 92, "Hunian asri · aman · terpadu");
        Material signMaterial = _scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 0.2f, 0.5f) with
        {
            BaseColorMap = sign,
            EmissiveMap = sign,
            Emissive = new Vector3(0.9f, 0.8f, 0.55f),
            EmissiveIntensity = 0.15f,
            CullMode = CullMode.None,
        });
        NightEmissives.Add(signMaterial);
        _assets.AddBox(null, new Vector3(0f, 5.6f, z), new Vector3(20f, 1.2f, 0.6f), _palette.DarkTimber, "gate-beam");
        Node board = _scene.AddMesh(_assets.Plane(14f, 2.6f), signMaterial, null, "gate-sign");
        board.Position = new Vector3(0f, 5.6f, z + 0.32f);

        // Guard post by the exit lane.
        _assets.AddSlab(null, new Vector3(-9f, 0f, z - 6f), new Vector2(3f, 3f), 2.8f, 0f, _palette.WhiteWall, "guard-post");
        _assets.AddSlab(null, new Vector3(-9f, 2.8f, z - 6f), new Vector2(3.8f, 3.8f), 0.25f, 0f, _palette.DarkMetal, "guard-roof");
        _assets.AddBox(null, new Vector3(-7.45f, 1.5f, z - 6f), new Vector3(0.04f, 1.2f, 2.2f), _palette.Glass, "guard-window");
        _assets.AddBox(null, new Vector3(-4f, 1f, z - 4f), new Vector3(6f, 0.1f, 0.1f), _palette.RoadLine, "boom-barrier");

        Properties.Add(new PropertyInfo
        {
            Id = "gate",
            Name = "Gerbang Utama & Pos Keamanan",
            Type = "Fasilitas",
            Status = PropertyStatus.Facility,
            LandArea = 420,
            BuildingArea = 9,
            LotSize = new Vector2(30, 14),
            Description = "Akses satu pintu dengan pos keamanan 24 jam, CCTV dan palang otomatis berbasis kartu akses penghuni.",
            Features = ["Keamanan 24 jam", "CCTV terintegrasi", "Kartu akses (RFID)", "Taman gerbang"],
            Position = new Vector3(0f, 0f, z),
        });
    }

    // ---------------------------------------------------------------- houses

    private static List<RoomSpec> SingleStoreyRooms() =>
    [
        new("Ruang Tamu", RoomKind.Living, -6f, 0.5f, 0f, 5f),
        new("Dapur & Ruang Makan", RoomKind.Kitchen, -6f, -5f, 0f, 0.5f),
        new("Kamar Tidur Utama", RoomKind.MasterBedroom, 0f, 1f, 6f, 5f),
        new("Kamar Mandi", RoomKind.Bathroom, 0f, -1.5f, 3f, 1f),
        new("Kamar Tamu", RoomKind.GuestRoom, 0f, -5f, 6f, -1.5f),
        new("Ruang Cuci", RoomKind.Hall, 3f, -1.5f, 6f, 1f),
    ];

    private static List<RoomSpec> TwoStoreyRooms() =>
    [
        new("Ruang Tamu", RoomKind.Living, -6.5f, 1f, 1f, 5.5f),
        new("Tangga & Hall", RoomKind.Hall, 1f, 1f, 3.5f, 5.5f),
        new("Kamar Tamu", RoomKind.GuestRoom, 3.5f, 1f, 6.5f, 5.5f),
        new("Dapur & Ruang Makan", RoomKind.Kitchen, -6.5f, -5.5f, 1f, 1f),
        new("Kamar Mandi Bawah", RoomKind.Bathroom, 1f, -5.5f, 6.5f, 1f),
        new("Ruang Keluarga", RoomKind.Family, -6.5f, 0f, 1f, 5.5f, 1),
        new("Hall Atas", RoomKind.Hall, 1f, 1f, 3.5f, 5.5f, 1),
        new("Ruang Bermain Anak", RoomKind.Playroom, 3.5f, 0f, 6.5f, 5.5f, 1),
        new("Kamar Tidur Utama", RoomKind.MasterBedroom, -6.5f, -5.5f, 1f, 0f, 1),
        new("Kamar Mandi Atas", RoomKind.Bathroom, 1f, -5.5f, 3.5f, 1f, 1),
        new("Kamar Anak", RoomKind.GuestRoom, 3.5f, -5.5f, 6.5f, 0f, 1),
    ];

    private void BuildHouses()
    {
        float[] xs = [-50f, -34f, -18f, 18f, 34f, 50f];
        string[] names = ["Anggrek", "Bougenville", "Cempaka", "Dahlia", "Edelweis", "Flamboyan"];
        int index = 0;

        foreach ((float rowZ, float yaw, string row) in new[] { (StreetA - 16f, 0f, "B"), (StreetA + 16f, MathF.PI, "A") })
        {
            for (int i = 0; i < xs.Length; i++)
            {
                index++;
                float x = xs[i];
                bool south = row == "B";
                // The three furnished show units sit on the east side of the south row.
                bool showA = south && i == 3;
                bool showB = south && i == 4;
                bool showC = south && i == 5;
                bool twoStorey = showB || showC || (!south && i % 2 == 0);
                int style = (i + (south ? 0 : 1)) % 3;
                string block = $"{row}{i + 1}";

                PropertyInfo info = MakeInfo(block, names[i], twoStorey, showA || showB || showC, showC, style, index);
                HousePlan plan = new()
                {
                    Info = info,
                    Width = twoStorey ? 13f : 12f,
                    Depth = twoStorey ? 11f : 10f,
                    Floors = twoStorey ? 2 : 1,
                    Roof = style == 2 ? RoofStyle.Flat : RoofStyle.Gable,
                    Rooms = twoStorey ? TwoStoreyRooms() : SingleStoreyRooms(),
                    Furnished = showA || showB || showC,
                    Pool = showC,
                    Style = style,
                };

                // The house sits towards the back of its lot, facing the street.
                float setback = 4.5f;
                Vector3 lotCentre = new(x, 0f, rowZ);
                Vector3 houseOffset = Vector3.Transform(new Vector3(0f, 0f, -setback + 2f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
                BuiltHouse house = _houses.Build(plan, lotCentre + houseOffset, yaw);

                info.Position = lotCentre;
                Properties.Add(info);
                BuildingRoots[house.Root.Id] = info;

                // Lot boundary: low hedge along the sides.
                foreach (float side in new[] { -7.4f, 7.4f })
                {
                    Node hedge = _assets.AddBox(null, new Vector3(x + side, 0.35f, rowZ), new Vector3(0.4f, 0.7f, 20f), _palette.Foliage, "lot-hedge");
                    hedge.CastShadow = false;
                }

                string group = plan.Furnished ? $"Rumah Contoh {info.Name}" : "Rumah Lainnya";
                Vector3 front = Vector3.Transform(new Vector3(0f, 0f, 16f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
                Locations.Add(new Location(group, plan.Furnished ? "Tampak depan" : $"Blok {block} - {info.Name}",
                    lotCentre + front + new Vector3(3f, 3.2f, 0f), lotCentre + new Vector3(0f, 2.5f, 0f), false, info.Id, "🏠"));

                if (!plan.Furnished)
                {
                    continue;
                }

                foreach ((RoomSpec room, Vector3 eye, Vector3 target) in house.RoomViews.Where(v => v.Room.Kind != RoomKind.Hall))
                {
                    Matrix4x4 toWorld = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(lotCentre + houseOffset);
                    string icon = room.Kind switch
                    {
                        RoomKind.Kitchen => "🍳",
                        RoomKind.Bathroom => "🛁",
                        RoomKind.MasterBedroom or RoomKind.GuestRoom => "🛏",
                        RoomKind.Playroom => "🧸",
                        RoomKind.Family => "📺",
                        _ => "🛋",
                    };
                    Locations.Add(new Location(group, room.Floor > 0 ? $"{room.Name} (lt. 2)" : room.Name,
                        Vector3.Transform(eye, toWorld), Vector3.Transform(target, toWorld), true, info.Id, icon));
                }

                if (plan.Pool)
                {
                    Vector3 poolView = Vector3.Transform(new Vector3(-3f, 2.2f, -13f), Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(lotCentre + houseOffset));
                    Vector3 poolTarget = Vector3.Transform(new Vector3(2f, 0f, -9.5f), Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(lotCentre + houseOffset));
                    Locations.Add(new Location(group, "Kolam Renang Pribadi", poolView, poolTarget, false, info.Id, "🏊"));
                }
            }
        }
    }

    private static PropertyInfo MakeInfo(string block, string name, bool twoStorey, bool show, bool pool, int style, int index)
    {
        PropertyStatus status = show
            ? PropertyStatus.Available
            : (index % 3) switch { 0 => PropertyStatus.Sold, 1 => PropertyStatus.Reserved, _ => PropertyStatus.Available };
        float land = twoStorey ? (pool ? 300f : 240f) : 180f;
        float building = twoStorey ? (pool ? 200f : 165f) : 90f;
        string styleName = style switch { 0 => "Klasik Tropis", 1 => "Skandinavia Hangat", _ => "Modern Minimalis" };
        List<string> features =
        [
            "Carport 1 mobil (kanopi kaca)",
            "Taman depan & belakang",
            "Smart door lock",
            "Water heater",
            "Instalasi internet fiber",
        ];
        if (twoStorey)
        {
            features.Add("Balkon & void ruang tamu");
        }

        if (pool)
        {
            features.Add("Kolam renang pribadi 7 x 3.5 m");
            features.Add("Panel surya 2 kWp");
        }

        return new PropertyInfo
        {
            Id = $"house-{block}",
            Name = name,
            Block = block,
            Type = twoStorey ? $"Tipe {building:0}/{land:0} · 2 lantai" : $"Tipe {building:0}/{land:0} · 1 lantai",
            Status = status,
            LandArea = land,
            BuildingArea = building,
            LotSize = new Vector2(15f, land / 15f),
            Floors = twoStorey ? 2 : 1,
            Bedrooms = twoStorey ? 4 : 2,
            Bathrooms = twoStorey ? 3 : 1,
            Carports = 1,
            Facing = block.StartsWith('A') ? "Selatan" : "Utara",
            PowerVa = twoStorey ? 4400 : 2200,
            PriceRupiah = (long)(((land * 6_500_000) + (building * 5_200_000)) * (pool ? 1.25 : 1.0)),
            YearBuilt = 2026,
            Roof = style == 2 ? "Dak beton datar, waterproofing membran" : "Rangka baja ringan, genteng keramik",
            Description = $"Rumah gaya {styleName.ToLowerInvariant()} di blok {block}. " +
                (show ? "Unit contoh yang sudah dilengkapi furnitur - silakan jelajahi interiornya." : "Denah sama dengan unit contoh tipe yang sesuai."),
            Features = features,
        };
    }

    // ----------------------------------------------------------------- shops

    private void BuildShops()
    {
        (string Name, string Tagline, Color Colour)[] shops =
        [
            ("Nusa Mart", "Minimarket 24 jam", Color.FromRgb(200, 40, 40)),
            ("Kopi Senja", "Kafe & roastery", Color.FromRgb(120, 72, 40)),
            ("Apotek Sehat", "Apotek & klinik", Color.FromRgb(20, 130, 80)),
            ("Laundry Kilat", "Cuci & setrika", Color.FromRgb(30, 90, 170)),
        ];

        const float z = StreetB - 12f;
        for (int i = 0; i < shops.Length; i++)
        {
            float x = -56f + (i * 11f);
            Node shop = _scene.CreateNode(null, $"shop-{i}");
            shop.Position = new Vector3(x, 0f, z);

            _assets.AddSlab(shop, new Vector3(0f, 0f, -1.5f), new Vector2(10.6f, 10f), 4.2f, 0f, _palette.WhiteWall, "shop-body");
            _assets.AddSlab(shop, new Vector3(0f, 0f, -1.5f), new Vector2(11f, 10.4f), 0.3f, 4.2f, _palette.DarkMetal, "shop-roof");
            // Glass storefront and door.
            _assets.AddBox(shop, new Vector3(0f, 1.7f, 3.52f), new Vector3(8.5f, 3f, 0.05f), _palette.WindowGlow, "storefront");
            _assets.AddBox(shop, new Vector3(0f, 3.35f, 4.1f), new Vector3(10.6f, 0.12f, 1.4f), _palette.DarkMetal, "awning");

            Texture signTexture = TextTextures.Create(_scene, shops[i].Name, shops[i].Colour, Avalonia.Media.Colors.White, 768, 160, 84, shops[i].Tagline);
            Material sign = _scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 0.1f, 0.5f) with
            {
                BaseColorMap = signTexture,
                EmissiveMap = signTexture,
                Emissive = Vector3.One,
                EmissiveIntensity = 0.2f,
            });
            NightEmissives.Add(sign);
            Node board = _scene.AddMesh(_assets.Plane(8f, 1.6f), sign, shop, "shop-sign");
            board.Position = new Vector3(0f, 4.0f, 3.62f);

            // Interior shelves seen through the glass.
            for (int s = 0; s < 3; s++)
            {
                _assets.AddBox(shop, new Vector3(-3f + (s * 3f), 0.9f, -1f), new Vector3(2f, 1.8f, 0.6f), _palette.Timber, "shelf");
            }

            PropertyInfo info = new()
            {
                Id = $"shop-{i}",
                Name = $"Ruko {shops[i].Name}",
                Type = "Ruko 1 lantai · 11 x 10 m",
                Block = $"R{i + 1}",
                Status = i == 3 ? PropertyStatus.Available : PropertyStatus.Sold,
                LandArea = 132,
                BuildingArea = 106,
                LotSize = new Vector2(11, 12),
                Bathrooms = 1,
                PowerVa = 7700,
                PriceRupiah = i == 3 ? 1_650_000_000 : 0,
                Certificate = "SHGB (Hak Guna Bangunan)",
                Facing = "Utara",
                Structure = "Beton bertulang, fasad kaca tempered",
                Roof = "Dak beton, kanopi baja",
                Flooring = "Homogeneous tile 80x80",
                Description = $"{shops[i].Tagline}. Unit komersial di koridor pertokoan dengan parkir depan.",
                Features = ["Parkir 4 mobil", "Fasad kaca penuh", "Signage menyala malam hari", "Akses langsung dari jalan utama"],
                Position = new Vector3(x, 0f, z),
            };
            Properties.Add(info);
            BuildingRoots[shop.Id] = info;
        }

        // Parking bays with a couple of cars.
        for (int i = 0; i < 4; i++)
        {
            Vector3 bay = new(-54f + (i * 11f), 0.07f, StreetB - 6.2f);
            _assets.AddBox(null, bay + new Vector3(-1.4f, 0f, 0f), new Vector3(0.1f, 0.01f, 4.5f), _palette.RoadLine, "bay-line").CastShadow = false;
            if (i % 2 == 0)
            {
                _models.Place("car", null, bay, MathF.PI, 4.4f);
            }
        }

        _assets.AddSlab(null, new Vector3(-39f, 0f, StreetB - 6.2f), new Vector2(46f, 5f), 0.05f, 0f, _palette.Asphalt, "parking").CastShadow = false;
        Locations.Add(new Location("Lingkungan", "Area Pertokoan", new Vector3(-30f, 4.5f, StreetB + 6f), new Vector3(-40f, 2f, StreetB - 12f), false, "shop-0", "🏪"));
    }

    // ------------------------------------------------------------ clubhouse

    private void BuildClubhouse()
    {
        Vector3 centre = new(36f, 0f, StreetB - 22f);
        Node club = _scene.CreateNode(null, "clubhouse");
        club.Position = centre;

        _assets.AddSlab(club, new Vector3(0f, 0f, 8f), new Vector2(22f, 8f), 4.5f, 0f, _palette.Stucco, "club-body");
        _assets.AddSlab(club, new Vector3(0f, 0f, 8f), new Vector2(24f, 10f), 0.35f, 4.5f, _palette.DarkTimber, "club-roof");
        _assets.AddBox(club, new Vector3(0f, 2.0f, 3.95f), new Vector3(18f, 3.6f, 0.05f), _palette.WindowGlow, "club-glass");

        Texture signTexture = TextTextures.Create(_scene, "Clubhouse", Color.FromRgb(18, 40, 56), Color.FromRgb(240, 230, 200), 512, 128, 72);
        Material sign = _scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 0.1f, 0.5f) with
        {
            BaseColorMap = signTexture,
            EmissiveMap = signTexture,
            Emissive = Vector3.One,
            EmissiveIntensity = 0.2f,
        });
        NightEmissives.Add(sign);
        Node board = _scene.AddMesh(_assets.Plane(5f, 1.25f), sign, club, "club-sign");
        board.Position = new Vector3(0f, 4.1f, 12.06f);

        // Pool deck on the side facing the street.
        _houses.BuildPool(club, new Vector3(-2f, 0f, -4f), new Vector2(18f, 8f));
        _houses.BuildPool(club, new Vector3(10f, 0f, -4f), new Vector2(3.5f, 3.5f));
        for (int i = 0; i < 4; i++)
        {
            Vector3 umbrella = new(-10f + (i * 5f), 0f, -10.5f);
            _assets.AddBox(club, umbrella + new Vector3(0f, 1.2f, 0f), new Vector3(0.06f, 2.4f, 0.06f), _palette.Metal, "umbrella-pole");
            Node canopy = _scene.AddMesh(_assets.Cone(1.4f, 0.5f, 8), _palette.Linen, club, "umbrella");
            canopy.Position = umbrella + new Vector3(0f, 2.5f, 0f);
        }

        PropertyInfo info = new()
        {
            Id = "clubhouse",
            Name = "Clubhouse & Kolam Renang",
            Type = "Fasilitas bersama",
            Status = PropertyStatus.Facility,
            LandArea = 1150,
            BuildingArea = 176,
            LotSize = new Vector2(32, 36),
            Bathrooms = 4,
            PowerVa = 23000,
            Description = "Kolam renang dewasa 18 x 8 m dan kolam anak, ruang serbaguna, gym kecil dan kafe tepi kolam.",
            Features = ["Kolam dewasa 18 x 8 m", "Kolam anak", "Gym & ruang serbaguna", "Shower & loker", "Lifeguard akhir pekan"],
            Position = centre,
        };
        Properties.Add(info);
        BuildingRoots[club.Id] = info;
        Locations.Add(new Location("Lingkungan", "Kolam Renang Clubhouse", centre + new Vector3(-14f, 3.5f, -14f), centre + new Vector3(0f, 0f, -4f), false, "clubhouse", "🏊"));
    }

    // ----------------------------------------------------------------- park

    private void BuildPark()
    {
        Vector3 centre = new(-34f, 0f, -2f);
        Node park = _scene.CreateNode(null, "park");
        park.Position = centre;

        // Paths crossing the park.
        _assets.AddSlab(park, Vector3.Zero, new Vector2(40f, 2.2f), 0.05f, 0f, _palette.Paving, "park-path").CastShadow = false;
        _assets.AddSlab(park, Vector3.Zero, new Vector2(2.2f, 24f), 0.05f, 0f, _palette.Paving, "park-path").CastShadow = false;

        // Pond with a small bridge.
        Node pond = _assets.AddFloor(park, new Vector3(-11f, 0.04f, -6f), new Vector2(9f, 6f), _palette.Water, true, "pond");
        pond.ReceiveShadow = false;
        _assets.AddBox(park, new Vector3(-11f, 0.25f, -6f), new Vector3(1.6f, 0.12f, 6.4f), _palette.Timber, "bridge");

        // Gazebo.
        Vector3 gazebo = new(11f, 0f, 6f);
        _assets.AddSlab(park, gazebo, new Vector2(5f, 5f), 0.3f, 0f, _palette.Timber, "gazebo-deck");
        foreach ((float gx, float gz) in new[] { (-2f, -2f), (2f, -2f), (-2f, 2f), (2f, 2f) })
        {
            _assets.AddBox(park, gazebo + new Vector3(gx, 1.5f, gz), new Vector3(0.15f, 2.6f, 0.15f), _palette.DarkTimber, "gazebo-post");
        }

        Node roof = _scene.AddMesh(_assets.Cone(3.8f, 1.8f, 4), _palette.RoofTile, park, "gazebo-roof");
        roof.Position = gazebo + new Vector3(0f, 3.7f, 0f);
        roof.EulerAngles = new Vector3(0f, MathF.PI / 4f, 0f);

        // Playground: slide, swings, see-saw and sandbox.
        Vector3 play = new(-10f, 0f, 7f);
        _assets.AddSlab(park, play, new Vector2(10f, 7f), 0.06f, 0f, _palette.Terracotta, "play-surface").CastShadow = false;
        _assets.AddBox(park, play + new Vector3(-3f, 1.1f, 0f), new Vector3(1.2f, 0.1f, 1.2f), _palette.SignBlue, "slide-platform");
        foreach ((float px, float pz) in new[] { (-3.5f, -0.5f), (-2.5f, -0.5f), (-3.5f, 0.5f), (-2.5f, 0.5f) })
        {
            _assets.AddBox(park, play + new Vector3(px, 0.55f, pz), new Vector3(0.08f, 1.1f, 0.08f), _palette.Metal, "slide-post");
        }

        Node slide = _assets.AddBox(park, play + new Vector3(-1.5f, 0.6f, 0f), new Vector3(2.4f, 0.06f, 0.6f), _palette.FabricWarm, "slide");
        slide.EulerAngles = new Vector3(0f, 0f, -0.45f);
        _assets.AddBox(park, play + new Vector3(2.5f, 2.2f, 0f), new Vector3(3.4f, 0.1f, 0.1f), _palette.Metal, "swing-bar");
        foreach (float sx in new[] { 1.3f, 3.7f })
        {
            Node leg = _assets.AddBox(park, play + new Vector3(sx, 1.1f, 0f), new Vector3(0.1f, 2.3f, 0.1f), _palette.Metal, "swing-leg");
            leg.EulerAngles = new Vector3(0f, 0f, 0f);
        }

        foreach (float seat in new[] { 1.9f, 3.1f })
        {
            _assets.AddBox(park, play + new Vector3(seat, 0.5f, 0f), new Vector3(0.5f, 0.05f, 0.25f), _palette.FabricWarm, "swing-seat");
            _assets.AddBox(park, play + new Vector3(seat, 1.35f, 0f), new Vector3(0.02f, 1.7f, 0.02f), _palette.Chrome, "swing-chain");
        }

        _assets.AddSlab(park, play + new Vector3(0f, 0f, 2.5f), new Vector2(3f, 1.8f), 0.2f, 0f, _palette.Paving, "sandbox");

        // Benches and trees.
        for (int i = 0; i < 4; i++)
        {
            Vector3 bench = new(-16f + (i * 9f), 0f, -1.8f);
            _assets.AddBox(park, bench + new Vector3(0f, 0.45f, 0f), new Vector3(1.8f, 0.08f, 0.45f), _palette.Timber, "bench-seat");
            _assets.AddBox(park, bench + new Vector3(0f, 0.75f, -0.22f), new Vector3(1.8f, 0.45f, 0.06f), _palette.Timber, "bench-back");
            _assets.AddBox(park, bench + new Vector3(0f, 0.22f, 0f), new Vector3(1.6f, 0.44f, 0.3f), _palette.DarkMetal, "bench-frame");
        }

        Random random = new(99);
        for (int i = 0; i < 16; i++)
        {
            Vector3 position = new(-18f + ((float)random.NextDouble() * 36f), 0f, -11f + ((float)random.NextDouble() * 22f));
            if (MathF.Abs(position.X) < 2.5f || MathF.Abs(position.Z) < 2.5f || (position.X < -5f && position.Z < -1f && position.X > -16f))
            {
                continue;
            }

            _interior.Tree(park, position, 0.8f + ((float)random.NextDouble() * 0.5f), detailed: true);
        }

        PropertyInfo info = new()
        {
            Id = "park",
            Name = "Taman Nusantara",
            Type = "Ruang terbuka hijau",
            Status = PropertyStatus.Facility,
            LandArea = 1000,
            LotSize = new Vector2(40, 25),
            Description = "Taman lingkungan dengan danau kecil, gazebo, jogging track dan taman bermain anak.",
            Features = ["Danau & jembatan kayu", "Gazebo", "Playground: perosotan, ayunan, bak pasir", "Jogging track", "Bangku taman"],
            Position = centre,
        };
        Properties.Add(info);
        BuildingRoots[park.Id] = info;
        Locations.Add(new Location("Lingkungan", "Taman & Danau", centre + new Vector3(8f, 3f, -14f), centre + new Vector3(-6f, 0f, 2f), false, "park", "🌳"));
        Locations.Add(new Location("Lingkungan", "Taman Bermain Anak", centre + new Vector3(-4f, 2.4f, 14f), centre + new Vector3(-10f, 0.8f, 7f), false, "park", "🛝"));
    }

    private void BuildSportsCourt()
    {
        Vector3 centre = new(34f, 0f, -4f);
        Node court = _scene.CreateNode(null, "sports-court");
        court.Position = centre;
        Material courtSurface = _scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.12f, 0.36f, 0.30f, 1f), 0f, 0.7f));
        _assets.AddSlab(court, Vector3.Zero, new Vector2(28f, 15f), 0.08f, 0f, courtSurface, "court").CastShadow = false;
        foreach ((Vector3 offset, Vector3 size) in new[]
        {
            (new Vector3(0f, 0.085f, 7.2f), new Vector3(28f, 0.01f, 0.1f)),
            (new Vector3(0f, 0.085f, -7.2f), new Vector3(28f, 0.01f, 0.1f)),
            (new Vector3(13.9f, 0.085f, 0f), new Vector3(0.1f, 0.01f, 14.4f)),
            (new Vector3(-13.9f, 0.085f, 0f), new Vector3(0.1f, 0.01f, 14.4f)),
            (new Vector3(0f, 0.085f, 0f), new Vector3(0.1f, 0.01f, 14.4f)),
        })
        {
            _assets.AddBox(court, offset, size, _palette.RoadLine, "court-line").CastShadow = false;
        }

        foreach (float side in new[] { -1f, 1f })
        {
            _assets.AddBox(court, new Vector3(side * 12.8f, 1.6f, 0f), new Vector3(0.15f, 3.2f, 0.15f), _palette.DarkMetal, "hoop-pole");
            _assets.AddBox(court, new Vector3(side * 12.5f, 3.2f, 0f), new Vector3(0.05f, 1.05f, 1.8f), _palette.Glass, "backboard");
            Node ring = _scene.AddMesh(_scene.CreateTorusGeometry(0.23f, 0.02f, 8, 20), _palette.FabricWarm, court, "hoop");
            ring.Position = new Vector3(side * 12.1f, 3.05f, 0f);
            ring.EulerAngles = new Vector3(MathF.PI / 2f, 0f, 0f);
        }

        PropertyInfo info = new()
        {
            Id = "court",
            Name = "Lapangan Olahraga",
            Type = "Fasilitas bersama",
            Status = PropertyStatus.Facility,
            LandArea = 520,
            LotSize = new Vector2(32, 16),
            Description = "Lapangan serbaguna untuk basket dan futsal dengan lampu sorot malam hari.",
            Features = ["Lapangan basket", "Bisa untuk futsal & badminton", "Tribun kecil"],
            Position = centre,
        };
        Properties.Add(info);
        BuildingRoots[court.Id] = info;
    }

    // --------------------------------------------------------- street items

    private void StreetLamp(Vector3 position, float yaw)
    {
        Node lamp = _scene.CreateNode(null, "street-lamp");
        lamp.Position = position;
        lamp.EulerAngles = new Vector3(0f, yaw, 0f);
        if (_models.Place("street-lamp", lamp, Vector3.Zero, 0f, 6.2f, fitHeight: true) is null)
        {
            Node pole = _scene.AddMesh(_assets.Cylinder(0.08f, 6f, 8), _palette.DarkMetal, lamp, "lamp-pole");
            pole.Position = new Vector3(0f, 3f, 0f);
            _assets.AddBox(lamp, new Vector3(0f, 6f, 0.6f), new Vector3(0.08f, 0.08f, 1.3f), _palette.DarkMetal, "lamp-arm");
        }

        // Emissive head so the lamp visibly glows at night, whatever the model looks like.
        _assets.AddBox(lamp, new Vector3(0f, 5.92f, 1.2f), new Vector3(0.3f, 0.06f, 0.5f), _palette.LampGlow, "lamp-head").CastShadow = false;

        Node light = _scene.AddLight(
            Light.Spot(new Vector3(1f, 0.85f, 0.6f), 160f, range: 22f, innerAngle: 0.5f, outerAngle: 0.95f) with { Enabled = false },
            lamp,
            "street-light");
        light.Position = new Vector3(0f, 5.8f, 1.2f);
        light.EulerAngles = new Vector3(-MathF.PI / 2f, 0f, 0f);
        StreetLights.Add(light);
    }

    private void StreetSign(Vector3 position, string first, string second, float yaw)
    {
        Node sign = _scene.CreateNode(null, "street-sign");
        sign.Position = position;
        sign.EulerAngles = new Vector3(0f, yaw, 0f);
        Node pole = _scene.AddMesh(_assets.Cylinder(0.05f, 3f, 8), _palette.Metal, sign, "sign-pole");
        pole.Position = new Vector3(0f, 1.5f, 0f);

        foreach ((string text, float height, float turn) in new[] { (first, 2.75f, 0f), (second, 2.35f, MathF.PI / 2f) })
        {
            Texture texture = TextTextures.Create(_scene, text, Color.FromRgb(18, 96, 52), Avalonia.Media.Colors.White, 512, 110, 60);
            Material material = _scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 0.2f, 0.4f) with
            {
                BaseColorMap = texture,
                CullMode = CullMode.None,
            });
            Node board = _scene.AddMesh(_assets.Plane(1.6f, 0.34f), material, sign, "sign-board");
            board.Position = new Vector3(0f, height, 0f);
            board.EulerAngles = new Vector3(0f, turn, 0f);
        }
    }

    private void DirectionSign(Vector3 position, string text, float yaw)
    {
        Texture texture = TextTextures.Create(_scene, text, Color.FromRgb(20, 60, 130), Avalonia.Media.Colors.White, 768, 160, 58);
        Material material = _scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 0.2f, 0.4f) with
        {
            BaseColorMap = texture,
            CullMode = CullMode.None,
        });
        Node sign = _scene.CreateNode(null, "direction-sign");
        sign.Position = position;
        sign.EulerAngles = new Vector3(0f, yaw, 0f);
        foreach (float x in new[] { -1.1f, 1.1f })
        {
            _assets.AddBox(sign, new Vector3(x, 1.2f, 0f), new Vector3(0.08f, 2.4f, 0.08f), _palette.Metal, "sign-post");
        }

        Node board = _scene.AddMesh(_assets.Plane(2.6f, 0.55f), material, sign, "direction-board");
        board.Position = new Vector3(0f, 2.2f, 0.05f);
    }

    private void BuildStreetFurniture()
    {
        // Lamps along the boulevard (both sides) and the two streets.
        for (float z = -60f; z <= 80f; z += 28f)
        {
            StreetLamp(new Vector3(-8.4f, 0.15f, z), MathF.PI / 2f);
            StreetLamp(new Vector3(8.4f, 0.15f, z + 14f), -MathF.PI / 2f);
        }

        foreach (float streetZ in new[] { StreetA, StreetB })
        {
            for (float x = -58f; x <= 58f; x += 23f)
            {
                if (MathF.Abs(x) < 10f)
                {
                    continue;
                }

                StreetLamp(new Vector3(x, 0.15f, streetZ - 5.2f), 0f);
            }
        }

        StreetSign(new Vector3(-9.2f, 0.15f, StreetA + 5.5f), "Jl. Anggrek", "Boulevard Nusantara", 0f);
        StreetSign(new Vector3(9.2f, 0.15f, StreetB + 5.5f), "Jl. Cempaka", "Boulevard Nusantara", 0f);
        DirectionSign(new Vector3(9.5f, 0.15f, 70f), "Rumah Contoh  →  Jl. Anggrek", MathF.PI);
        DirectionSign(new Vector3(-9.5f, 0.15f, StreetA - 8f), "←  Taman  ·  Pertokoan", MathF.PI);
        DirectionSign(new Vector3(9.5f, 0.15f, StreetB + 8f), "Clubhouse & Kolam Renang  →", MathF.PI);

        // Bus shelter and bins by the gate.
        _assets.AddBox(null, new Vector3(-10.6f, 1.3f, 60f), new Vector3(1.8f, 2.6f, 4f), _palette.Glass, "bus-shelter");
        _assets.AddBox(null, new Vector3(-10.6f, 2.65f, 60f), new Vector3(2.2f, 0.1f, 4.4f), _palette.DarkMetal, "bus-shelter-roof");
    }

    private void AddNeighbourhoodLocations()
    {
        Locations.Insert(0, new Location("Lingkungan", "Gerbang Utama", new Vector3(6f, 3.2f, 108f), new Vector3(0f, 3f, 80f), false, "gate", "🚪"));
        Locations.Insert(1, new Location("Lingkungan", "Pemandangan Udara", new Vector3(70f, 70f, 100f), new Vector3(0f, 0f, 0f), false, null, "🚁"));
        Locations.Insert(2, new Location("Lingkungan", "Boulevard Nusantara", new Vector3(3.5f, 1.7f, 60f), new Vector3(0f, 1.5f, 20f), false, null, "🛣"));
        Locations.Add(new Location("Lingkungan", "Lapangan Olahraga", new Vector3(22f, 4f, 8f), new Vector3(34f, 0f, -4f), false, "court", "🏀"));
    }
}
