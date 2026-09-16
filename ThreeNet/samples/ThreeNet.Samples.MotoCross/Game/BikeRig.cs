using System.Numerics;
using ThreeNet;

namespace MotoCross.Game;

/// <summary>
/// The rider and machine, assembled from primitives: chassis, tank, seat,
/// engine, exhaust, forks, swingarm, wheels and a rider who leans with the
/// bike, stands up over the bumps and tucks in on the straights.
/// </summary>
public sealed class BikeRig
{
    private readonly Node _root;
    private readonly Node _chassis;
    private readonly Node _fork;
    private readonly Node _frontWheel;
    private readonly Node _swingarm;
    private readonly Node _rearWheel;
    private readonly Node _handlebar;
    private readonly Node _rider;
    private readonly Node _riderTorso;
    private readonly Node _headlight;
    private readonly Node _headlightGlow;

    public BikeRig(Scene scene)
    {
        Material plastic = scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.72f, 0.09f, 0.06f, 1f), 0.15f, 0.28f));
        Material plasticWhite = scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.86f, 0.87f, 0.9f, 1f), 0.1f, 0.25f));
        Material chrome = scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.78f, 0.79f, 0.82f, 1f), 0.95f, 0.18f));
        Material engineMetal = scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.45f, 0.46f, 0.48f, 1f), 0.8f, 0.4f));
        Material rubber = scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.04f, 0.04f, 0.05f, 1f), 0f, 0.92f));
        Material gearFabric = scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.10f, 0.22f, 0.55f, 1f), 0f, 0.7f));
        Material helmetShell = scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.9f, 0.55f, 0.05f, 1f), 0.2f, 0.2f));
        Material visor = scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.05f, 0.06f, 0.08f, 0.85f), 0.6f, 0.08f) with
        {
            AlphaMode = AlphaMode.Blend,
        });
        Material lamp = scene.CreateMaterial(MaterialOptions.Basic(new Vector4(1f, 0.96f, 0.85f, 1f)) with
        {
            Emissive = new Vector3(1f, 0.94f, 0.8f),
            EmissiveIntensity = 8f,
        });

        // Geometry is shared between the parts that repeat.
        Geometry tyre = scene.CreateTorusGeometry(0.26f, 0.09f, 12, 28);
        Geometry rim = scene.CreateCylinderGeometry(0.17f, 0.17f, 0.09f, 16);
        Geometry tube = scene.CreateCylinderGeometry(0.035f, 0.035f, 1f, 8);
        Geometry limb = scene.CreateCylinderGeometry(0.075f, 0.09f, 1f, 8);

        _root = scene.CreateNode(null, "bike");
        _chassis = scene.CreateNode(_root, "chassis");

        // --- frame, tank, seat, engine ------------------------------------
        Node frame = scene.AddMesh(scene.CreateBoxGeometry(0.16f, 0.22f, 1.15f), plastic, _chassis, "frame");
        frame.Position = new Vector3(0f, 0.63f, 0.02f);

        Node tank = scene.AddMesh(scene.CreateBoxGeometry(0.3f, 0.26f, 0.5f), plastic, _chassis, "tank");
        tank.Position = new Vector3(0f, 0.82f, 0.12f);

        Node seat = scene.AddMesh(scene.CreateBoxGeometry(0.26f, 0.12f, 0.62f), rubber, _chassis, "seat");
        seat.Position = new Vector3(0f, 0.86f, -0.28f);

        Node engine = scene.AddMesh(scene.CreateBoxGeometry(0.34f, 0.34f, 0.42f), engineMetal, _chassis, "engine");
        engine.Position = new Vector3(0f, 0.5f, 0.02f);

        Node radiator = scene.AddMesh(scene.CreateBoxGeometry(0.06f, 0.26f, 0.2f), chrome, _chassis, "radiator");
        radiator.Position = new Vector3(0.19f, 0.66f, 0.2f);
        Node radiator2 = scene.AddMesh(scene.CreateBoxGeometry(0.06f, 0.26f, 0.2f), chrome, _chassis, "radiator");
        radiator2.Position = new Vector3(-0.19f, 0.66f, 0.2f);

        Node exhaust = scene.AddMesh(scene.CreateCylinderGeometry(0.055f, 0.075f, 0.95f, 10), chrome, _chassis, "exhaust");
        exhaust.Position = new Vector3(0.17f, 0.55f, -0.38f);
        exhaust.EulerAngles = new Vector3(MathF.PI / 2.1f, 0.12f, 0f);

        Node frontFender = scene.AddMesh(scene.CreateBoxGeometry(0.28f, 0.04f, 0.5f), plasticWhite, _chassis, "front-fender");
        frontFender.Position = new Vector3(0f, 0.86f, 0.72f);
        frontFender.EulerAngles = new Vector3(-0.22f, 0f, 0f);

        Node rearFender = scene.AddMesh(scene.CreateBoxGeometry(0.26f, 0.04f, 0.46f), plasticWhite, _chassis, "rear-fender");
        rearFender.Position = new Vector3(0f, 0.96f, -0.66f);
        rearFender.EulerAngles = new Vector3(0.18f, 0f, 0f);

        Node plate = scene.AddMesh(scene.CreatePlaneGeometry(0.3f, 0.26f), plasticWhite, _chassis, "number-plate");
        plate.Position = new Vector3(0f, 0.95f, 0.5f);
        plate.EulerAngles = new Vector3(-0.35f, 0f, 0f);

        _headlight = scene.AddMesh(scene.CreateSphereGeometry(0.1f, 16, 12), lamp, _chassis, "headlight");
        _headlight.Position = new Vector3(0f, 0.95f, 0.58f);
        _headlightGlow = scene.AddLight(
            Light.Spot(new Vector3(1f, 0.95f, 0.85f), 60f, range: 40f, innerAngle: 0.25f, outerAngle: 0.5f) with
            {
                Enabled = false,
                CastShadow = false,
            },
            _root,
            "headlight-beam");
        _headlightGlow.Position = new Vector3(0f, 0.95f, 0.6f);
        // Lights shine along -Z, the bike faces +Z.
        _headlightGlow.EulerAngles = new Vector3(0.1f, MathF.PI, 0f);

        // --- front suspension and wheel -----------------------------------
        _fork = scene.CreateNode(_chassis, "fork");
        _fork.Position = new Vector3(0f, 0.86f, 0.62f);
        for (int side = -1; side <= 1; side += 2)
        {
            Node leg = scene.AddMesh(tube, chrome, _fork, "fork-leg");
            leg.Position = new Vector3(side * 0.14f, -0.2f, 0.06f);
            leg.Scale = new Vector3(1f, 0.62f, 1f);
            leg.EulerAngles = new Vector3(-0.28f, 0f, 0f);
        }

        _handlebar = scene.AddMesh(tube, chrome, _fork, "handlebar");
        _handlebar.Position = new Vector3(0f, 0.2f, -0.02f);
        _handlebar.Scale = new Vector3(1f, 0.62f, 1f);
        _handlebar.EulerAngles = new Vector3(0f, 0f, MathF.PI / 2f);

        _frontWheel = scene.CreateNode(_fork, "front-wheel");
        _frontWheel.Position = new Vector3(0f, -0.44f, 0.14f);
        Node frontTyre = scene.AddMesh(tyre, rubber, _frontWheel, "tyre");
        frontTyre.EulerAngles = new Vector3(0f, MathF.PI / 2f, 0f);
        Node frontRim = scene.AddMesh(rim, chrome, _frontWheel, "rim");
        frontRim.EulerAngles = new Vector3(0f, 0f, MathF.PI / 2f);

        // --- swingarm and rear wheel --------------------------------------
        _swingarm = scene.CreateNode(_chassis, "swingarm");
        _swingarm.Position = new Vector3(0f, 0.55f, -0.3f);
        Node arm = scene.AddMesh(scene.CreateBoxGeometry(0.22f, 0.08f, 0.62f), engineMetal, _swingarm, "arm");
        arm.Position = new Vector3(0f, -0.04f, -0.3f);

        _rearWheel = scene.CreateNode(_swingarm, "rear-wheel");
        _rearWheel.Position = new Vector3(0f, -0.1f, -0.62f);
        Node rearTyre = scene.AddMesh(tyre, rubber, _rearWheel, "tyre");
        rearTyre.Scale = new Vector3(1.05f, 1.05f, 1.15f);
        rearTyre.EulerAngles = new Vector3(0f, MathF.PI / 2f, 0f);
        Node rearRim = scene.AddMesh(rim, chrome, _rearWheel, "rim");
        rearRim.EulerAngles = new Vector3(0f, 0f, MathF.PI / 2f);

        // --- rider ---------------------------------------------------------
        _rider = scene.CreateNode(_chassis, "rider");
        _rider.Position = new Vector3(0f, 0.9f, -0.1f);

        _riderTorso = scene.AddMesh(scene.CreateBoxGeometry(0.34f, 0.5f, 0.24f), gearFabric, _rider, "torso");
        _riderTorso.Position = new Vector3(0f, 0.3f, 0.06f);
        _riderTorso.EulerAngles = new Vector3(0.35f, 0f, 0f);

        Node head = scene.AddMesh(scene.CreateSphereGeometry(0.15f, 18, 14), helmetShell, _rider, "helmet");
        head.Position = new Vector3(0f, 0.65f, 0.18f);
        Node visorMesh = scene.AddMesh(scene.CreateBoxGeometry(0.2f, 0.09f, 0.06f), visor, head, "visor");
        visorMesh.Position = new Vector3(0f, 0.01f, 0.12f);

        for (int side = -1; side <= 1; side += 2)
        {
            Node arm2 = scene.AddMesh(limb, gearFabric, _rider, "arm");
            arm2.Position = new Vector3(side * 0.22f, 0.36f, 0.28f);
            arm2.Scale = new Vector3(1f, 0.5f, 1f);
            arm2.EulerAngles = new Vector3(MathF.PI / 2.1f, side * 0.25f, 0f);

            Node leg = scene.AddMesh(limb, gearFabric, _rider, "leg");
            leg.Position = new Vector3(side * 0.18f, -0.08f, -0.06f);
            leg.Scale = new Vector3(1.1f, 0.55f, 1.1f);
            leg.EulerAngles = new Vector3(0.5f, side * 0.1f, 0f);

            Node boot = scene.AddMesh(scene.CreateBoxGeometry(0.12f, 0.1f, 0.26f), rubber, _rider, "boot");
            boot.Position = new Vector3(side * 0.24f, -0.42f, 0.02f);
        }

        Root = _root;
        TryLoadModel(scene);
    }

    /// <summary>File name of the detailed rider and bike model, next to the executable.</summary>
    public const string ModelFile = "Assets/rider-bike.glb";

    /// <summary>Length of a real 450 cc motocross bike, used to scale the model.</summary>
    private const float ModelLength = 2.15f;

    /// <summary>True when the detailed glTF model replaced the primitive one.</summary>
    public bool UsesDetailedModel { get; private set; }

    /// <summary>
    /// Swaps the primitive bike for the detailed glTF model (rider and machine in
    /// one mesh, generated with Rodin) when it ships with the build. The model is
    /// scaled to real size, stood on the ground and turned to face +Z; the
    /// primitive rig stays as a fallback and for its suspension animation.
    /// </summary>
    private void TryLoadModel(Scene scene)
    {
        string path = Path.Combine(AppContext.BaseDirectory, ModelFile);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            Node holder = scene.CreateNode(_root, "rider-bike-model");
            ImportResult import = scene.LoadGltf(path, holder);
            BoundingBox bounds = scene.GetBounds(import.Root);
            Vector3 size = bounds.Max - bounds.Min;
            float length = MathF.Max(size.X, size.Z);
            float scale = ModelLength / MathF.Max(length, 1e-3f);

            // The model is authored lengthwise along Z; ModelYaw flips it when it
            // faces the other way.
            holder.Scale = new Vector3(scale);
            holder.EulerAngles = new Vector3(0f, (size.X > size.Z ? MathF.PI / 2f : 0f) + ModelYaw, 0f);
            Vector3 centre = (bounds.Min + bounds.Max) * 0.5f;
            holder.Position = new Vector3(0f, -bounds.Min.Y * scale, 0f)
                - Vector3.Transform(new Vector3(centre.X, 0f, centre.Z) * scale, holder.Rotation);

            _chassis.Visible = false;
            _modelHolder = holder;
            UsesDetailedModel = true;
        }
        catch (ThreeNetException)
        {
            // A broken asset simply keeps the primitive bike.
            _chassis.Visible = true;
        }
    }

    /// <summary>Extra yaw applied to the imported model so it faces the direction of travel.</summary>
    private const float ModelYaw = 0f;

    private Node? _modelHolder;

    public Node Root { get; }

    /// <summary>Where the chase camera should look, in world space.</summary>
    public Vector3 HeadPosition => _root.WorldPosition + new Vector3(0f, 1.5f, 0f);

    public void SetHeadlight(bool on)
    {
        _headlightGlow.Light = _headlightGlow.Light is { } light ? light with { Enabled = on } : null;
        _headlight.Visible = true;
    }

    /// <summary>Copies the physics state onto the rig and animates the details.</summary>
    public void Update(BikePhysics bike, float dt, double totalSeconds)
    {
        _root.Position = bike.Position - new Vector3(0f, 0.42f, 0f);
        // Node Euler angles are applied YXZ: yaw, then pitch, then roll.
        _root.EulerAngles = new Vector3(-bike.Pitch, bike.Yaw, bike.Roll);

        if (_modelHolder is not null)
        {
            // A single mesh has no separate suspension: sell it with a little
            // squat under compression and a bob over the bumps.
            float squat = ((bike.FrontCompression + bike.RearCompression) * 0.5f) * 0.08f;
            _modelHolder.Scale = new Vector3(_modelHolder.Scale.X, _modelHolder.Scale.X * (1f - squat), _modelHolder.Scale.X);
            return;
        }

        // Wheels spin with the ground speed.
        _frontWheel.EulerAngles = new Vector3(0f, 0f, -bike.WheelSpin);
        _rearWheel.EulerAngles = new Vector3(0f, 0f, -bike.WheelSpin);

        // Suspension: the fork dives under braking and compresses on landing,
        // the swingarm squats under power.
        float forkTravel = -0.16f * bike.FrontCompression;
        _fork.Position = new Vector3(0f, 0.86f + forkTravel, 0.62f);
        _swingarm.EulerAngles = new Vector3(0.24f * bike.RearCompression, 0f, 0f);

        // The bars follow the steering, the rider follows the bars.
        float steerAngle = Math.Clamp(bike.Roll * 0.7f, -0.45f, 0.45f);
        _fork.EulerAngles = new Vector3(0f, steerAngle, 0f);

        // Attack position: stand up over bumps and when landing, tuck at speed.
        float bumpiness = MathF.Min(1f, (bike.FrontCompression + bike.RearCompression) * 2.2f);
        float standing = bike.Grounded ? bumpiness : 0.85f;
        float tuck = Math.Clamp(bike.Speed / 30f, 0f, 1f);
        float bob = bike.Grounded ? MathF.Sin((float)totalSeconds * 9f) * 0.012f * bumpiness : 0f;
        _rider.Position = new Vector3(0f, 0.9f + (standing * 0.16f) + bob, -0.1f + (tuck * 0.1f));
        _riderTorso.EulerAngles = new Vector3(0.35f + (tuck * 0.35f) - (standing * 0.15f), 0f, -bike.Roll * 0.25f);
        _rider.EulerAngles = new Vector3(0f, -steerAngle * 0.3f, -bike.Roll * 0.35f);

        _ = dt;
    }
}
