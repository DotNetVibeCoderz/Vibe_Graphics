using System.Numerics;
using ThreeNet.Interop;

namespace ThreeNet;

public enum RigidBodyType : uint
{
    Dynamic = 0,
    Fixed = 1,
    /// <summary>Follows its node (animation or code) and pushes dynamic bodies.</summary>
    Kinematic = 2,
}

public enum ColliderShape : uint
{
    Box = 0,
    Sphere = 1,
    /// <summary>Along the local Y axis.</summary>
    Capsule = 2,
    /// <summary>Along the local Y axis.</summary>
    Cylinder = 3,
    /// <summary>Exact triangles of a geometry; best for static level geometry.</summary>
    TriangleMesh = 4,
    ConvexHull = 5,
}

public enum JointType : uint
{
    Fixed = 0,
    /// <summary>Ball and socket.</summary>
    Ball = 1,
    /// <summary>Rotates around the axis.</summary>
    Hinge = 2,
    /// <summary>Slides along the axis.</summary>
    Slider = 3,
}

public record struct RigidBodyOptions
{
    public RigidBodyType Type;
    /// <summary>Added to the mass the colliders' densities give.</summary>
    public float AdditionalMass;
    public float LinearDamping;
    public float AngularDamping;
    public float GravityScale;
    /// <summary>Continuous collision detection for fast moving bodies.</summary>
    public bool ContinuousCollision;
    public bool LockRotations;
    public bool CanSleep;
    public Vector3 LinearVelocity;
    public Vector3 AngularVelocity;

    public RigidBodyOptions()
    {
        Type = RigidBodyType.Dynamic;
        AngularDamping = 0.05f;
        GravityScale = 1f;
        CanSleep = true;
    }

    public static RigidBodyOptions Dynamic => new();

    public static RigidBodyOptions Fixed => new() { Type = RigidBodyType.Fixed };

    public static RigidBodyOptions Kinematic => new() { Type = RigidBodyType.Kinematic };

    internal readonly NativeBodyDesc ToNative() => new()
    {
        Kind = (uint)Type,
        AdditionalMass = AdditionalMass,
        LinearDamping = LinearDamping,
        AngularDamping = AngularDamping,
        GravityScale = GravityScale,
        Ccd = ContinuousCollision ? 1 : 0,
        LockRotations = LockRotations ? 1 : 0,
        CanSleep = CanSleep ? 1 : 0,
        LinearVelocity = LinearVelocity,
        AngularVelocity = AngularVelocity,
    };
}

/// <summary>A collision shape in world units, attached to a node.</summary>
public record struct ColliderOptions
{
    public ColliderShape Shape;
    public Vector3 HalfExtents;
    public float Radius;
    public float HalfHeight;
    /// <summary>Source geometry for triangle meshes and convex hulls (scaled by the node's world scale).</summary>
    public Geometry? Geometry;
    /// <summary>Offset from the node origin, in the node's rotated frame.</summary>
    public Vector3 Offset;
    public Quaternion Rotation;
    public float Friction;
    public float Restitution;
    public float Density;
    /// <summary>Sensors report contacts but do not collide.</summary>
    public bool IsSensor;
    /// <summary>Collision groups this collider belongs to.</summary>
    public uint Membership;
    /// <summary>Collision groups this collider collides with.</summary>
    public uint Filter;

    public ColliderOptions()
    {
        Rotation = Quaternion.Identity;
        Friction = 0.5f;
        Density = 1f;
        Membership = uint.MaxValue;
        Filter = uint.MaxValue;
    }

    public static ColliderOptions Box(Vector3 halfExtents) => new() { Shape = ColliderShape.Box, HalfExtents = halfExtents };

    public static ColliderOptions Sphere(float radius) => new() { Shape = ColliderShape.Sphere, Radius = radius };

    public static ColliderOptions Capsule(float halfHeight, float radius) => new() { Shape = ColliderShape.Capsule, HalfHeight = halfHeight, Radius = radius };

    public static ColliderOptions Cylinder(float halfHeight, float radius) => new() { Shape = ColliderShape.Cylinder, HalfHeight = halfHeight, Radius = radius };

    public static ColliderOptions TriangleMesh(Geometry geometry) => new() { Shape = ColliderShape.TriangleMesh, Geometry = geometry };

    public static ColliderOptions ConvexHull(Geometry geometry) => new() { Shape = ColliderShape.ConvexHull, Geometry = geometry };

