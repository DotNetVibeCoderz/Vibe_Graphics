using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ThreeNet.Networking;

/// <summary>A node whose transform is shared through a <see cref="NetworkSession"/>.</summary>
public sealed class ReplicatedNode
{
    private readonly List<(double Time, Vector3 Position, Quaternion Rotation, Vector3 Scale)> _buffer = [];

    internal ReplicatedNode(NetworkSession session, Node node, uint networkId, bool owned)
    {
        Session = session;
        Node = node;
        NetworkId = networkId;
        IsOwned = owned;
    }

    public NetworkSession Session { get; }

    public Node Node { get; }

    public uint NetworkId { get; }

    /// <summary>True on the peer that sends this node's transform.</summary>
    public bool IsOwned { get; }

    /// <summary>Snapshots received so far (remote copies only).</summary>
    public int SnapshotCount { get; private set; }

    /// <summary>Remote copies snap instead of blending when the gap is larger than this (teleports).</summary>
    public float SnapDistance { get; set; } = 10f;

    internal (Vector3 Position, Quaternion Rotation, Vector3 Scale) Capture()
    {
        Matrix4x4.Decompose(Node.WorldMatrix, out Vector3 scale, out Quaternion rotation, out Vector3 position);
        return (position, rotation, scale);
    }

    internal void AddSnapshot(double time, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        SnapshotCount++;
        if (_buffer.Count > 0 && time <= _buffer[^1].Time)
        {
            return; // out of order
        }

        _buffer.Add((time, position, rotation, scale));
        if (_buffer.Count > 32)
        {
            _buffer.RemoveRange(0, _buffer.Count - 32);
        }
    }

    internal void Apply(double renderTime)
    {
        if (IsOwned || _buffer.Count == 0)
        {
            return;
        }

        (double Time, Vector3 Position, Quaternion Rotation, Vector3 Scale) target;
        if (renderTime <= _buffer[0].Time)
        {
            target = _buffer[0];
        }
        else if (renderTime >= _buffer[^1].Time)
        {
            // No newer data: hold the last state rather than extrapolating.
            target = _buffer[^1];
        }
        else
        {
            int index = _buffer.FindIndex(s => s.Time > renderTime);
            var a = _buffer[index - 1];
            var b = _buffer[index];
            float t = (float)((renderTime - a.Time) / Math.Max(1e-6, b.Time - a.Time));
            target = Vector3.Distance(a.Position, b.Position) > SnapDistance
                ? b
                : (renderTime, Vector3.Lerp(a.Position, b.Position, t), Quaternion.Slerp(a.Rotation, b.Rotation, t), Vector3.Lerp(a.Scale, b.Scale, t));
            // Keep one snapshot older than the render time.
            if (index > 1)
            {
                _buffer.RemoveRange(0, index - 1);
            }
        }

        SetWorld(target.Position, target.Rotation, target.Scale);
    }

    private void SetWorld(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Matrix4x4 world = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
        if (Node.Parent is { } parent && Matrix4x4.Invert(parent.WorldMatrix, out Matrix4x4 inverse))
        {
            world *= inverse;
        }

        Matrix4x4.Decompose(world, out Vector3 localScale, out Quaternion localRotation, out Vector3 localPosition);
        Node.Position = localPosition;
        Node.Rotation = localRotation;
        Node.Scale = localScale;
    }
}

internal sealed class PacketWriter
{
    private readonly MemoryStream _stream = new();

    public void WriteByte(byte value) => _stream.WriteByte(value);

    public void WriteUInt16(ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void WriteUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void WriteDouble(double value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void WriteSingle(float value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void WriteVector3(Vector3 v)
    {
        WriteSingle(v.X);
        WriteSingle(v.Y);
        WriteSingle(v.Z);
    }

    public void WriteQuaternion(Quaternion q)
    {
        WriteSingle(q.X);
        WriteSingle(q.Y);
        WriteSingle(q.Z);
        WriteSingle(q.W);
    }

    public void WriteString(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt16((ushort)Math.Min(bytes.Length, ushort.MaxValue));
        _stream.Write(bytes, 0, Math.Min(bytes.Length, ushort.MaxValue));
    }

    /// <summary>Length prefixed (u32) byte payload.</summary>
    public void WriteBytes(byte[] value)
    {
        WriteUInt32((uint)value.Length);
        _stream.Write(value);
    }

    public byte[] ToArray() => _stream.ToArray();
}

internal sealed class PacketReader(byte[] data)
{
    public int Position { get; private set; }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (Position + count > data.Length)
        {
            throw new EndOfStreamException("packet too short");
        }

        ReadOnlySpan<byte> span = data.AsSpan(Position, count);
        Position += count;
        return span;
    }

    public byte ReadByte() => Take(1)[0];

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public Vector3 ReadVector3() => new(ReadSingle(), ReadSingle(), ReadSingle());

    public Quaternion ReadQuaternion() => new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());

    public string ReadString() => Encoding.UTF8.GetString(Take(ReadUInt16()));

    public byte[] ReadBytes() => Take((int)ReadUInt32()).ToArray();
}
