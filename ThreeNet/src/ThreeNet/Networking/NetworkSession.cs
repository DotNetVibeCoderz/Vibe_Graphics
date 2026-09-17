using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace ThreeNet.Networking;

/// <summary>Tuning of a <see cref="NetworkSession"/>.</summary>
public sealed record NetworkOptions
{
    /// <summary>Transform snapshots sent per second.</summary>
    public int TickRate { get; init; } = 20;

    /// <summary>How far behind real time remote transforms are shown, so there are two snapshots to blend.</summary>
    public TimeSpan InterpolationDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>A peer that sends nothing for this long is dropped.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Unacknowledged reliable messages are resent after this interval.</summary>
    public TimeSpan ResendInterval { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>A client gives up connecting after this long.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>A remote participant.</summary>
public sealed class NetworkPeer
{
    internal NetworkPeer(int id, string name, EndPoint endPoint)
    {
        Id = id;
        Name = name;
        EndPoint = endPoint;
    }

    public int Id { get; }

    public string Name { get; internal set; }

    public EndPoint EndPoint { get; }

    /// <summary>Smoothed round trip time measured from reliable acknowledgements.</summary>
    public TimeSpan RoundTripTime { get; internal set; }

    internal double LastHeard;
    internal uint NextSendSequence = 1;
    internal uint NextReceiveSequence = 1;
    internal readonly Dictionary<uint, (byte[] Packet, double SentAt, double FirstSent)> Unacked = [];
    internal readonly SortedDictionary<uint, (byte Channel, byte[] Payload)> OutOfOrder = [];
    internal double ClockOffset = double.NaN;
}

/// <summary>An application message received from a peer.</summary>
public readonly record struct NetworkMessage(NetworkPeer Sender, byte Channel, byte[] Data, bool Reliable);

/// <summary>
/// Small UDP session for multiplayer scenes: one host, any number of clients.
/// It replicates node transforms (snapshots at <see cref="NetworkOptions.TickRate"/>,
/// interpolated on receivers) and carries application messages, reliable and
/// ordered or unreliable. The host relays client traffic to the other clients.
/// Call <see cref="Update"/> once per frame on the thread that owns the scene;
/// socket I/O runs in the background.
/// </summary>
public sealed class NetworkSession : IDisposable
{
    internal const ushort Magic = 0x4E54; // "TN"
    internal const byte ProtocolVersion = 1;

    private enum PacketType : byte
    {
        Connect = 1,
        Accept = 2,
        Disconnect = 3,
        Heartbeat = 4,
        Snapshot = 5,
        Reliable = 6,
        Ack = 7,
        Unreliable = 8,
        PeerJoined = 9,
        PeerLeft = 10,
    }

    private readonly Socket _socket;
    private readonly NetworkOptions _options;
    private readonly ConcurrentQueue<(EndPoint From, byte[] Data)> _inbox = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<int, NetworkPeer> _peers = [];
    private readonly Dictionary<uint, ReplicatedNode> _replicated = [];
    private readonly EndPoint? _hostEndPoint;
    private readonly string _name;
    private double _lastSnapshot;
    private double _lastHeartbeat;
    private double _connectStarted;
    private int _nextPeerId = 2;
    private bool _disposed;

    private NetworkSession(Socket socket, NetworkOptions options, string name, EndPoint? host)
    {
        _socket = socket;
        _options = options;
        _name = name;
        _hostEndPoint = host;
        IsHost = host is null;
        LocalPeerId = IsHost ? 1 : 0;
        _ = Task.Run(ReceiveLoop);
    }

    /// <summary>Starts a host listening on <paramref name="port"/> (0 picks a free port, see <see cref="Port"/>).</summary>
    public static NetworkSession Host(int port, string name = "host", NetworkOptions? options = null)
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        IgnoreConnectionResets(socket);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        return new NetworkSession(socket, options ?? new NetworkOptions(), name, null);
    }

    /// <summary>Connects to a host; <see cref="Connected"/> fires from <see cref="Update"/> once accepted.</summary>
    public static NetworkSession Connect(string address, int port, string name = "client", NetworkOptions? options = null)
    {
        IPAddress ip = IPAddress.TryParse(address, out IPAddress? parsed)
            ? parsed
            : Dns.GetHostAddresses(address).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        IgnoreConnectionResets(socket);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        NetworkSession session = new(socket, options ?? new NetworkOptions(), name, new IPEndPoint(ip, port));
        session._connectStarted = session.Now;
        session.SendConnect();
        return session;
    }

    public bool IsHost { get; }

    /// <summary>1 for the host; assigned by the host for clients (0 until connected).</summary>
    public int LocalPeerId { get; private set; }

    public bool IsConnected => IsHost || LocalPeerId != 0;

    /// <summary>True when a client failed to connect or lost the host.</summary>
    public bool IsClosed { get; private set; }

    public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    /// <summary>Remote peers. On a client this includes the host (id 1) and the other clients.</summary>
    public IReadOnlyCollection<NetworkPeer> Peers => _peers.Values;

    public double Time => Now;

    public event Action<NetworkPeer>? PeerConnected;

    public event Action<NetworkPeer>? PeerDisconnected;

    /// <summary>Raised on a client when the host accepted it.</summary>
    public event Action? Connected;

    /// <summary>Raised on a client when connecting failed or the host went away.</summary>
    public event Action<string>? Closed;

    public event Action<NetworkMessage>? MessageReceived;

    private double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>
    /// Replicates <paramref name="node"/> under <paramref name="networkId"/>. The
    /// owner sends its transform; everybody else interpolates towards it. Use the
    /// same id on every peer.
    /// </summary>
    public ReplicatedNode Replicate(Node node, uint networkId, bool owned)
    {
        ArgumentNullException.ThrowIfNull(node);
        ReplicatedNode replicated = new(this, node, networkId, owned);
        _replicated[networkId] = replicated;
        return replicated;
    }

    public void StopReplicating(uint networkId) => _replicated.Remove(networkId);

    /// <summary>
    /// Sends application data. Clients send to the host, which relays to the
    /// other clients unless <paramref name="targetPeer"/> is the host (1).
    /// The host sends to everybody or to one peer.
    /// </summary>
    public void Send(ReadOnlySpan<byte> data, byte channel = 0, bool reliable = true, int? targetPeer = null)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("the session is not connected yet");
        }

