using System.Numerics;
using ThreeNet.Interop;

namespace ThreeNet;

/// <summary>
/// A node in the scene graph. A node is a thin handle into the native scene:
/// it holds no state of its own, and two handles pointing at the same node in
/// the same scene compare equal.
/// </summary>
public sealed class Node : IEquatable<Node>
{
    internal Node(Scene scene, uint id)
    {
        Scene = scene;
        Id = id;
    }

    /// <summary>The scene that owns this node.</summary>
    public Scene Scene { get; }

    /// <summary>Native identifier; <c>0</c> is the null handle.</summary>
    public uint Id { get; }

    /// <summary>True when this handle does not point at a node.</summary>
    public bool IsNull => Id == 0;

    /// <summary>Node name, used by <see cref="Scene.FindByName"/>.</summary>
    public unsafe string Name
    {
        get => NativeError.ReadString((buffer, capacity) =>
            NativeMethods.tn_node_get_name(Scene.Handle, Id, (byte*)buffer, capacity));
        set => NativeError.Check(NativeMethods.tn_node_set_name(Scene.Handle, Id, value));
    }

    /// <summary>Local position relative to the parent.</summary>
    public Vector3 Position
    {
        get => GetTransform().Translation;
        set => NativeError.Check(NativeMethods.tn_node_set_position(Scene.Handle, Id, value));
    }

    /// <summary>Local rotation relative to the parent.</summary>
    public Quaternion Rotation
    {
        get
        {
            NativeTransform transform = GetTransform();
            return new Quaternion(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W);
        }
        set => NativeError.Check(NativeMethods.tn_node_set_rotation(
            Scene.Handle, Id, new Vector4(value.X, value.Y, value.Z, value.W)));
    }

    /// <summary>
    /// Local rotation as Euler angles in radians (pitch, yaw, roll), applied in
    /// YXZ order like Three.js.
    /// </summary>
    public Vector3 EulerAngles
    {
        get
        {
            NativeError.Check(NativeMethods.tn_node_get_euler_angles(Scene.Handle, Id, out Vector3 angles));
            return angles;
        }
        set => NativeError.Check(NativeMethods.tn_node_set_euler_angles(Scene.Handle, Id, value));
    }

    /// <summary>Local scale relative to the parent.</summary>
    public Vector3 Scale
    {
        get => GetTransform().Scale;
        set => NativeError.Check(NativeMethods.tn_node_set_scale(Scene.Handle, Id, value));
    }

    /// <summary>Hides the node and its subtree when false.</summary>
    public bool Visible
    {
        set => NativeError.Check(NativeMethods.tn_node_set_visible(Scene.Handle, Id, value ? 1 : 0));
    }

    /// <summary>Layer bitmask; a camera only renders nodes sharing a bit with it.</summary>
    public uint Layers
    {
        set => NativeError.Check(NativeMethods.tn_node_set_layers(Scene.Handle, Id, value));
    }

    /// <summary>Free slot for application data (an id, an index, a handle).</summary>
    public ulong Tag
    {
        get
        {
            NativeError.Check(NativeMethods.tn_node_get_user_data(Scene.Handle, Id, out ulong value));
            return value;
        }
        set => NativeError.Check(NativeMethods.tn_node_set_user_data(Scene.Handle, Id, value));
    }

    /// <summary>Light attached to this node, if any.</summary>
    public Light? Light
    {
        get
        {
            int status = NativeMethods.tn_node_get_light(Scene.Handle, Id, out NativeLightDesc desc);
            return status == NativeStatus.Ok ? ThreeNet.Light.FromNative(desc) : null;
        }
        set
        {
            if (value is { } light)
            {
                NativeLightDesc desc = light.ToNative();
                NativeError.Check(NativeMethods.tn_node_set_light(Scene.Handle, Id, in desc));
            }
            else
            {
                NativeError.Check(NativeMethods.tn_node_clear_light(Scene.Handle, Id));
            }
        }
    }

    /// <summary>Camera attached to this node, if any.</summary>
    public Camera? Camera
    {
        get
        {
            int status = NativeMethods.tn_node_get_camera(Scene.Handle, Id, out NativeCameraDesc desc);
            return status == NativeStatus.Ok ? ThreeNet.Camera.FromNative(desc) : null;
        }
        set
        {
            if (value is { } camera)
            {
                NativeCameraDesc desc = camera.ToNative();
                NativeError.Check(NativeMethods.tn_node_set_camera(Scene.Handle, Id, in desc));
            }
            else
            {
                NativeError.Check(NativeMethods.tn_node_clear_camera(Scene.Handle, Id));
            }
        }
    }

    /// <summary>Parent node, or null for the root.</summary>
    public Node? Parent
    {
        get
        {
            uint parent = NativeMethods.tn_node_get_parent(Scene.Handle, Id);
            return parent == 0 ? null : new Node(Scene, parent);
        }
        set => NativeError.Check(NativeMethods.tn_scene_set_parent(Scene.Handle, Id, value?.Id ?? 0));
    }