    internal readonly NativeColliderDesc ToNative() => new()
    {
        Shape = (uint)Shape,
        HalfExtents = HalfExtents,
        Radius = Radius,
        HalfHeight = HalfHeight,
        Geometry = Geometry?.Id ?? 0,
        Offset = Offset,
        Rotation = new Vector4(Rotation.X, Rotation.Y, Rotation.Z, Rotation.W),
        Friction = Friction,
        Restitution = Restitution,
        Density = Density,
        Sensor = IsSensor ? 1 : 0,
        Membership = Membership,
        Filter = Filter,
    };
}

public readonly record struct PhysicsHit(Node Node, float Distance, Vector3 Point, Vector3 Normal);

public readonly record struct ContactEvent(Node NodeA, Node NodeB, bool Started, bool IsSensor)
{
    /// <summary>True when either side is <paramref name="node"/>.</summary>
    public bool Involves(Node node) => NodeA.Equals(node) || NodeB.Equals(node);

    /// <summary>The node on the other side of <paramref name="node"/>.</summary>
    public Node Other(Node node) => NodeA.Equals(node) ? NodeB : NodeA;
}

/// <summary>A joint between two bodies.</summary>
public sealed class PhysicsJoint
{
    private readonly Scene _scene;

    internal PhysicsJoint(Scene scene, uint id)
    {
        _scene = scene;
        Id = id;
    }

    public uint Id { get; }

    public bool Remove() => NativeMethods.tn_physics_remove_joint(_scene.Handle, Id) == 1;
}

/// <summary>
/// Rigid body physics (Rapier) for a scene. Attach bodies and colliders to nodes,
/// then call <see cref="Step"/> every frame: dynamic bodies move their nodes,
/// kinematic bodies follow them. Access it through <see cref="Scene.Physics"/>.
/// </summary>
public sealed class PhysicsWorld
{
    private readonly Scene _scene;
    private Vector3 _gravity = new(0f, -9.81f, 0f);
    private float _fixedTimestep = 1f / 60f;
    private int _maxSubsteps = 8;

    internal PhysicsWorld(Scene scene) => _scene = scene;

    public Vector3 Gravity
    {
        get => _gravity;
        set
        {
            _gravity = value;
            Configure();
        }
    }

    /// <summary>Seconds per simulation step (default 1/60).</summary>
    public float FixedTimestep
    {
        get => _fixedTimestep;
        set
        {
            _fixedTimestep = value;
            Configure();
        }
    }

    /// <summary>Steps allowed per <see cref="Step"/> call before the backlog is dropped.</summary>
    public int MaxSubsteps
    {
        get => _maxSubsteps;
        set
        {
            _maxSubsteps = value;
            Configure();
        }
    }

    /// <summary>Raised from <see cref="Step"/> for every contact that started or stopped.</summary>
    public event Action<ContactEvent>? Contact;

    private void Configure() =>
        NativeError.Check(NativeMethods.tn_physics_configure(_scene.Handle, _gravity, _fixedTimestep, (uint)Math.Max(1, _maxSubsteps)));

    /// <summary>Adds a rigid body at the node's current world pose (one per node).</summary>
    public void AddBody(Node node, in RigidBodyOptions options)
    {
        NativeBodyDesc native = options.ToNative();
        NativeError.Check(NativeMethods.tn_physics_add_body(_scene.Handle, node.Id, in native));
    }

    /// <summary>Adds a collider to the node's body, or a static collider when it has none.</summary>
    public void AddCollider(Node node, in ColliderOptions options)
    {
        NativeColliderDesc native = options.ToNative();
        NativeError.Check(NativeMethods.tn_physics_add_collider(_scene.Handle, node.Id, in native));
    }

    /// <summary>Shorthand for a body plus one collider.</summary>
    public void Add(Node node, in RigidBodyOptions body, in ColliderOptions collider)
    {
        AddBody(node, body);
        AddCollider(node, collider);
    }

    public bool Remove(Node node) => NativeMethods.tn_physics_remove(_scene.Handle, node.Id) == 1;

    public bool HasBody(Node node) => NativeMethods.tn_physics_has_body(_scene.Handle, node.Id) == 1;

    /// <summary>Advances by <paramref name="deltaSeconds"/> in fixed steps; returns the number of steps.</summary>
    public int Step(float deltaSeconds)
    {
        int steps = NativeMethods.tn_physics_step(_scene.Handle, deltaSeconds);
        NativeError.Check(steps);
        if (steps > 0 && Contact is not null)
        {
            foreach (ContactEvent contact in TakeContactEvents())
            {
                Contact(contact);
            }
        }

        return steps;
    }