        byte[] payload = data.ToArray();
        if (IsHost)
        {
            foreach (NetworkPeer peer in _peers.Values.Where(p => targetPeer is null || p.Id == targetPeer))
            {
                SendApplication(peer, LocalPeerId, 0, channel, payload, reliable);
            }
        }
        else if (_peers.TryGetValue(1, out NetworkPeer? host))
        {
            SendApplication(host, LocalPeerId, targetPeer ?? 0, channel, payload, reliable);
        }
    }

    /// <summary>Processes received packets, sends snapshots, resends and heartbeats, applies interpolation.</summary>
    public void Update()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        double now = Now;

        while (_inbox.TryDequeue(out (EndPoint From, byte[] Data) item))
        {
            try
            {
                Handle(item.From, item.Data, now);
            }
            catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentException or EndOfStreamException)
            {
                // Malformed packet: ignore it.
            }
        }

        if (!IsHost && LocalPeerId == 0 && !IsClosed)
        {
            if (now - _connectStarted > _options.ConnectTimeout.TotalSeconds)
            {
                Close("connection timed out");
            }
            else if (now - _lastHeartbeat > 0.25)
            {
                _lastHeartbeat = now;
                SendConnect();
            }
        }

        if (IsConnected)
        {
            foreach (NetworkPeer peer in _peers.Values.ToArray())
            {
                if (now - peer.LastHeard > _options.Timeout.TotalSeconds)
                {
                    DropPeer(peer, notify: true);
                    continue;
                }

                foreach ((uint sequence, (byte[] packet, double sentAt, double firstSent)) in peer.Unacked.ToArray())
                {
                    if (now - sentAt > _options.ResendInterval.TotalSeconds)
                    {
                        peer.Unacked[sequence] = (packet, now, firstSent);
                        SendRaw(peer.EndPoint, packet);
                    }
                }
            }

            if (now - _lastHeartbeat > 0.5)
            {
                _lastHeartbeat = now;
                foreach (NetworkPeer peer in DirectPeers())
                {
                    SendRaw(peer.EndPoint, Header(PacketType.Heartbeat, LocalPeerId).ToArray());
                }
            }

            if (now - _lastSnapshot >= 1.0 / Math.Max(1, _options.TickRate))
            {
                _lastSnapshot = now;
                SendSnapshot(now);
            }
        }

        double renderTime = now - _options.InterpolationDelay.TotalSeconds;
        foreach (ReplicatedNode replicated in _replicated.Values)
        {
            replicated.Apply(renderTime);
        }
    }

    // ------------------------------------------------------------ packets

    private static PacketWriter Header(PacketType type, int sender)
    {
        PacketWriter writer = new();
        writer.WriteUInt16(Magic);
        writer.WriteByte(ProtocolVersion);
        writer.WriteByte((byte)type);
        writer.WriteUInt16((ushort)sender);
        return writer;
    }

    private void SendConnect()
    {
        PacketWriter writer = Header(PacketType.Connect, 0);
        writer.WriteString(_name);
        SendRaw(_hostEndPoint!, writer.ToArray());
    }

    private IEnumerable<NetworkPeer> DirectPeers() =>
        IsHost ? _peers.Values : _peers.Values.Where(p => p.Id == 1);

    private void SendApplication(NetworkPeer peer, int origin, int target, byte channel, byte[] payload, bool reliable)
    {
        if (reliable)
        {
            uint sequence = peer.NextSendSequence++;
            PacketWriter writer = Header(PacketType.Reliable, LocalPeerId);
            writer.WriteUInt32(sequence);
            writer.WriteUInt16((ushort)origin);
            writer.WriteUInt16((ushort)target);
            writer.WriteByte(channel);
            writer.WriteBytes(payload);
            byte[] packet = writer.ToArray();
            double now = Now;
            peer.Unacked[sequence] = (packet, now, now);
            SendRaw(peer.EndPoint, packet);
        }
        else
        {
            PacketWriter writer = Header(PacketType.Unreliable, LocalPeerId);
            writer.WriteUInt16((ushort)origin);
            writer.WriteUInt16((ushort)target);
            writer.WriteByte(channel);
            writer.WriteBytes(payload);
            SendRaw(peer.EndPoint, writer.ToArray());
        }
    }

    /// <summary>Reliable control packet (join / leave notices) from the host.</summary>
    private void SendReliableControl(NetworkPeer peer, PacketType type, int subject, string name)
    {
        PacketWriter body = new();
        body.WriteByte((byte)type);
        body.WriteUInt16((ushort)subject);
        body.WriteString(name);
        SendApplication(peer, 1, peer.Id, 255, body.ToArray(), reliable: true);
    }

    private void SendSnapshot(double now)
    {
        ReplicatedNode[] owned = _replicated.Values.Where(r => r.IsOwned && r.Node.Scene is not null).ToArray();
        if (owned.Length == 0)
        {
            return;
        }

        PacketWriter writer = Header(PacketType.Snapshot, LocalPeerId);
        writer.WriteUInt16((ushort)LocalPeerId);
        writer.WriteDouble(now);
        writer.WriteUInt16((ushort)owned.Length);
        foreach (ReplicatedNode replicated in owned)
        {
            (Vector3 position, Quaternion rotation, Vector3 scale) = replicated.Capture();
            writer.WriteUInt32(replicated.NetworkId);
            writer.WriteVector3(position);
            writer.WriteQuaternion(rotation);
            writer.WriteVector3(scale);
        }

        byte[] packet = writer.ToArray();
        foreach (NetworkPeer peer in DirectPeers())
        {
            SendRaw(peer.EndPoint, packet);
        }
    }

    private void Handle(EndPoint from, byte[] data, double now)
    {
        PacketReader reader = new(data);
        if (reader.ReadUInt16() != Magic || reader.ReadByte() != ProtocolVersion)
        {
            return;
        }

        PacketType type = (PacketType)reader.ReadByte();
        int sender = reader.ReadUInt16();

        if (type == PacketType.Connect)
        {
            if (IsHost)
            {
                AcceptClient(from, reader.ReadString(), now);
            }

            return;
        }

        if (type == PacketType.Accept && !IsHost)
        {
            if (LocalPeerId == 0)
            {
                LocalPeerId = reader.ReadUInt16();
                string hostName = reader.ReadString();
                NetworkPeer host = new(1, hostName, from) { LastHeard = now };
                _peers[1] = host;
                Connected?.Invoke();
                PeerConnected?.Invoke(host);
            }

            return;
        }

        NetworkPeer? direct = IsHost
            ? _peers.Values.FirstOrDefault(p => p.EndPoint.Equals(from))
            : _peers.GetValueOrDefault(1);
        if (direct is null || (!IsHost && !from.Equals(direct.EndPoint)))
        {
            return;
        }

        direct.LastHeard = now;
        switch (type)
        {
            case PacketType.Disconnect:
                if (IsHost)
                {
                    DropPeer(direct, notify: true);
                }
                else
                {
                    Close("the host closed the session");
                }

                break;
            case PacketType.Heartbeat:
                break;
            case PacketType.Ack:
            {
                uint sequence = reader.ReadUInt32();
                if (direct.Unacked.Remove(sequence, out var entry))
                {
                    double rtt = now - entry.FirstSent;
                    direct.RoundTripTime = direct.RoundTripTime == TimeSpan.Zero
                        ? TimeSpan.FromSeconds(rtt)
                        : TimeSpan.FromSeconds((direct.RoundTripTime.TotalSeconds * 0.875) + (rtt * 0.125));
                }

                break;
            }

            case PacketType.Reliable:
            {
                uint sequence = reader.ReadUInt32();
                PacketWriter ack = Header(PacketType.Ack, LocalPeerId);
                ack.WriteUInt32(sequence);
                SendRaw(from, ack.ToArray());

                if (sequence < direct.NextReceiveSequence || direct.OutOfOrder.ContainsKey(sequence))
                {
                    break; // duplicate
                }

                direct.OutOfOrder[sequence] = (0, data[reader.Position..]);
                while (direct.OutOfOrder.Remove(direct.NextReceiveSequence, out var next))
                {
                    direct.NextReceiveSequence++;
                    DeliverApplication(direct, new PacketReader(next.Payload), reliable: true);
                }

                break;
            }

            case PacketType.Unreliable:
                DeliverApplication(direct, reader, reliable: false);
                break;
            case PacketType.Snapshot:
                HandleSnapshot(data, reader, now);
                break;
        }
    }

    private void AcceptClient(EndPoint from, string name, double now)
    {
        NetworkPeer? existing = _peers.Values.FirstOrDefault(p => p.EndPoint.Equals(from));
        NetworkPeer peer = existing ?? new NetworkPeer(_nextPeerId++, name, from);
        peer.LastHeard = now;

        PacketWriter accept = Header(PacketType.Accept, LocalPeerId);
        accept.WriteUInt16((ushort)peer.Id);
        accept.WriteString(_name);
        SendRaw(from, accept.ToArray());

        if (existing is not null)
        {
            return; // repeated connect while the accept was in flight
        }

        _peers[peer.Id] = peer;
        // Tell the newcomer about everybody else, and everybody else about the newcomer.
        foreach (NetworkPeer other in _peers.Values.Where(p => p.Id != peer.Id))
        {
            SendReliableControl(peer, PacketType.PeerJoined, other.Id, other.Name);
            SendReliableControl(other, PacketType.PeerJoined, peer.Id, peer.Name);
        }

        PeerConnected?.Invoke(peer);
    }

    private void DeliverApplication(NetworkPeer direct, PacketReader reader, bool reliable)
    {
        int origin = reader.ReadUInt16();
        int target = reader.ReadUInt16();
        byte channel = reader.ReadByte();
        byte[] payload = reader.ReadBytes();

        if (channel == 255 && !IsHost)
        {
            HandleControl(payload);
            return;
        }

        if (IsHost)
        {
            origin = direct.Id; // clients cannot spoof their origin
            // Relay to the other clients unless it was addressed to the host.
            if (target != 1)
            {
                foreach (NetworkPeer peer in _peers.Values.Where(p => p.Id != origin && (target == 0 || p.Id == target)))
                {
                    SendApplication(peer, origin, target, channel, payload, reliable);
                }
            }

            if (target is not (0 or 1))
            {
                return;
            }
        }

        NetworkPeer sender = _peers.GetValueOrDefault(origin) ?? direct;
        MessageReceived?.Invoke(new NetworkMessage(sender, channel, payload, reliable));
    }

    private void HandleControl(byte[] payload)
    {
        PacketReader reader = new(payload);
        PacketType type = (PacketType)reader.ReadByte();
        int subject = reader.ReadUInt16();
        string name = reader.ReadString();
        if (subject == LocalPeerId || subject == 1)
        {
            return;
        }

        if (type == PacketType.PeerJoined && !_peers.ContainsKey(subject))
        {
            // Relayed peers are reached through the host; their end point is the host's.
            NetworkPeer peer = new(subject, name, _hostEndPoint!) { LastHeard = double.PositiveInfinity };
            _peers[subject] = peer;
            PeerConnected?.Invoke(peer);
        }
        else if (type == PacketType.PeerLeft && _peers.Remove(subject, out NetworkPeer? left))
        {
            PeerDisconnected?.Invoke(left);
        }
    }

    private void HandleSnapshot(byte[] data, PacketReader reader, double now)
    {
        int origin = reader.ReadUInt16();
        double sentAt = reader.ReadDouble();
        int count = reader.ReadUInt16();

        if (IsHost)
        {
            // Forward client snapshots unchanged to the other clients.
            NetworkPeer? source = _peers.GetValueOrDefault(origin);
            foreach (NetworkPeer peer in _peers.Values.Where(p => p.Id != origin))
            {
                SendRaw(peer.EndPoint, data);
            }

            if (source is null)
            {
                return;
            }
        }

        NetworkPeer? owner = _peers.GetValueOrDefault(origin);
        if (owner is null)
        {
            return;
        }

        // Map the sender's clock onto ours using the smallest observed delay.
        double offset = now - sentAt;
        owner.ClockOffset = double.IsNaN(owner.ClockOffset) ? offset : Math.Min(owner.ClockOffset, offset);
        double localTime = sentAt + owner.ClockOffset;

        for (int i = 0; i < count; i++)
        {
            uint id = reader.ReadUInt32();
            Vector3 position = reader.ReadVector3();
            Quaternion rotation = reader.ReadQuaternion();
            Vector3 scale = reader.ReadVector3();
            if (_replicated.TryGetValue(id, out ReplicatedNode? replicated) && !replicated.IsOwned)
            {
                replicated.AddSnapshot(localTime, position, rotation, scale);
            }
        }
    }

    private void DropPeer(NetworkPeer peer, bool notify)
    {
        if (!IsHost)
        {
            if (peer.Id == 1)
            {
                Close("the host timed out");
            }

            return;
        }

        if (_peers.Remove(peer.Id) && notify)
        {
            foreach (NetworkPeer other in _peers.Values)
            {
                SendReliableControl(other, PacketType.PeerLeft, peer.Id, peer.Name);
            }

            PeerDisconnected?.Invoke(peer);
        }
    }

    private void Close(string reason)
    {
        if (IsClosed)
        {
            return;
        }

        IsClosed = true;
        foreach (NetworkPeer peer in _peers.Values.ToArray())
        {
            PeerDisconnected?.Invoke(peer);
        }

        _peers.Clear();
        LocalPeerId = 0;
        Closed?.Invoke(reason);
    }

    private void SendRaw(EndPoint to, byte[] packet)
    {
        try
        {
            _socket.SendTo(packet, to);
        }
        catch (SocketException)
        {
            // Unreachable peers are handled by timeouts.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ReceiveLoop()
    {
        byte[] buffer = new byte[65536];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_cancel.IsCancellationRequested)
        {
            try
            {
                SocketReceiveFromResult result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _cancel.Token);
                _inbox.Enqueue((result.RemoteEndPoint, buffer.AsSpan(0, result.ReceivedBytes).ToArray()));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // ICMP port unreachable and similar: keep listening.
            }
        }
    }

    private static void IgnoreConnectionResets(Socket socket)
    {
        if (OperatingSystem.IsWindows())
        {
            // SIO_UDP_CONNRESET: stop ICMP "port unreachable" from failing receives.
            const int SioUdpConnreset = -1744830452;
            socket.IOControl(SioUdpConnreset, [0, 0, 0, 0], null);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        byte[] goodbye = Header(PacketType.Disconnect, LocalPeerId).ToArray();
        foreach (NetworkPeer peer in DirectPeers().ToArray())
        {
            SendRaw(peer.EndPoint, goodbye);
        }

        _cancel.Cancel();
        _socket.Dispose();
        _cancel.Dispose();
    }
}