    /// <summary>Direct children of this node.</summary>
    public IReadOnlyList<Node> Children
    {
        get
        {
            uint count = NativeMethods.tn_node_get_child_count(Scene.Handle, Id);
            Node[] children = new Node[count];
            for (uint i = 0; i < count; i++)
            {
                children[i] = new Node(Scene, NativeMethods.tn_node_get_child(Scene.Handle, Id, i));
            }

            return children;
        }
    }

    /// <summary>World matrix, with every ancestor transform applied.</summary>
    public unsafe Matrix4x4 WorldMatrix
    {
        get
        {
            Matrix4x4 matrix;
            NativeError.Check(NativeMethods.tn_node_get_world_matrix(Scene.Handle, Id, (float*)&matrix));
            return matrix;
        }
    }

    /// <summary>World space position, taken from the world matrix.</summary>
    public Vector3 WorldPosition => WorldMatrix.Translation;

    /// <summary>Creates a child node.</summary>
    public Node CreateChild(string? name = null) => Scene.CreateNode(this, name);

    /// <summary>Attaches a geometry / material pair so the node renders as a mesh.</summary>
    public void AttachMesh(Geometry geometry, Material material) =>
        NativeError.Check(NativeMethods.tn_node_attach_mesh(Scene.Handle, Id, geometry.Id, material.Id));

    /// <summary>
    /// Whether the mesh on this node renders into shadow maps. Defaults to true;
    /// has no effect on nodes without a mesh.
    /// </summary>
    public bool CastShadow
    {
        get => (GetShadowFlags() & 1) != 0;
        set => NativeError.Check(NativeMethods.tn_node_set_shadow_flags(Scene.Handle, Id, value ? 1 : 0, ReceiveShadow ? 1 : 0));
    }

    /// <summary>Whether the mesh on this node is darkened by shadows. Defaults to true.</summary>
    public bool ReceiveShadow
    {
        get => (GetShadowFlags() & 2) != 0;
        set => NativeError.Check(NativeMethods.tn_node_set_shadow_flags(Scene.Handle, Id, CastShadow ? 1 : 0, value ? 1 : 0));
    }

    /// <summary>
    /// Sets <see cref="CastShadow"/> and <see cref="ReceiveShadow"/> on this node and
    /// every descendant that has a mesh (handy for imported models).
    /// </summary>
    public void SetShadowsRecursive(bool cast, bool receive)
    {
        Stack<Node> pending = new();
        pending.Push(this);
        while (pending.TryPop(out Node? node))
        {
            // Nodes without a mesh report "no mesh"; they are simply skipped.
            _ = NativeMethods.tn_node_set_shadow_flags(Scene.Handle, node.Id, cast ? 1 : 0, receive ? 1 : 0);
            foreach (Node child in node.Children)
            {
                pending.Push(child);
            }
        }
    }

    private uint GetShadowFlags()
    {
        NativeError.Check(NativeMethods.tn_node_get_shadow_flags(Scene.Handle, Id, out uint flags));
        return flags;
    }

    /// <summary>Removes the mesh from this node, keeping the node itself.</summary>
    public void DetachMesh() => NativeError.Check(NativeMethods.tn_node_detach_mesh(Scene.Handle, Id));

    /// <summary>Sets the local transform in one call.</summary>
    public void SetTransform(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        NativeTransform transform = new()
        {
            Translation = position,
            Rotation = new Vector4(rotation.X, rotation.Y, rotation.Z, rotation.W),
            Scale = scale,
        };
        NativeError.Check(NativeMethods.tn_node_set_transform(Scene.Handle, Id, in transform));
    }

    /// <summary>Rotates the node so its -Z axis points at <paramref name="target"/>.</summary>
    public void LookAt(Vector3 target, Vector3? up = null) =>
        NativeError.Check(NativeMethods.tn_node_look_at(Scene.Handle, Id, target, up ?? Vector3.UnitY));

    /// <summary>Moves the node by <paramref name="delta"/> in parent space.</summary>
    public void Translate(Vector3 delta) => Position += delta;

    /// <summary>Rotates the node by <paramref name="rotation"/> in local space.</summary>
    public void Rotate(Quaternion rotation) => Rotation = Quaternion.Normalize(Rotation * rotation);

    /// <summary>
    /// Copies this node and its whole subtree under <paramref name="parent"/>
    /// (the scene root when null). The copy shares geometry, materials and
    /// textures, so instancing an imported model costs almost nothing.
    /// </summary>
    public Node Clone(Node? parent = null) =>
        new(Scene, NativeError.CheckHandle(NativeMethods.tn_node_clone(Scene.Handle, Id, parent?.Id ?? 0)));

    /// <summary>Removes this node and its subtree from the scene.</summary>
    public void Remove() => Scene.Remove(this);

    private NativeTransform GetTransform()
    {
        NativeError.Check(NativeMethods.tn_node_get_transform(Scene.Handle, Id, out NativeTransform transform));
        return transform;
    }

    public bool Equals(Node? other) => other is not null && Id == other.Id && ReferenceEquals(Scene, other.Scene);

    public override bool Equals(object? obj) => Equals(obj as Node);

    public override int GetHashCode() => HashCode.Combine(Scene, Id);

    public override string ToString() => IsNull ? "Node(null)" : $"Node(#{Id} {Name})";

    public static bool operator ==(Node? left, Node? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(Node? left, Node? right) => !(left == right);
}