    /// <summary>Contact events since the last call (or since the last <see cref="Step"/> with a <see cref="Contact"/> handler).</summary>
    public unsafe IReadOnlyList<ContactEvent> TakeContactEvents()
    {
        const int Capacity = 512;
        NativeContactEvent* buffer = stackalloc NativeContactEvent[Capacity];
        int count = NativeMethods.tn_physics_take_events(_scene.Handle, buffer, Capacity);
        NativeError.Check(count);
        ContactEvent[] events = new ContactEvent[count];
        for (int i = 0; i < count; i++)
        {
            events[i] = new ContactEvent(new Node(_scene, buffer[i].NodeA), new Node(_scene, buffer[i].NodeB), buffer[i].Started != 0, buffer[i].Sensor != 0);
        }

        return events;
    }

    public void ApplyImpulse(Node node, Vector3 impulse, Vector3 torqueImpulse = default) =>
        NativeError.Check(NativeMethods.tn_physics_apply_impulse(_scene.Handle, node.Id, impulse, torqueImpulse));

    /// <summary>Force and torque applied during the next step.</summary>
    public void AddForce(Node node, Vector3 force, Vector3 torque = default) =>
        NativeError.Check(NativeMethods.tn_physics_add_force(_scene.Handle, node.Id, force, torque));

    public void SetVelocity(Node node, Vector3 linear, Vector3 angular = default) =>
        NativeError.Check(NativeMethods.tn_physics_set_velocity(_scene.Handle, node.Id, linear, angular));

    public (Vector3 Linear, Vector3 Angular) GetVelocity(Node node)
    {
        NativeError.Check(NativeMethods.tn_physics_get_velocity(_scene.Handle, node.Id, out Vector3 linear, out Vector3 angular));
        return (linear, angular);
    }

    /// <summary>Moves a body and its node instantly (world space).</summary>
    public void Teleport(Node node, Vector3 position, Quaternion? rotation = null)
    {
        Quaternion r = rotation ?? Quaternion.Identity;
        NativeError.Check(NativeMethods.tn_physics_teleport(_scene.Handle, node.Id, position, new Vector4(r.X, r.Y, r.Z, r.W)));
    }

    public bool IsSleeping(Node node)
    {
        int result = NativeMethods.tn_physics_is_sleeping(_scene.Handle, node.Id);
        NativeError.Check(result);
        return result == 1;
    }

    /// <summary>Closest collider along a ray (sensors ignored); <paramref name="exclude"/> skips one node's body.</summary>
    public PhysicsHit? Raycast(Vector3 origin, Vector3 direction, float maxDistance = 0f, Node? exclude = null)
    {
        int hit = NativeMethods.tn_physics_raycast(_scene.Handle, origin, direction, maxDistance, exclude?.Id ?? 0, out NativePhysicsHit native);
        NativeError.Check(hit);
        return hit == 1 ? new PhysicsHit(new Node(_scene, native.Node), native.Distance, native.Point, native.Normal) : null;
    }

    /// <summary>Joins two bodies. Anchors are in each node's local frame.</summary>
    public PhysicsJoint AddJoint(JointType type, Node a, Node b, Vector3 anchorA, Vector3 anchorB, Vector3? axis = null)
    {
        uint id = NativeMethods.tn_physics_add_joint(_scene.Handle, (uint)type, a.Id, b.Id, anchorA, anchorB, axis ?? Vector3.UnitY);
        return new PhysicsJoint(_scene, NativeError.CheckHandle(id));
    }

    /// <summary>
    /// Moves a kinematic character (kinematic body plus one collider, usually a
    /// capsule) by <paramref name="desired"/>, sliding along walls, climbing slopes
    /// and snapping to the ground. The node moves immediately.
    /// </summary>
    public (Vector3 Applied, bool Grounded) MoveCharacter(Node node, Vector3 desired, float deltaSeconds)
    {
        int result = NativeMethods.tn_physics_move_character(_scene.Handle, node.Id, desired, deltaSeconds, out Vector3 applied);
        NativeError.Check(result);
        return (applied, result == 1);
    }

    /// <summary>Character controller settings shared by every character.</summary>
    public void ConfigureCharacters(float maxSlopeDegrees = 45f, float stepHeight = 0.3f, float snapToGround = 0.2f) =>
        NativeError.Check(NativeMethods.tn_physics_configure_character(_scene.Handle, maxSlopeDegrees, stepHeight, snapToGround));
}
