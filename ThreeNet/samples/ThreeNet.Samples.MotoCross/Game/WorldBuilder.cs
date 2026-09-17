using System.Numerics;
using ThreeNet;

namespace MotoCross.Game;

/// <summary>
/// Builds the whole circuit: landscape mesh, the graded track ribbon, scenery
/// (trees, rocks, tyre walls, banners, flags, the start gate and floodlights)
/// and the sky dome. Trees, boulders and tyre stacks are Rodin (Hyper3D) models;
/// everything else is procedural.
/// </summary>
public sealed class WorldBuilder(Scene scene, TerrainField terrain)
{
    private readonly Scene _scene = scene;
    private readonly TerrainField _terrain = terrain;
    private readonly Track _track = terrain.Track;
    private readonly Random _random = new(20260916);

    /// <summary>Street lamps and floodlights, switched on for the night preset.</summary>
    public List<Node> NightLights { get; } = [];

    /// <summary>Marker posts lit at night, useful for the "circuit at dusk" look.</summary>
    public List<Node> Flags { get; } = [];

    public void Build()
    {
        (Texture dirt, Texture dirtNormal) = ProceduralTextures.Dirt(_scene);
        (Texture grass, Texture grassNormal) = ProceduralTextures.Grass(_scene);
        (Texture concrete, Texture concreteNormal) = ProceduralTextures.Concrete(_scene);

        Material grassMaterial = _scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 0f, 0.95f) with
        {
            BaseColorMap = grass,
            NormalMap = grassNormal,
            NormalScale = 0.8f,
            UvScale = new Vector2(26f, 26f),
        });
        Material dirtMaterial = _scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 0f, 0.92f) with
        {
            BaseColorMap = dirt,
            NormalMap = dirtNormal,
            NormalScale = 1.2f,
            UvScale = Vector2.One,
        });
        Material concreteMaterial = _scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 0f, 0.85f) with
        {
            BaseColorMap = concrete,
            NormalMap = concreteNormal,
            UvScale = new Vector2(2f, 2f),
        });

        BuildLandscape(grassMaterial);
        BuildTrackRibbon(dirtMaterial);
        BuildTrackEdges(concreteMaterial);
        BuildScenery(concreteMaterial);
        BuildStartGate(concreteMaterial);
        BuildSky();
    }

    /// <summary>Coarse landscape grid; the track corridor is lowered slightly so
    /// the finer ribbon mesh always wins the depth test.</summary>
    private void BuildLandscape(Material material)
    {
        const int resolution = 192;
        const float half = TerrainField.WorldSize * 0.5f;
        float step = TerrainField.WorldSize / resolution;

        MeshBuilder builder = new();
        for (int z = 0; z <= resolution; z++)
        {
            for (int x = 0; x <= resolution; x++)
            {
                float wx = -half + (x * step);
                float wz = -half + (z * step);
                GroundSample ground = _terrain.Sample(wx, wz);
                float y = ground.Height - (ground.DirtWeight * 0.04f);
                builder.Add(
                    new Vector3(wx, y, wz),
                    Vector3.UnitY,
                    new Vector2(x / (float)resolution, z / (float)resolution));
            }
        }

        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                uint a = (uint)((z * (resolution + 1)) + x);
                uint b = a + 1;
                uint c = a + (uint)resolution + 2;
                uint d = a + (uint)resolution + 1;
                // Counter clockwise seen from above, so the surface faces the sky.
                builder.Quad(a, d, c, b);
            }
        }

        Geometry geometry = builder.Build(_scene, computeNormals: true);
        Node node = _scene.AddMesh(geometry, material, name: "landscape");
        node.CastShadow = false;
    }

    /// <summary>High resolution ribbon following the centreline, carrying the
    /// jumps, whoops and berms the bike actually rides.</summary>
    private void BuildTrackRibbon(Material material)
    {
        const int lateralSteps = 10;
        int samples = _track.Samples.Count;
        // The ribbon covers the whole graded corridor, so no landscape polygon peeks through.
        float width = Track.HalfWidth + Track.ShoulderWidth;

        MeshBuilder builder = new();
        for (int i = 0; i <= samples; i++)
        {
            float distance = i / (float)samples * _track.Length;
            Vector3 centre = _track.PositionAt(distance);
            Vector3 tangent = _track.TangentAt(distance);
            Vector3 right = Vector3.Normalize(new Vector3(tangent.Z, 0f, -tangent.X));

            for (int j = 0; j <= lateralSteps; j++)
            {
                float lateral = ((j / (float)lateralSteps) - 0.5f) * 2f * width;
                Vector3 position = centre + (right * lateral);
                GroundSample ground = _terrain.Sample(position.X, position.Z);
                builder.Add(
                    new Vector3(position.X, ground.Height + 0.02f, position.Z),
                    Vector3.UnitY,
                    new Vector2((lateral / 3f) + 0.5f, distance / 6f));
            }
        }

        for (int i = 0; i < samples; i++)
        {
            for (int j = 0; j < lateralSteps; j++)
            {
                uint a = (uint)((i * (lateralSteps + 1)) + j);
                uint b = a + 1;
                uint c = a + (uint)lateralSteps + 2;
                uint d = a + (uint)lateralSteps + 1;
                builder.Quad(a, d, c, b);
            }
        }

        Geometry geometry = builder.Build(_scene, computeNormals: true);
        Node node = _scene.AddMesh(geometry, material, name: "track");
        node.CastShadow = false;
    }

    /// <summary>Kerb blocks and hazard boards along the fast corners.</summary>
    private void BuildTrackEdges(Material concrete)
    {
        Texture hazard = ProceduralTextures.Hazard(_scene, new Vector3(0.75f, 0.12f, 0.07f), new Vector3(0.92f, 0.9f, 0.86f));
        Material hazardMaterial = _scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 0f, 0.7f) with
        {
            BaseColorMap = hazard,
            UvScale = new Vector2(2f, 1f),
            CullMode = CullMode.None,
        });
        Geometry board = _scene.CreateBoxGeometry(2.4f, 0.9f, 0.08f);
        Geometry post = _scene.CreateCylinderGeometry(0.06f, 0.07f, 1.1f, 6);
        Material postMaterial = _scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.25f, 0.24f, 0.23f, 1f), 0.1f, 0.8f));

        float spacing = 9f;
        for (float distance = 0f; distance < _track.Length; distance += spacing)
        {
            Vector3 centre = _track.PositionAt(distance);
            Vector3 tangent = _track.TangentAt(distance);
            Vector3 right = Vector3.Normalize(new Vector3(tangent.Z, 0f, -tangent.X));
            float bank = MathF.Abs(_track.BankAt(distance));
            // Boards only guard the corners, where riders actually run wide.
            if (bank < 0.12f)
            {
                continue;
            }

            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 position = centre + (right * side * (Track.HalfWidth + 1.6f));
                GroundSample ground = _terrain.Sample(position.X, position.Z);
                Node node = _scene.AddMesh(board, hazardMaterial, name: "hazard");
                node.Position = new Vector3(position.X, ground.Height + 0.45f, position.Z);
                node.EulerAngles = new Vector3(0f, MathF.Atan2(tangent.X, tangent.Z), 0f);

                Node stake = _scene.AddMesh(post, postMaterial, name: "post");
                stake.Position = new Vector3(position.X, ground.Height + 0.55f, position.Z);
            }
        }

        // Tyre walls at the two tightest hairpins.
        Geometry tyre = _scene.CreateTorusGeometry(0.55f, 0.22f, 10, 20);
        Material rubber = _scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.05f, 0.05f, 0.06f, 1f), 0f, 0.9f));
        foreach (float distance in new[] { _track.Length * 0.27f, _track.Length * 0.72f })
        {
            Vector3 centre = _track.PositionAt(distance);
            Vector3 tangent = _track.TangentAt(distance);
            Vector3 right = Vector3.Normalize(new Vector3(tangent.Z, 0f, -tangent.X));
            for (int i = -4; i <= 4; i++)
            {
                Vector3 position = centre + (right * (Track.HalfWidth + 2.2f)) + (tangent * i * 1.2f);
                GroundSample ground = _terrain.Sample(position.X, position.Z);
                for (int layer = 0; layer < 2; layer++)
                {
                    Node node = _scene.AddMesh(tyre, rubber, name: "tyre");
                    node.Position = new Vector3(position.X, ground.Height + 0.22f + (layer * 0.42f), position.Z);
                    node.EulerAngles = new Vector3(MathF.PI / 2f, 0f, 0f);
                }
            }
        }

        _ = concrete;
    }

    /// <summary>Trees, rocks, hay bales, marker flags and floodlight towers.</summary>
    private void BuildScenery(Material concrete)
    {
        ModelLibrary models = new(_scene);
        Geometry trunk = _scene.CreateCylinderGeometry(0.16f, 0.26f, 3.2f, 7);
        Geometry canopy = _scene.CreateConeGeometry(1.7f, 4.4f, 9);
        Geometry rock = _scene.CreateSphereGeometry(1f, 9, 6);
        Geometry bale = _scene.CreateCylinderGeometry(0.6f, 0.6f, 1.2f, 10);

        Material bark = _scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.18f, 0.12f, 0.08f, 1f), 0f, 0.95f));
        Material leaves = _scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.08f, 0.22f, 0.07f, 1f), 0f, 0.85f));
        Material stone = _scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.24f, 0.23f, 0.22f, 1f), 0f, 0.9f));
        Material straw = _scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.52f, 0.42f, 0.15f, 1f), 0f, 0.95f));

        // Rodin trees are detailed (~35k triangles), so there are fewer, bigger
        // ones, denser near the circuit where the rider actually sees them.
        int placed = 0;
        for (int attempt = 0; attempt < 2000 && placed < 190; attempt++)
        {
            float x = ((float)_random.NextDouble() - 0.5f) * TerrainField.WorldSize * 0.95f;
            float z = ((float)_random.NextDouble() - 0.5f) * TerrainField.WorldSize * 0.95f;
            GroundSample ground = _terrain.Sample(x, z);
            if (ground.Distance < Track.HalfWidth + 7f || (ground.Distance > 70f && _random.NextDouble() > 0.35))
            {
                continue;
            }

            placed++;
            bool pine = _random.NextDouble() > 0.4;
            float height = pine ? 9f + ((float)_random.NextDouble() * 7f) : 7f + ((float)_random.NextDouble() * 5f);
            float yaw = (float)_random.NextDouble() * MathF.Tau;
            Vector3 position = new(x, ground.Height - 0.15f, z);
            if (models.Place(pine ? "pine-tree" : "oak-tree", null, position, yaw, height, fitHeight: true) is { } placedTree)
            {
                // Only trees close to the circuit fall inside the shadow cascades
                // often enough to be worth drawing into them.
                if (ground.Distance > 35f)
                {
                    placedTree.SetShadowsRecursive(cast: false, receive: true);
                }

                continue;
            }

            Node tree = _scene.CreateNode(null, "tree");
            tree.Position = position;
            tree.Scale = new Vector3(height / 6.6f);
            Node stem = _scene.AddMesh(trunk, bark, tree, "trunk");
            stem.Position = new Vector3(0f, 1.6f, 0f);
            Node crown = _scene.AddMesh(canopy, leaves, tree, "canopy");
            crown.Position = new Vector3(0f, 4.4f, 0f);
        }

        for (int i = 0; i < 90; i++)
        {
            float x = ((float)_random.NextDouble() - 0.5f) * TerrainField.WorldSize * 0.9f;
            float z = ((float)_random.NextDouble() - 0.5f) * TerrainField.WorldSize * 0.9f;
            GroundSample ground = _terrain.Sample(x, z);
            if (ground.Distance < Track.HalfWidth + 3f)
            {
                continue;
            }

            float size = 0.8f + ((float)_random.NextDouble() * 2.4f);
            float yaw = (float)_random.NextDouble() * MathF.Tau;
            // Sink boulders a little so they read as bedded into the slope.
            Vector3 position = new(x, ground.Height - (size * 0.12f), z);
            if (models.Place("boulder", null, position, yaw, size) is not null)
            {
                continue;
            }

            Node node = _scene.AddMesh(rock, stone, name: "rock");
            node.Position = position;
            node.Scale = new Vector3(size * 0.5f, size * 0.3f, size * 0.5f);
            node.EulerAngles = new Vector3(0f, yaw, 0f);
        }

        // Tyre walls on the outside of every corner-ish feature entry.
        foreach (TrackFeature feature in _track.Features)
        {
            float distance = feature.Start - 6f;
            Vector3 centre = _track.PositionAt(distance);
            Vector3 tangent = _track.TangentAt(distance);
            Vector3 right = Vector3.Normalize(new Vector3(tangent.Z, 0f, -tangent.X));
            for (int i = 0; i < 4; i++)
            {
                Vector3 position = centre + (right * (Track.HalfWidth + 1.8f)) + (tangent * ((i - 1.5f) * 1.25f));
                GroundSample ground = _terrain.Sample(position.X, position.Z);
                models.Place("tire-stack", null, new Vector3(position.X, ground.Height, position.Z), i * 0.9f, 1.15f);
            }
        }

        // Hay bales in the run-off areas of the jump landings.
        foreach (TrackFeature feature in _track.Features)
        {
            for (int i = 0; i < 6; i++)
            {
                float distance = feature.Start + (feature.Length * 0.5f) + ((i - 3) * 1.6f);
                Vector3 centre = _track.PositionAt(distance);
                Vector3 tangent = _track.TangentAt(distance);
                Vector3 right = Vector3.Normalize(new Vector3(tangent.Z, 0f, -tangent.X));
                Vector3 position = centre + (right * (i % 2 == 0 ? 1f : -1f) * (Track.HalfWidth + 3.4f));
                GroundSample ground = _terrain.Sample(position.X, position.Z);
                Node node = _scene.AddMesh(bale, straw, name: "bale");
                node.Position = new Vector3(position.X, ground.Height + 0.6f, position.Z);
                node.EulerAngles = new Vector3(MathF.PI / 2f, MathF.Atan2(tangent.X, tangent.Z), 0f);
            }
        }

        BuildFloodlights(concrete);
        BuildMarkerFlags();
    }

    private void BuildFloodlights(Material concrete)
    {
        Geometry mast = _scene.CreateCylinderGeometry(0.12f, 0.2f, 12f, 8);
        Geometry head = _scene.CreateBoxGeometry(2.2f, 0.9f, 0.5f);
        Material metal = _scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.42f, 0.44f, 0.47f, 1f), 0.85f, 0.35f));
        Material lampMaterial = _scene.CreateMaterial(MaterialOptions.Basic(new Vector4(1f, 0.95f, 0.85f, 1f)) with
        {
            Emissive = new Vector3(1f, 0.93f, 0.78f),
            EmissiveIntensity = 6f,
        });

        for (int i = 0; i < 6; i++)
        {
            float distance = _track.Length * i / 6f;
            Vector3 centre = _track.PositionAt(distance);
            Vector3 tangent = _track.TangentAt(distance);
            Vector3 right = Vector3.Normalize(new Vector3(tangent.Z, 0f, -tangent.X));
            Vector3 position = centre + (right * (Track.HalfWidth + 7f));
            GroundSample ground = _terrain.Sample(position.X, position.Z);

            Node tower = _scene.CreateNode(null, "floodlight");
            tower.Position = new Vector3(position.X, ground.Height, position.Z);
            Node pole = _scene.AddMesh(mast, metal, tower, "mast");
            pole.Position = new Vector3(0f, 6f, 0f);
            Node box = _scene.AddMesh(head, lampMaterial, tower, "head");
            box.Position = new Vector3(0f, 12f, 0f);
            box.LookAt(new Vector3(centre.X, ground.Height + 1f, centre.Z));

            // The spot light itself: off during the day, switched on at night.
            Node lamp = _scene.AddLight(
                Light.Spot(new Vector3(1f, 0.94f, 0.82f), 900f, range: 60f, innerAngle: 0.35f, outerAngle: 0.62f) with
                {
                    CastShadow = i % 2 == 0,
                    Enabled = false,
                    ShadowNormalBias = 2f,
                },
                tower,
                "floodlight-lamp");
            lamp.Position = new Vector3(0f, 12f, 0f);
            lamp.LookAt(new Vector3(centre.X, ground.Height, centre.Z));
            NightLights.Add(lamp);
        }
    }

    private void BuildMarkerFlags()
    {
        Texture checker = ProceduralTextures.Checker(_scene, new Vector3(0.9f, 0.9f, 0.9f), new Vector3(0.05f, 0.05f, 0.05f), 4);
        Material flagMaterial = _scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 0f, 0.8f) with
        {
            BaseColorMap = checker,
            CullMode = CullMode.None,
        });
        Geometry cloth = _scene.CreatePlaneGeometry(1.1f, 0.7f);
        Geometry pole = _scene.CreateCylinderGeometry(0.03f, 0.03f, 2.2f, 6);
        Material poleMaterial = _scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.8f, 0.8f, 0.82f, 1f), 0.6f, 0.4f));

        for (int i = 0; i < 26; i++)
        {
            float distance = _track.Length * i / 26f;
            Vector3 centre = _track.PositionAt(distance);
            Vector3 tangent = _track.TangentAt(distance);
            Vector3 right = Vector3.Normalize(new Vector3(tangent.Z, 0f, -tangent.X));
            int side = i % 2 == 0 ? 1 : -1;
            Vector3 position = centre + (right * side * (Track.HalfWidth + 2.8f));
            GroundSample ground = _terrain.Sample(position.X, position.Z);

            Node group = _scene.CreateNode(null, "flag");
            group.Position = new Vector3(position.X, ground.Height, position.Z);
            group.EulerAngles = new Vector3(0f, MathF.Atan2(tangent.X, tangent.Z), 0f);
            Node stick = _scene.AddMesh(pole, poleMaterial, group, "pole");
            stick.Position = new Vector3(0f, 1.1f, 0f);
            Node banner = _scene.AddMesh(cloth, flagMaterial, group, "cloth");
            banner.Position = new Vector3(0.55f, 1.8f, 0f);
            banner.EulerAngles = new Vector3(0f, MathF.PI / 2f, 0f);
            Flags.Add(banner);
        }
    }

    /// <summary>Start / finish gate with a checkered banner and grid markings.</summary>
    private void BuildStartGate(Material concrete)
    {
        Vector3 centre = _track.PositionAt(0f);
        Vector3 tangent = _track.TangentAt(0f);
        float heading = MathF.Atan2(tangent.X, tangent.Z);
        GroundSample ground = _terrain.Sample(centre.X, centre.Z);

        Node gate = _scene.CreateNode(null, "start-gate");
        gate.Position = new Vector3(centre.X, ground.Height, centre.Z);
        gate.EulerAngles = new Vector3(0f, heading, 0f);

        Geometry pillar = _scene.CreateBoxGeometry(0.7f, 6.5f, 0.7f);
        for (int side = -1; side <= 1; side += 2)
        {
            Node column = _scene.AddMesh(pillar, concrete, gate, "gate-pillar");
            column.Position = new Vector3(side * (Track.HalfWidth + 1.4f), 3.25f, 0f);
        }

        Texture checker = ProceduralTextures.Checker(_scene, new Vector3(0.95f, 0.95f, 0.95f), new Vector3(0.04f, 0.04f, 0.04f), 10);
        Material bannerMaterial = _scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 0f, 0.75f) with
        {
            BaseColorMap = checker,
            CullMode = CullMode.None,
            UvScale = new Vector2(3f, 1f),
        });
        Node banner = _scene.AddMesh(_scene.CreatePlaneGeometry((Track.HalfWidth + 1.4f) * 2f, 1.6f), bannerMaterial, gate, "gate-banner");
        banner.Position = new Vector3(0f, 6f, 0f);

        Node line = _scene.AddMesh(_scene.CreatePlaneGeometry(Track.HalfWidth * 2f, 1.2f), bannerMaterial, gate, "finish-line");
        line.Position = new Vector3(0f, 0.08f, 0f);
        line.EulerAngles = new Vector3(-MathF.PI / 2f, 0f, 0f);
        line.CastShadow = false;
    }

    /// <summary>Inverted sphere painted with a vertical gradient; its colours are
    /// swapped by the day / night presets.</summary>
    private void BuildSky()
    {
        Material material = _scene.CreateMaterial(MaterialOptions.Basic(new Vector4(0.42f, 0.6f, 0.85f, 1f)) with
        {
            CullMode = CullMode.Front,
            BaseColorMap = SkyGradient(),
            RenderOrder = -10,
        });
        Node dome = _scene.AddMesh(_scene.CreateSphereGeometry(400f, 32, 20), material, name: "sky");
        dome.CastShadow = false;
        dome.ReceiveShadow = false;
        SkyDome = dome;
        SkyMaterial = material;
    }

    private Texture SkyGradient()
    {
        const int size = 128;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            float t = y / (float)(size - 1);
            // Horizon haze at the bottom, deeper blue overhead.
            Vector3 color = Vector3.Lerp(new Vector3(0.75f, 0.82f, 0.92f), new Vector3(0.18f, 0.36f, 0.72f), MathF.Pow(1f - t, 1.4f));
            for (int x = 0; x < size; x++)
            {
                int index = ((y * size) + x) * 4;
                pixels[index] = (byte)(MathF.Pow(color.X, 1f / 2.2f) * 255f);
                pixels[index + 1] = (byte)(MathF.Pow(color.Y, 1f / 2.2f) * 255f);
                pixels[index + 2] = (byte)(MathF.Pow(color.Z, 1f / 2.2f) * 255f);
                pixels[index + 3] = 255;
            }
        }

        Texture texture = _scene.CreateTexture(size, size, pixels);
        texture.SetSampler(WrapMode.ClampToEdge, WrapMode.ClampToEdge, linearFilter: true, mipmaps: false, anisotropy: 1);
        return texture;
    }

    public Node? SkyDome { get; private set; }

    public Material? SkyMaterial { get; private set; }
}
